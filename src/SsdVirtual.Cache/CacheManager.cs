using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.Database;

namespace SsdVirtual.Cache;

/// <summary>Reads bytes of a node from the cloud: (node, offset, destination, ct) → bytes read.</summary>
public delegate Task<int> RemoteReader(Node node, long offset, Memory<byte> destination, CancellationToken ct);

/// <summary>
/// Block cache of one account. Each file has a sparse "blob" in the cache directory whose length always
/// equals the logical file size, plus a bitmap of 1 MiB blocks that are valid. Blocks that are not valid are
/// fetched from the cloud on demand (with coalescing, de-duplication and read-ahead). Local writes go to the
/// blob and are uploaded by the sync engine. Dirty and pinned data is never evicted.
/// </summary>
public sealed class CacheManager : IDisposable
{
    public const int BlockSize = 1 << 20;
    private const int MaxRunBlocks = 16;

    private readonly MetadataStore _store;
    private readonly string _blobDir;
    private readonly string _accountId;
    private readonly RemoteReader _remote;
    private readonly SemaphoreSlim _transfers;
    private readonly Dictionary<long, CachedFile> _files = new();
    private readonly Timer _persistTimer;
    private long _usedBytes, _hits, _misses;

    public long QuotaBytes { get; set; }
    public long ReadAheadMaxBytes { get; set; } = 32L * 1024 * 1024;
    public string Directory { get; }
    public long UsedBytes => Interlocked.Read(ref _usedBytes);
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    private sealed class CachedFile
    {
        public long NodeId;
        public FileStream Stream = null!;
        public BlockBitmap Bits = new();
        /// <summary>Number of bytes at the start of the file whose content lives in the cloud.</summary>
        public long RemoteBacked;
        public long LastAccessTicks;
        public long AccountedBytes;
        public readonly object Lock = new();
        public readonly Dictionary<long, Task> InFlight = new();
        public int Refs;
        public bool Dirty;
        public bool Deleted;
        public long SequentialEnd = -1;
        public long ReadAhead = BlockSize;

        public bool Present(long block) => (long)block * BlockSize >= RemoteBacked || Bits.Get(block);
    }

    public CacheManager(MetadataStore store, string accountCacheDir, string accountId, RemoteReader remote, int parallelTransfers, long quotaBytes)
    {
        _store = store;
        _accountId = accountId;
        Directory = accountCacheDir;
        _blobDir = Path.Combine(accountCacheDir, "blobs");
        System.IO.Directory.CreateDirectory(_blobDir);
        _remote = remote;
        _transfers = new SemaphoreSlim(Math.Max(1, parallelTransfers));
        QuotaBytes = quotaBytes;
        List<CacheFileRecord> records;
        lock (_store.Gate) records = _store.AllCacheRecords();
        _usedBytes = records.Sum(r => r.CachedBytes);
        RemoveOrphanBlobs(records);
        _persistTimer = new Timer(_ => PersistDirty(), null, 2000, 2000);
    }

    private string BlobPath(long nodeId) => Path.Combine(_blobDir, nodeId.ToString("x") + ".blk");

    private void RemoveOrphanBlobs(List<CacheFileRecord> records)
    {
        var known = new HashSet<string>(records.Select(r => BlobPath(r.NodeId)), StringComparer.OrdinalIgnoreCase);
        foreach (var f in System.IO.Directory.EnumerateFiles(_blobDir, "*.blk"))
        {
            if (known.Contains(f)) continue;
            try { File.Delete(f); } catch (Exception ex) { Log.Warn("Could not delete orphan cache blob " + f, ex); }
        }
    }

    // ------------------------------------------------------------------ file handles

    private CachedFile Acquire(Node node)
    {
        CacheFileRecord? rec = null;
        lock (_files)
        {
            if (_files.TryGetValue(node.Id, out var existing)) { existing.Refs++; return existing; }
        }
        lock (_store.Gate) rec = _store.GetCacheRecord(node.Id);
        lock (_files)
        {
            if (_files.TryGetValue(node.Id, out var existing)) { existing.Refs++; return existing; }
            var path = BlobPath(node.Id);
            var blobExisted = File.Exists(path);
            var fs = OpenBlob(path);
            var cf = new CachedFile { NodeId = node.Id, Stream = fs, Refs = 1, LastAccessTicks = DateTime.UtcNow.Ticks };
            if (rec != null && blobExisted)
            {
                cf.Bits = new BlockBitmap(rec.Bitmap);
                cf.RemoteBacked = rec.Size;
                cf.AccountedBytes = rec.CachedBytes;
            }
            else
            {
                // No (valid) cache yet: everything that exists in the cloud must be fetched.
                if (rec != null) Interlocked.Add(ref _usedBytes, -rec.CachedBytes);
                cf.RemoteBacked = node.HasRemote ? node.Size : 0;
                cf.Dirty = true;
            }
            if (fs.Length != node.Size) fs.SetLength(node.Size);
            _files[node.Id] = cf;
            return cf;
        }
    }

    private void Release(CachedFile cf)
    {
        bool close;
        lock (_files)
        {
            close = --cf.Refs == 0;
            if (close) _files.Remove(cf.NodeId);
        }
        if (!close) return;
        if (cf.Deleted)
        {
            try { cf.Stream.Dispose(); File.Delete(BlobPath(cf.NodeId)); } catch (Exception ex) { Log.Debug("Blob delete failed: " + ex.Message); }
            return;
        }
        Persist(cf);
        try { cf.Stream.Dispose(); } catch { }
    }

    private static FileStream OpenBlob(string path)
    {
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.None);
        Sparse.TryMakeSparse(fs.SafeFileHandle);
        return fs;
    }

    /// <summary>Keeps the blob of an open file open between I/O requests (call <see cref="Unretain"/> on close).</summary>
    public void Retain(Node node)
    {
        if (!node.IsDirectory) Acquire(node);
    }

    public void Unretain(Node node)
    {
        if (node.IsDirectory) return;
        CachedFile? cf;
        lock (_files) _files.TryGetValue(node.Id, out cf);
        if (cf != null) Release(cf);
    }

    /// <summary>Creates an empty, fully local blob for a file created on the virtual drive.</summary>
    public void CreateLocal(Node node)
    {
        var cf = Acquire(node);
        try
        {
            lock (cf.Lock)
            {
                cf.RemoteBacked = 0;
                cf.Bits.Reset();
                cf.Stream.SetLength(node.Size);
                cf.Dirty = true;
            }
        }
        finally { Release(cf); }
    }

    // ------------------------------------------------------------------ persistence

    private void Persist(CachedFile cf)
    {
        CacheFileRecord rec;
        long delta;
        lock (cf.Lock)
        {
            if (!cf.Dirty || cf.Deleted) return;
            try { cf.Stream.Flush(true); } catch (ObjectDisposedException) { return; }
            var size = cf.Stream.Length;
            var blocks = (size + BlockSize - 1) / BlockSize;
            var remoteBlocks = (cf.RemoteBacked + BlockSize - 1) / BlockSize;
            long cachedBytes = Math.Min(size, cf.Bits.CountSet(Math.Min(blocks, remoteBlocks)) * (long)BlockSize) + Math.Max(0, size - cf.RemoteBacked);
            bool complete = true;
            for (long i = 0; i < Math.Min(blocks, remoteBlocks); i++) if (!cf.Bits.Get(i)) { complete = false; break; }
            rec = new CacheFileRecord
            {
                NodeId = cf.NodeId, Size = cf.RemoteBacked, Bitmap = cf.Bits.ToArray(), CachedBytes = cachedBytes,
                LastAccessTicks = cf.LastAccessTicks, Complete = complete,
            };
            delta = cachedBytes - cf.AccountedBytes;
            cf.AccountedBytes = cachedBytes;
            cf.Dirty = false;
        }
        Interlocked.Add(ref _usedBytes, delta);
        lock (_store.Gate)
        {
            if (_store.Get(cf.NodeId) == null) return; // node deleted meanwhile
            _store.SaveCacheRecord(rec);
        }
    }

    public void PersistDirty()
    {
        List<CachedFile> open;
        lock (_files) open = _files.Values.ToList();
        foreach (var cf in open)
        {
            try { Persist(cf); } catch (Exception ex) { Log.Warn("Cache persist failed", ex); }
        }
    }

    // ------------------------------------------------------------------ reads

    /// <summary>Reads from the cache, fetching missing blocks. Returns bytes copied into <paramref name="dest"/>.</summary>
    public int Read(Node node, long fileSize, long offset, Span<byte> dest, CancellationToken ct)
    {
        if (offset >= fileSize) return 0;
        var len = (int)Math.Min(dest.Length, fileSize - offset);
        var cf = Acquire(node);
        try
        {
            EnsureRangeAsync(cf, node, offset, len, ct, true).GetAwaiter().GetResult();
            int n = RandomAccess.Read(cf.Stream.SafeFileHandle, dest[..len], offset);
            if (n < len) dest[n..len].Clear();
            cf.LastAccessTicks = DateTime.UtcNow.Ticks;
            MaybeReadAhead(cf, node, fileSize, offset, len);
            return len;
        }
        finally { Release(cf); }
    }

    private void MaybeReadAhead(CachedFile cf, Node node, long fileSize, long offset, int len)
    {
        long start, count;
        lock (cf.Lock)
        {
            if (offset == cf.SequentialEnd || (offset < cf.SequentialEnd + BlockSize && offset > cf.SequentialEnd - BlockSize && cf.SequentialEnd > 0))
                cf.ReadAhead = Math.Min(Math.Max(ReadAheadMaxBytes, BlockSize), cf.ReadAhead * 2);
            else
                cf.ReadAhead = BlockSize;
            cf.SequentialEnd = offset + len;
            start = offset + len;
            count = Math.Min(cf.ReadAhead, fileSize - start);
            if (count <= 0 || cf.ReadAhead <= BlockSize) return;
            // Skip if the window is already cached.
            bool any = false;
            for (long b = start / BlockSize; b <= (start + count - 1) / BlockSize; b++)
                if (!cf.Present(b) && !cf.InFlight.ContainsKey(b)) { any = true; break; }
            if (!any) return;
        }
        var ra = Acquire(node);
        _ = Task.Run(async () =>
        {
            try { await EnsureRangeAsync(ra, node, start, (int)count, CancellationToken.None, false).ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("Read-ahead failed: " + ex.Message); }
            finally { Release(ra); }
        });
    }

    private async Task EnsureRangeAsync(CachedFile cf, Node node, long offset, long length, CancellationToken ct, bool countStats)
    {
        if (length <= 0) return;
        var first = offset / BlockSize;
        var last = (offset + length - 1) / BlockSize;
        var waits = new List<Task>();
        var runs = new List<(long Start, int Count, TaskCompletionSource Tcs)>();
        lock (cf.Lock)
        {
            long runStart = -1;
            int runLen = 0;
            void CloseRun()
            {
                if (runLen == 0) return;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                for (long b = runStart; b < runStart + runLen; b++) cf.InFlight[b] = tcs.Task;
                waits.Add(tcs.Task);
                runs.Add((runStart, runLen, tcs));
                runLen = 0;
            }
            for (long b = first; b <= last; b++)
            {
                if (cf.Present(b)) { if (countStats) Interlocked.Increment(ref _hits); CloseRun(); continue; }
                if (countStats) Interlocked.Increment(ref _misses);
                if (cf.InFlight.TryGetValue(b, out var t)) { CloseRun(); if (!waits.Contains(t)) waits.Add(t); continue; }
                if (runLen == 0) runStart = b;
                runLen++;
                if (runLen == MaxRunBlocks) CloseRun();
            }
            CloseRun();
        }
        foreach (var (start, count, tcs) in runs) _ = FetchRunAsync(cf, node, start, count, tcs, ct);
        if (waits.Count == 0) return;
        await Task.WhenAll(waits).ConfigureAwait(false);
    }

    private async Task FetchRunAsync(CachedFile cf, Node node, long startBlock, int blocks, TaskCompletionSource tcs, CancellationToken ct)
    {
        byte[]? buffer = null;
        try
        {
            await _transfers.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long off = startBlock * BlockSize;
                long end;
                lock (cf.Lock) end = Math.Min((startBlock + blocks) * (long)BlockSize, cf.RemoteBacked);
                if (end > off)
                {
                    var len = (int)(end - off);
                    buffer = ArrayPool<byte>.Shared.Rent(len);
                    int got = await _remote(node, off, buffer.AsMemory(0, len), ct).ConfigureAwait(false);
                    if (got < len) Array.Clear(buffer, got, len - got);
                    lock (cf.Lock)
                    {
                        var currentEnd = Math.Min(end, cf.RemoteBacked);
                        for (long b = startBlock; b < startBlock + blocks; b++)
                        {
                            long bs = b * BlockSize;
                            if (bs >= currentEnd || cf.Bits.Get(b)) continue;
                            long be = Math.Min(bs + BlockSize, currentEnd);
                            RandomAccess.Write(cf.Stream.SafeFileHandle, new ReadOnlySpan<byte>(buffer, (int)(bs - off), (int)(be - bs)), bs);
                            cf.Bits.Set(b);
                        }
                        cf.Dirty = true;
                    }
                }
            }
            finally { _transfers.Release(); }
            lock (cf.Lock) for (long b = startBlock; b < startBlock + blocks; b++) cf.InFlight.Remove(b);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            lock (cf.Lock) for (long b = startBlock; b < startBlock + blocks; b++) cf.InFlight.Remove(b);
            tcs.TrySetException(ex);
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Writes local data. Partially covered blocks that are not cached are fetched first.</summary>
    public void Write(Node node, long fileSizeBefore, long offset, ReadOnlySpan<byte> data, CancellationToken ct)
    {
        if (data.Length == 0) return;
        var cf = Acquire(node);
        try
        {
            long end = offset + data.Length;
            long firstBlock = offset / BlockSize, lastBlock = (end - 1) / BlockSize;
            // Only the first and the last block can be partially overwritten; their old content is needed.
            foreach (var b in firstBlock == lastBlock ? new[] { firstBlock } : new[] { firstBlock, lastBlock })
            {
                long bs = b * BlockSize;
                long be = Math.Min(bs + BlockSize, fileSizeBefore);
                if (bs >= fileSizeBefore) continue;
                bool fullyCovered = offset <= bs && end >= be;
                if (!fullyCovered) EnsureRangeAsync(cf, node, bs, 1, ct, false).GetAwaiter().GetResult();
            }
            lock (cf.Lock)
            {
                // Wait-free: any fetch still running for these blocks will skip them once the bits are set.
                RandomAccess.Write(cf.Stream.SafeFileHandle, data, offset);
                for (long b = firstBlock; b <= lastBlock; b++) if (b * (long)BlockSize < cf.RemoteBacked) cf.Bits.Set(b);
                cf.Dirty = true;
                cf.LastAccessTicks = DateTime.UtcNow.Ticks;
            }
        }
        finally { Release(cf); }
    }

    public void SetLength(Node node, long newSize, CancellationToken ct)
    {
        var cf = Acquire(node);
        try
        {
            lock (cf.Lock)
            {
                var old = cf.Stream.Length;
                if (newSize < cf.RemoteBacked) cf.RemoteBacked = newSize;
                if (newSize < old) cf.Bits.TruncateFrom((newSize + BlockSize - 1) / BlockSize);
                cf.Stream.SetLength(newSize);
                cf.Dirty = true;
            }
        }
        finally { Release(cf); }
    }

    public void Flush(Node node)
    {
        CachedFile? cf;
        lock (_files) _files.TryGetValue(node.Id, out cf);
        if (cf != null) Persist(cf);
    }

    // ------------------------------------------------------------------ whole-file operations

    public async Task EnsureFullyCachedAsync(Node node, long fileSize, Action<long>? progress, CancellationToken ct)
    {
        var cf = Acquire(node);
        try
        {
            const long slice = 64L * 1024 * 1024;
            for (long off = 0; off < fileSize; off += slice)
            {
                ct.ThrowIfCancellationRequested();
                await EnsureRangeAsync(cf, node, off, Math.Min(slice, fileSize - off), ct, false).ConfigureAwait(false);
                progress?.Invoke(Math.Min(fileSize, off + slice));
            }
            Persist(cf);
        }
        finally { Release(cf); }
    }

    public bool IsFullyCached(Node node, long fileSize) => CachedBytes(node, fileSize) >= fileSize;

    public long CachedBytes(Node node, long fileSize)
    {
        CachedFile? cf;
        lock (_files) _files.TryGetValue(node.Id, out cf);
        if (cf != null)
        {
            lock (cf.Lock)
            {
                long blocks = (fileSize + BlockSize - 1) / BlockSize;
                long n = 0;
                for (long b = 0; b < blocks; b++) if (cf.Present(b)) n += Math.Min(BlockSize, fileSize - b * BlockSize);
                return n;
            }
        }
        CacheFileRecord? rec;
        lock (_store.Gate) rec = _store.GetCacheRecord(node.Id);
        if (rec == null) return node.HasRemote ? 0 : fileSize;
        if (rec.Complete) return fileSize;
        var bits = new BlockBitmap(rec.Bitmap);
        long total = 0, nb = (fileSize + BlockSize - 1) / BlockSize;
        for (long b = 0; b < nb; b++)
            if (b * BlockSize >= rec.Size || bits.Get(b)) total += Math.Min(BlockSize, fileSize - b * BlockSize);
        return total;
    }

    /// <summary>Opens a read-only view of the blob for uploading. Caller must have called <see cref="EnsureFullyCachedAsync"/>.</summary>
    public FileStream OpenForUpload(Node node)
        => new(BlobPath(node.Id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);

    /// <summary>Called after a successful upload: the uploaded bytes are now the cloud copy.</summary>
    public void MarkUploaded(Node node, long uploadedSize)
    {
        var cf = Acquire(node);
        try
        {
            lock (cf.Lock)
            {
                // Every block was local at upload time; all of them stay valid.
                long blocks = (Math.Max(cf.Stream.Length, uploadedSize) + BlockSize - 1) / BlockSize;
                for (long b = 0; b < blocks; b++) cf.Bits.Set(b);
                cf.RemoteBacked = uploadedSize;
                cf.Dirty = true;
            }
            Persist(cf);
        }
        finally { Release(cf); }
    }

    /// <summary>Drops cached content because the cloud copy changed. Never call for dirty nodes.</summary>
    public void Invalidate(Node node, long newRemoteSize)
    {
        CachedFile? cf;
        lock (_files) _files.TryGetValue(node.Id, out cf);
        if (cf != null)
        {
            lock (cf.Lock)
            {
                cf.Bits.Reset();
                cf.RemoteBacked = newRemoteSize;
                cf.Stream.SetLength(0);
                cf.Stream.SetLength(newRemoteSize);
                cf.Dirty = true;
            }
            Persist(cf);
            return;
        }
        Remove(node.Id);
    }

    /// <summary>Deletes the cached content of a node (deleted file or eviction).</summary>
    public void Remove(long nodeId)
    {
        CachedFile? cf;
        lock (_files) _files.TryGetValue(nodeId, out cf);
        if (cf != null && cf.Refs > 0) return; // still in use; evicted later
        CacheFileRecord? rec;
        lock (_store.Gate)
        {
            rec = _store.GetCacheRecord(nodeId);
            _store.DeleteCacheRecord(nodeId);
        }
        if (rec != null) Interlocked.Add(ref _usedBytes, -rec.CachedBytes);
        try { File.Delete(BlobPath(nodeId)); } catch (Exception ex) { Log.Debug("Blob delete failed: " + ex.Message); }
    }

    /// <summary>Deletes cache data of nodes that were removed from the namespace (records already deleted).</summary>
    public void Forget(IEnumerable<Node> removed)
    {
        foreach (var n in removed)
        {
            if (n.IsDirectory) continue;
            CachedFile? cf;
            lock (_files) _files.TryGetValue(n.Id, out cf);
            if (cf != null)
            {
                lock (cf.Lock) { Interlocked.Add(ref _usedBytes, -cf.AccountedBytes); cf.AccountedBytes = 0; cf.Dirty = false; cf.Deleted = true; }
                continue; // closed (and deleted) by the last Release
            }
            var path = BlobPath(n.Id);
            try
            {
                if (File.Exists(path)) { Interlocked.Add(ref _usedBytes, -ApproxSize(path)); File.Delete(path); }
            }
            catch (Exception ex) { Log.Debug("Blob delete failed: " + ex.Message); }
        }
    }

    private static long ApproxSize(string path)
    {
        try { return Sparse.AllocatedSize(path); } catch { return 0; }
    }

    // ------------------------------------------------------------------ eviction

    /// <summary>Evicts least-recently-used clean, unpinned, closed files until usage is below 90% of quota.</summary>
    public int EnforceQuota()
    {
        if (QuotaBytes <= 0 || UsedBytes <= QuotaBytes) return 0;
        List<(CacheFileRecord Rec, bool Evictable)> candidates;
        lock (_store.Gate)
        {
            candidates = _store.AllCacheRecords()
                .Select(r => (r, _store.Get(r.NodeId) is { } n && !n.Pinned && !n.IsDirty && n.OpenHandles == 0 && n.HasRemote))
                .ToList();
        }
        var target = QuotaBytes * 9 / 10;
        int evicted = 0;
        foreach (var (rec, ok) in candidates.Where(c => c.Evictable).OrderBy(c => c.Rec.LastAccessTicks))
        {
            if (UsedBytes <= target) break;
            bool inUse;
            lock (_files) inUse = _files.ContainsKey(rec.NodeId);
            if (inUse) continue;
            Remove(rec.NodeId);
            evicted++;
        }
        if (evicted > 0) Log.Info($"[{_accountId}] cache eviction removed {evicted} files; now {Bytes.Format(UsedBytes)} of {Bytes.Format(QuotaBytes)}.");
        return evicted;
    }

    /// <summary>Removes every clean cached file (dirty data is kept).</summary>
    public int ClearClean()
    {
        List<long> ids;
        lock (_store.Gate)
            ids = _store.AllCacheRecords().Where(r => _store.Get(r.NodeId) is { } n && !n.IsDirty && n.HasRemote).Select(r => r.NodeId).ToList();
        int removed = 0;
        foreach (var id in ids)
        {
            bool inUse;
            lock (_files) inUse = _files.ContainsKey(id);
            if (inUse) continue;
            Remove(id);
            removed++;
        }
        return removed;
    }

    /// <summary>Verifies fully cached files against the cloud MD5 (when the provider supplies one).</summary>
    public (int Checked, int Skipped, List<string> Problems) Verify(Func<Node, string> describe, CancellationToken ct)
    {
        List<(Node Node, CacheFileRecord Rec)> items;
        lock (_store.Gate)
            items = _store.AllCacheRecords().Select(r => (_store.Get(r.NodeId), r)).Where(x => x.Item1 != null).Select(x => (x.Item1!, x.r)).ToList();
        int checkedCount = 0, skipped = 0;
        var problems = new List<string>();
        foreach (var (node, rec) in items)
        {
            ct.ThrowIfCancellationRequested();
            if (node.IsDirty || !rec.Complete || node.RemoteHash.Length != 32 || rec.Size != node.Size) { skipped++; continue; }
            bool inUse;
            lock (_files) inUse = _files.ContainsKey(node.Id);
            if (inUse) { skipped++; continue; }
            string md5;
            using (var fs = new FileStream(BlobPath(node.Id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
                md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
            checkedCount++;
            if (!string.Equals(md5, node.RemoteHash, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{describe(node)}: checksum mismatch, cached copy discarded.");
                Remove(node.Id);
            }
        }
        return (checkedCount, skipped, problems);
    }

    public (long Dirty, long Pinned, int Files) Breakdown()
    {
        lock (_store.Gate)
        {
            long dirty = 0, pinned = 0;
            var recs = _store.AllCacheRecords();
            foreach (var r in recs)
            {
                var n = _store.Get(r.NodeId);
                if (n == null) continue;
                if (n.IsDirty) dirty += r.CachedBytes;
                if (n.Pinned) pinned += r.CachedBytes;
            }
            return (dirty, pinned, recs.Count);
        }
    }

    public void Dispose()
    {
        _persistTimer.Dispose();
        List<CachedFile> open;
        lock (_files) { open = _files.Values.ToList(); _files.Clear(); }
        foreach (var cf in open)
        {
            try { Persist(cf); cf.Stream.Dispose(); } catch { }
        }
        _transfers.Dispose();
    }
}

internal static class Sparse
{
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    public static void TryMakeSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    /// <summary>Bytes actually allocated on disk (sparse aware).</summary>
    public static long AllocatedSize(string path)
    {
        if (!OperatingSystem.IsWindows()) return new FileInfo(path).Length;
        uint low = GetCompressedFileSizeW(path, out var high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return new FileInfo(path).Length;
        return ((long)high << 32) | low;
    }
}
