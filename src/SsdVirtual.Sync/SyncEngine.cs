using System.Collections.Concurrent;
using System.Text.Json;
using SsdVirtual.Cache;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.Database;

namespace SsdVirtual.Sync;

/// <summary>
/// Background synchronization of one volume: replays the operation journal (mkdir/move/delete) in order,
/// uploads modified files (resumable, verified with MD5 where the cloud provides it), pulls remote changes,
/// keeps pinned files fully cached and enforces the cache quota. Survives restarts because all state lives
/// in the volume's SQLite database.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly Volume _v;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _folderLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _uploads = new();
    private readonly ConcurrentDictionary<long, (int Failures, DateTime NextTry)> _uploadBackoff = new();
    private readonly ConcurrentDictionary<long, Task> _pins = new();
    private Task? _loop;
    private DateTime _lastPoll = DateTime.MinValue, _lastEvict = DateTime.MinValue, _lastInfo = DateTime.MinValue, _lastPinScan = DateTime.MinValue;
    private bool _processingOps;
    private volatile bool _forcePoll;

    public SyncStatus Status { get; private set; } = SyncStatus.Synced;
    public int PendingUploads { get; private set; }
    public int PendingOps { get; private set; }
    public int FailedItems { get; private set; }

    public SyncEngine(Volume volume)
    {
        _v = volume;
        _v.OpsChanged += Wake;
        _v.UploadReady += Wake;
        _v.PinRequested += () => { _lastPinScan = DateTime.MinValue; Wake(); };
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    public void Wake()
    {
        if (_wake.CurrentCount < 4) _wake.Release();
    }

    public void SyncNow()
    {
        _forcePoll = true;
        _lastPinScan = DateTime.MinValue;
        lock (_v.Store.Gate) _v.Store.ResetFailedOps();
        _uploadBackoff.Clear();
        Wake();
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Error($"[{_v.Account.DisplayName}] sync step failed", ex);
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var settings = _v.Settings;

        // 1. connectivity / account info
        if (_v.Connection == ConnectionState.AuthRequired) { UpdateStatus(); return; }
        if (_v.Connection != ConnectionState.Online || DateTime.UtcNow - _lastInfo > TimeSpan.FromMinutes(10))
        {
            var probeEvery = _v.Connection == ConnectionState.Online ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(20);
            if (DateTime.UtcNow - _lastInfo > probeEvery)
            {
                _lastInfo = DateTime.UtcNow;
                try
                {
                    await _v.RefreshAccountInfoAsync(ct).ConfigureAwait(false);
                    await _v.EnsureRootAsync(ct).ConfigureAwait(false);
                    await EnsureChangeCursorAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Log.Debug($"[{_v.Account.DisplayName}] probe failed: {ex.Message}");
                }
            }
        }
        if (_v.Connection != ConnectionState.Online) { UpdateStatus(); return; }

        // 2. ordered structural operations
        bool uploadsRunning = !_uploads.IsEmpty;
        if (!_v.IsPathBased || !uploadsRunning) await ProcessOpsAsync(ct).ConfigureAwait(false);

        // 3. uploads
        bool opsPending;
        lock (_v.Store.Gate) opsPending = _v.Store.CountOps() > 0;
        if (!(_v.IsPathBased && opsPending) && InUploadWindow(settings)) StartUploads(settings, ct);

        // 4. remote changes
        if (_forcePoll || DateTime.UtcNow - _lastPoll > TimeSpan.FromSeconds(Math.Max(15, settings.SyncIntervalSeconds)))
        {
            _forcePoll = false;
            _lastPoll = DateTime.UtcNow;
            try { await PollRemoteAsync(ct).ConfigureAwait(false); _v.LastSyncUtc = DateTime.UtcNow; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Debug($"[{_v.Account.DisplayName}] remote poll failed: {ex.Message}");
            }
        }

        // 5. pinned content
        if (DateTime.UtcNow - _lastPinScan > TimeSpan.FromSeconds(30)) { _lastPinScan = DateTime.UtcNow; StartPinWork(ct); }

        // 6. cache quota
        if (DateTime.UtcNow - _lastEvict > TimeSpan.FromSeconds(30))
        {
            _lastEvict = DateTime.UtcNow;
            try { _v.Cache.EnforceQuota(); } catch (Exception ex) { Log.Warn("Cache eviction failed", ex); }
        }
        UpdateStatus();
    }

    private static bool InUploadWindow(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.UploadWindow)) return true;
        var parts = s.UploadWindow.Split('-');
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out var from) || !TimeSpan.TryParse(parts[1], out var to)) return true;
        var now = DateTime.Now.TimeOfDay;
        return from <= to ? now >= from && now < to : now >= from || now < to;
    }

    // ------------------------------------------------------------------ operation journal

    private async Task ProcessOpsAsync(CancellationToken ct)
    {
        List<OpRecord> ops;
        lock (_v.Store.Gate) ops = _v.Store.Ops();
        if (ops.Count == 0) return;
        _processingOps = true;
        try
        {
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                if (op.NextAttemptTicks > DateTime.UtcNow.Ticks) return; // keep order: wait for this one
                try
                {
                    await ExecuteOpAsync(op, ct).ConfigureAwait(false);
                    _v.OpCompleted(op);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    op.Attempts++;
                    op.LastError = ex.Message;
                    op.State = op.Attempts >= 5 ? "RetryRequired" : "Pending";
                    var delay = TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, Math.Min(op.Attempts, 7))));
                    op.NextAttemptTicks = DateTime.UtcNow.Add(delay).Ticks;
                    lock (_v.Store.Gate) _v.Store.UpdateOp(op);
                    Log.Warn($"[{_v.Account.DisplayName}] {op.Kind} failed (attempt {op.Attempts}): {ex.Message}");
                    return;
                }
            }
        }
        finally { _processingOps = false; }
    }

    private async Task ExecuteOpAsync(OpRecord op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case "mkdir":
            {
                Node? n;
                lock (_v.Store.Gate) n = _v.Store.Get(op.NodeId);
                if (n == null || n.Removed || n.HasRemote) return;
                await EnsureRemoteFolderAsync(n, ct).ConfigureAwait(false);
                return;
            }
            case "move":
            {
                await MoveAsync(op.NodeId, ct).ConfigureAwait(false);
                return;
            }
            case "delete":
            {
                var data = JsonSerializer.Deserialize<Volume.DeleteOpData>(op.Data)!;
                string path;
                lock (_v.Store.Gate)
                {
                    var parent = data.RemoteParentNodeId != 0 ? _v.Store.Get(data.RemoteParentNodeId) : null;
                    path = parent != null && !parent.Removed ? Volume.Join(_v.RemotePath(parent), data.RemoteName) : data.RemotePath;
                }
                await _v.RemoteAsync(c => _v.Provider.DeleteAsync(new RemoteRef(data.RemoteId, path), data.IsDirectory, _v.Account.DeleteToCloudTrash, c), ct).ConfigureAwait(false);
                return;
            }
            default:
                Log.Warn("Unknown journal operation " + op.Kind + " dropped.");
                return;
        }
    }

    private async Task MoveAsync(long nodeId, CancellationToken ct)
    {
        Node? n, from, to;
        RemoteRef itemRef, fromRef;
        lock (_v.Store.Gate)
        {
            n = _v.Store.Get(nodeId);
            if (n == null || n.Removed || !n.HasRemote) return;
            from = _v.Store.RemoteParentOf(n);
            to = n.Parent;
            if (from == null || to == null) return;
            if (from == to && n.RemoteName == n.Name) return; // already in sync
            itemRef = _v.RefOf(n);
            fromRef = _v.RefOf(from);
        }
        await EnsureRemoteFolderAsync(to, ct).ConfigureAwait(false);
        RemoteRef toRef;
        string toPath, newName;
        bool isDir;
        lock (_v.Store.Gate)
        {
            toRef = _v.RefOf(to);
            toPath = _v.RemotePath(to);
            newName = n.Name;
            isDir = n.IsDirectory;
        }
        CloudItem moved;
        try
        {
            moved = await _v.RemoteAsync(c => _v.Provider.MoveAsync(itemRef, fromRef, toRef, toPath, newName, isDir, c), ct).ConfigureAwait(false);
        }
        catch (CloudException ex) when (ex.Kind == CloudErrorKind.NotFound)
        {
            // The cloud copy vanished: treat the item as new so it is uploaded again.
            Log.Warn($"[{_v.Account.DisplayName}] move source missing in cloud; re-uploading {newName}.");
            lock (_v.Store.Gate)
            {
                foreach (var d in _v.Store.Descendants(n))
                {
                    _v.Store.SetRemoteId(d, "");
                    d.RemoteParentId = 0;
                    if (!d.IsDirectory) d.UploadedVersion = Math.Min(d.UploadedVersion, d.Version - 1);
                    else _v.Store.EnqueueOp("mkdir", d.Id, "");
                    _v.Store.Save(d);
                }
            }
            return;
        }
        lock (_v.Store.Gate)
        {
            if (n.Removed) return;
            var idIsPath = itemRef.Id == itemRef.Path;
            n.RemoteParentId = to.Id;
            n.RemoteName = moved.Name.Length > 0 ? moved.Name : newName;
            if (moved.Id.Length > 0) _v.Store.SetRemoteId(n, moved.Id);
            if (idIsPath)
            {
                // Providers that identify items by path: descendants got new identities.
                foreach (var d in _v.Store.Descendants(n).Skip(1))
                    if (d.HasRemote) _v.Store.SetRemoteId(d, _v.RemotePath(d));
                _v.Store.SaveMany(_v.Store.Descendants(n));
            }
            else _v.Store.Save(n);
        }
    }

    /// <summary>Creates the cloud folders for <paramref name="dir"/> and every missing ancestor.</summary>
    public async Task EnsureRemoteFolderAsync(Node dir, CancellationToken ct)
    {
        if (dir.IsRoot) { await _v.EnsureRootAsync(ct).ConfigureAwait(false); return; }
        lock (_v.Store.Gate) if (dir.HasRemote) return;
        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var chain = new List<Node>();
            lock (_v.Store.Gate)
                for (var c = dir; c != null && !c.IsRoot && !c.HasRemote; c = c.Parent) chain.Insert(0, c);
            await _v.EnsureRootAsync(ct).ConfigureAwait(false);
            foreach (var folder in chain)
            {
                Node parent;
                RemoteRef parentRef;
                string parentPath, name;
                lock (_v.Store.Gate)
                {
                    if (folder.Removed) throw new IOException("Folder was deleted.");
                    if (folder.HasRemote) continue;
                    parent = folder.Parent!;
                    parentRef = _v.RefOf(parent);
                    parentPath = _v.RemotePath(parent);
                    name = folder.Name;
                }
                CloudItem created;
                try
                {
                    created = await _v.RemoteAsync(c => _v.Provider.CreateFolderAsync(parentRef, parentPath, name, c), ct).ConfigureAwait(false);
                }
                catch (CloudException ex) when (ex.Kind == CloudErrorKind.AlreadyExists)
                {
                    var siblings = await _v.RemoteAsync(c => _v.Provider.ListAsync(parentRef, c), ct).ConfigureAwait(false);
                    created = siblings.FirstOrDefault(s => s.IsDirectory && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                              ?? throw ex;
                }
                lock (_v.Store.Gate)
                {
                    _v.Store.SetRemoteId(folder, created.Id.Length > 0 ? created.Id : created.Path);
                    folder.RemoteParentId = parent.Id;
                    folder.RemoteName = created.Name.Length > 0 ? created.Name : name;
                    _v.Store.Save(folder);
                }
            }
        }
        finally { _folderLock.Release(); }
    }

    // ------------------------------------------------------------------ uploads

    private void StartUploads(AppSettings settings, CancellationToken ct)
    {
        List<Node> candidates;
        var quiet = _v.Account.WritePolicy == CacheWritePolicy.WriteThrough ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, settings.UploadQuietSeconds));
        var now = DateTime.UtcNow;
        lock (_v.Store.Gate)
        {
            candidates = _v.Store.DirtyFiles()
                .Where(n => n.OpenHandles == 0 && now.Ticks - n.LastLocalWriteTicks >= quiet.Ticks && !_uploads.ContainsKey(n.Id))
                .OrderBy(n => n.LastLocalWriteTicks)
                .ToList();
        }
        foreach (var n in candidates)
        {
            if (_uploads.Count >= Math.Max(1, settings.MaxParallelTransfers)) break;
            if (_uploadBackoff.TryGetValue(n.Id, out var b) && b.NextTry > now) continue;
            var node = n;
            var task = Task.Run(() => UploadAsync(node, ct));
            _uploads[node.Id] = task;
            task.ContinueWith(_ => { _uploads.TryRemove(node.Id, out Task? _); Wake(); }, TaskScheduler.Default);
        }
    }

    private sealed class SessionStore : IUploadSessionStore
    {
        private readonly MetadataStore _s;
        private readonly long _node, _version;
        public SessionStore(MetadataStore s, long node, long version) { _s = s; _node = node; _version = version; }
        public string? Load() { lock (_s.Gate) { var x = _s.GetUploadSession(_node); return x is { } v && v.Version == _version ? v.Session : null; } }
        public void Save(string session) { lock (_s.Gate) if (_s.Get(_node) != null) _s.SetUploadSession(_node, _version, session); }
        public void Clear() { lock (_s.Gate) _s.DeleteUploadSession(_node); }
    }

    private async Task UploadAsync(Node node, CancellationToken ct)
    {
        long version, size;
        string path;
        Node parent;
        bool existing;
        DateTime modified;
        lock (_v.Store.Gate)
        {
            if (node.Removed || !node.IsDirty || node.Parent == null) return;
            version = node.Version;
            size = node.Size;
            path = _v.Store.PathOf(node);
            parent = node.Parent;
            existing = node.HasRemote;
            modified = FileTime.ToUtc(node.LastWriteTime);
        }
        var entry = TransferRegistry.Begin(_v.Account.Id, "Upload", path, size);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Cancellation.Token);
        try
        {
            entry.State = "Preparing";
            // A modified cloud file may still have blocks that were never downloaded: fetch them first.
            await _v.Cache.EnsureFullyCachedAsync(node, size, null, linked.Token).ConfigureAwait(false);
            if (!existing) await EnsureRemoteFolderAsync(parent, linked.Token).ConfigureAwait(false);

            RemoteRef parentRef, nodeRef;
            string parentPath, name;
            lock (_v.Store.Gate)
            {
                if (node.Removed) { TransferRegistry.Finish(entry, new OperationCanceledException()); return; }
                parentRef = _v.RefOf(node.Parent!);
                parentPath = _v.RemotePath(node.Parent!);
                nodeRef = _v.RefOf(node);
                name = node.Name;
                existing = node.HasRemote;
                if (!existing) parent = node.Parent!;
            }

            entry.State = "Running";
            CloudItem item;
            await using (var content = _v.Cache.OpenForUpload(node))
            {
                var request = new UploadRequest
                {
                    Parent = parentRef,
                    ParentPath = parentPath,
                    Name = name,
                    Existing = existing ? nodeRef : null,
                    Content = content,
                    Length = size,
                    ModifiedUtc = modified,
                    Session = new SessionStore(_v.Store, node.Id, version),
                    Progress = done => entry.BytesDone = done,
                };
                item = await _v.RemoteAsync(c => _v.Provider.UploadAsync(request, c), linked.Token).ConfigureAwait(false);
            }

            bool unchanged;
            lock (_v.Store.Gate) unchanged = node.Version == version && !node.Removed;
            if (unchanged && item.Md5.Length == 32 && (_v.Provider.Capabilities & ProviderCapabilities.ContentMd5) != 0)
            {
                var local = _v.Md5OfBlob(node, size);
                if (!string.Equals(local, item.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Checksum mismatch after upload (local {local}, cloud {item.Md5}); will retry.");
            }

            lock (_v.Store.Gate)
            {
                if (node.Removed)
                {
                    // Deleted locally while uploading: remove the copy we just created.
                    _v.Store.EnqueueOp("delete", 0, JsonSerializer.Serialize(new Volume.DeleteOpData
                    {
                        RemoteId = item.Id, RemotePath = item.Path.Length > 0 ? item.Path : Volume.Join(parentPath, name), IsDirectory = false,
                    }));
                }
                else
                {
                    // The change feed may already have added the new cloud item as a separate node: drop that duplicate.
                    var twin = item.Id.Length > 0 ? _v.Store.FindByRemoteId(item.Id) : null;
                    if (twin != null && twin != node && !twin.IsDirty) _v.Store.Remove(twin);
                    if (item.Id.Length > 0) _v.Store.SetRemoteId(node, item.Id);
                    else if (!node.HasRemote) _v.Store.SetRemoteId(node, item.Path);
                    if (!existing)
                    {
                        node.RemoteParentId = parent.Id;
                        node.RemoteName = item.Name.Length > 0 ? item.Name : name;
                    }
                    node.RemoteHash = item.Md5;
                    node.RemoteModifiedTicks = item.ModifiedUtc.Ticks;
                    if (node.Version == version) node.UploadedVersion = version;
                    _v.Store.Save(node);
                }
            }
            if (unchanged) _v.Cache.MarkUploaded(node, size);
            _uploadBackoff.TryRemove(node.Id, out _);
            TransferRegistry.Finish(entry);
            Log.Info($"[{_v.Account.DisplayName}] uploaded {path} ({Bytes.Format(size)}).");
        }
        catch (Exception ex)
        {
            TransferRegistry.Finish(entry, ex);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) return;
            var failures = _uploadBackoff.TryGetValue(node.Id, out var b) ? b.Failures + 1 : 1;
            var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Min(failures, 8))));
            _uploadBackoff[node.Id] = (failures, DateTime.UtcNow + delay);
            Log.Warn($"[{_v.Account.DisplayName}] upload of {path} failed (attempt {failures}, retry in {delay.TotalSeconds:0}s): {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ remote changes

    private async Task EnsureChangeCursorAsync(CancellationToken ct)
    {
        if ((_v.Provider.Capabilities & ProviderCapabilities.ChangeFeed) == 0) return;
        string? cursor;
        lock (_v.Store.Gate) cursor = _v.Store.GetValue("changes_cursor");
        if (!string.IsNullOrEmpty(cursor)) return;
        var c = await _v.RemoteAsync(x => _v.Provider.GetChangeCursorAsync(x), ct).ConfigureAwait(false);
        lock (_v.Store.Gate) _v.Store.SetValue("changes_cursor", c);
    }

    private async Task PollRemoteAsync(CancellationToken ct)
    {
        if ((_v.Provider.Capabilities & ProviderCapabilities.ChangeFeed) != 0)
        {
            await EnsureChangeCursorAsync(ct).ConfigureAwait(false);
            string cursor;
            lock (_v.Store.Gate) cursor = _v.Store.GetValue("changes_cursor") ?? "";
            if (cursor.Length == 0) return;
            var batch = await _v.RemoteAsync(c => _v.Provider.GetChangesAsync(cursor, c), ct).ConfigureAwait(false);
            if (batch.Changed.Count > 0 || batch.RemovedIds.Count > 0) _v.ApplyChanges(batch);
            lock (_v.Store.Gate) _v.Store.SetValue("changes_cursor", batch.NextCursor);
            return;
        }

        // No change feed: re-list folders that were looked at recently.
        var recent = _v.RecentlyEnumerated.Where(kv => kv.Value > DateTime.UtcNow.AddMinutes(-15).Ticks).Select(kv => kv.Key).ToList();
        var stale = DateTime.UtcNow.AddSeconds(-Math.Max(15, _v.Settings.SyncIntervalSeconds)).Ticks;
        int refreshed = 0;
        foreach (var id in recent)
        {
            Node? dir;
            lock (_v.Store.Gate) dir = _v.Store.Get(id);
            if (dir == null || !dir.IsDirectory || dir.ChildrenLoadedTicks == 0 || dir.ChildrenLoadedTicks > stale) continue;
            await _v.ListFromCloudAsync(dir, ct).ConfigureAwait(false);
            if (++refreshed >= 20) break;
        }
    }

    // ------------------------------------------------------------------ pinning

    private void StartPinWork(CancellationToken ct)
    {
        if (_pins.Count >= 2) return;
        List<Node> dirs, files;
        lock (_v.Store.Gate)
        {
            var pinned = _v.Store.AllNodes().Where(n => n.Pinned && !n.Removed).ToList();
            dirs = pinned.Where(n => n.IsDirectory && n.ChildrenLoadedTicks == 0).Take(20).ToList();
            files = pinned.Where(n => !n.IsDirectory && n.HasRemote).ToList();
        }
        foreach (var d in dirs)
        {
            try { _v.EnsureListed(d, ct); } catch (Exception ex) { Log.Debug("Pin listing failed: " + ex.Message); }
        }
        foreach (var f in files)
        {
            if (_pins.Count >= 2) break;
            if (_pins.ContainsKey(f.Id)) continue;
            long size;
            lock (_v.Store.Gate) size = f.Size;
            if (_v.Cache.IsFullyCached(f, size)) continue;
            var node = f;
            var t = Task.Run(async () =>
            {
                string path = _v.PathOf(node);
                var entry = TransferRegistry.Begin(_v.Account.Id, "Pin", path, size);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Cancellation.Token);
                try
                {
                    await _v.Cache.EnsureFullyCachedAsync(node, size, done => entry.BytesDone = done, linked.Token).ConfigureAwait(false);
                    TransferRegistry.Finish(entry);
                }
                catch (Exception ex)
                {
                    TransferRegistry.Finish(entry, ex);
                    Log.Debug($"Pin download of {path} failed: {ex.Message}");
                }
            });
            _pins[node.Id] = t;
            t.ContinueWith(_ => { _pins.TryRemove(node.Id, out Task? _); Wake(); }, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------ status

    private void UpdateStatus()
    {
        int dirty, ops, failedOps;
        bool conflicts;
        lock (_v.Store.Gate)
        {
            dirty = _v.Store.DirtyFiles().Count;
            var all = _v.Store.Ops();
            ops = all.Count;
            failedOps = all.Count(o => o.State == "RetryRequired");
            conflicts = _v.Conflicts().Any(c => c.Utc > DateTime.UtcNow.AddDays(-7));
        }
        PendingUploads = dirty;
        PendingOps = ops;
        FailedItems = failedOps + _uploadBackoff.Count(b => b.Value.Failures >= 5);
        Status = _v.Connection switch
        {
            ConnectionState.Offline => SyncStatus.Offline,
            ConnectionState.AuthRequired or ConnectionState.Error => SyncStatus.Error,
            _ when !_uploads.IsEmpty => SyncStatus.Uploading,
            _ when !_pins.IsEmpty => SyncStatus.Downloading,
            _ when _processingOps => SyncStatus.Processing,
            _ when FailedItems > 0 => SyncStatus.RetryRequired,
            _ when conflicts => SyncStatus.Conflict,
            _ when dirty > 0 || ops > 0 => SyncStatus.PendingUpload,
            _ => SyncStatus.Synced,
        };
    }

    /// <summary>Waits until every local change has been uploaded (used by tests and before uninstall).</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Wake();
            await Task.Delay(250).ConfigureAwait(false);
            UpdateStatus();
            if (PendingUploads == 0 && PendingOps == 0 && _uploads.IsEmpty) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        try { Task.WaitAll(_uploads.Values.Concat(_pins.Values).ToArray(), TimeSpan.FromSeconds(10)); } catch { }
        _stop.Dispose();
    }
}
