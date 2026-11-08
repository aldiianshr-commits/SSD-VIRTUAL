using System.Security.Cryptography;
using System.Text.Json;
using SsdVirtual.Cache;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.Database;

namespace SsdVirtual.Sync;

public enum RemoteChangeKind { Added, Removed, Modified, Renamed }

public readonly record struct RemoteChange(RemoteChangeKind Kind, string Path, string? NewPath = null);

/// <summary>
/// The namespace and data of one virtual drive: combines the metadata tree, the block cache and the cloud
/// provider. The filesystem layer calls into this class; every local change is recorded in the persistent
/// journal before the call returns, so nothing is lost when the network or the process goes away.
/// </summary>
public sealed class Volume : IDisposable
{
    public AccountConfig Account { get; }
    public MetadataStore Store { get; }
    public ICloudProvider Provider { get; }
    public CacheManager Cache { get; }
    public AppSettings Settings { get; set; }

    private readonly HashSet<long> _nodesWithOps = new();
    private readonly HashSet<Node> _pendingMetaSaves = new();
    private readonly Timer _metaTimer;
    private DateTime _offlineSince = DateTime.MinValue;
    private DateTime _lastProbe = DateTime.MinValue;
    private string _rootPath = "/";

    public ConnectionState Connection { get; private set; } = ConnectionState.Unknown;
    public CloudAccountInfo? Info { get; private set; }
    public string LastError { get; private set; } = "";
    public DateTime? LastSyncUtc { get; internal set; }
    public bool RootResolved { get; private set; }
    public bool IsPathBased => (Provider.Capabilities & ProviderCapabilities.PathBased) != 0;

    /// <summary>Raised (outside locks) when the cloud changed something, so Explorer can be refreshed.</summary>
    public event Action<IReadOnlyList<RemoteChange>>? RemoteChanged;
    public event Action? StatusChanged;

    public Volume(AccountConfig account, AppSettings settings, ICloudProvider provider, string accountCacheDir)
    {
        Account = account;
        Settings = settings;
        Provider = provider;
        Store = new MetadataStore(Path.Combine(accountCacheDir, "metadata.db"));
        RecoverAfterCrash(accountCacheDir);
        Cache = new CacheManager(Store, accountCacheDir, account.Id, ReadRemoteAsync, Math.Max(1, settings.MaxParallelTransfers), account.CacheQuotaBytes)
        {
            ReadAheadMaxBytes = Math.Max(1, settings.ReadAheadMaxMiB) * Bytes.MiB,
        };
        lock (Store.Gate)
        {
            foreach (var op in Store.Ops()) if (op.NodeId != 0) _nodesWithOps.Add(op.NodeId);
            var info = Store.GetValue("account_info");
            if (info != null) { try { Info = JsonSerializer.Deserialize<CloudAccountInfo>(info); } catch { } }
            _rootPath = Store.GetValue("root_path") ?? "/";
            RootResolved = Store.Root.HasRemote;
        }
        _metaTimer = new Timer(_ => FlushMetadata(), null, 2000, 2000);
    }

    /// <summary>
    /// After an unexpected shutdown, a dirty file's blob may be longer than the size last persisted (appends
    /// that were written but whose metadata was not saved yet). Keep the data the application wrote.
    /// </summary>
    private void RecoverAfterCrash(string dir)
    {
        lock (Store.Gate)
        {
            int fixedCount = 0, lost = 0;
            foreach (var n in Store.DirtyFiles())
            {
                var blob = Path.Combine(dir, "blobs", n.Id.ToString("x") + ".blk");
                if (!File.Exists(blob))
                {
                    if (n.HasRemote)
                    {
                        // The local copy is gone (cache folder deleted by hand): fall back to the cloud copy.
                        n.UploadedVersion = n.Version;
                        Store.Save(n);
                        lost++;
                    }
                    continue;
                }
                var len = new FileInfo(blob).Length;
                if (len > n.Size) { n.Size = len; Store.Save(n); fixedCount++; }
            }
            if (fixedCount > 0) Log.Warn($"[{Account.DisplayName}] recovered size of {fixedCount} files after unexpected shutdown.");
            if (lost > 0) Log.Error($"[{Account.DisplayName}] {lost} modified files had no local data (cache deleted); reverted to cloud copy.");
        }
    }

    // ------------------------------------------------------------------ connectivity

    public void ReportSuccess()
    {
        if (Connection is ConnectionState.Online) return;
        Connection = ConnectionState.Online;
        LastError = "";
        Log.Info($"[{Account.DisplayName}] online.");
        StatusChanged?.Invoke();
    }

    public void ReportFailure(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        LastError = ex.Message;
        if (ex is CloudException { Kind: CloudErrorKind.Auth })
        {
            if (Connection != ConnectionState.AuthRequired) Log.Warn($"[{Account.DisplayName}] authentication required: {ex.Message}");
            Connection = ConnectionState.AuthRequired;
            StatusChanged?.Invoke();
        }
        else if (CloudException.IsOffline(ex))
        {
            if (Connection != ConnectionState.Offline)
            {
                _offlineSince = DateTime.UtcNow;
                Log.Warn($"[{Account.DisplayName}] offline: {ex.Message}");
            }
            Connection = ConnectionState.Offline;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>When offline, fail fast instead of waiting for network timeouts (a probe is allowed every 20 s).</summary>
    private void ThrowIfUnreachable()
    {
        if (Connection == ConnectionState.AuthRequired)
            throw new CloudException(CloudErrorKind.Auth, "Account needs to be reconnected.");
        if (Connection == ConnectionState.Offline && DateTime.UtcNow - _lastProbe < TimeSpan.FromSeconds(20))
            throw new CloudException(CloudErrorKind.Network, "Cloud is offline.");
        if (Connection == ConnectionState.Offline) _lastProbe = DateTime.UtcNow;
    }

    public async Task<T> RemoteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct, bool probe = false)
    {
        if (probe) { if (Connection == ConnectionState.AuthRequired) ThrowIfUnreachable(); }
        else ThrowIfUnreachable();
        try
        {
            var result = await call(ct).ConfigureAwait(false);
            ReportSuccess();
            return result;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            throw;
        }
    }

    public Task RemoteAsync(Func<CancellationToken, Task> call, CancellationToken ct)
        => RemoteAsync<bool>(async c => { await call(c).ConfigureAwait(false); return true; }, ct);

    public async Task RefreshAccountInfoAsync(CancellationToken ct)
    {
        Info = await RemoteAsync(c => Provider.GetAccountInfoAsync(c), ct, probe: true).ConfigureAwait(false);
        lock (Store.Gate) Store.SetValue("account_info", JsonSerializer.Serialize(Info));
        StatusChanged?.Invoke();
    }

    /// <summary>Resolves the cloud folder of the drive root. Safe to call repeatedly.</summary>
    public async Task EnsureRootAsync(CancellationToken ct)
    {
        if (RootResolved) return;
        var root = await RemoteAsync(c => Provider.ResolveRootAsync(Account.RemoteRoot, c), ct).ConfigureAwait(false);
        lock (Store.Gate)
        {
            Store.SetRemoteId(Store.Root, root.Id.Length > 0 ? root.Id : "/");
            _rootPath = root.Path.Length > 0 ? root.Path : "/";
            Store.SetValue("root_path", _rootPath);
            Store.Save(Store.Root);
            RootResolved = true;
        }
    }

    // ------------------------------------------------------------------ remote references

    private static string JoinRemote(string a, string b) => a == "/" ? "/" + b.TrimStart('/') : a.TrimEnd('/') + "/" + b.TrimStart('/');

    /// <summary>Full cloud path of a node (call under Gate).</summary>
    public string RemotePath(Node n)
    {
        var rel = Store.RemotePathOf(n);
        return rel == "/" ? _rootPath : JoinRemote(_rootPath, rel);
    }

    /// <summary>Cloud reference of a node (call under Gate).</summary>
    public RemoteRef RefOf(Node n) => new(n.RemoteId, RemotePath(n));

    private Task<int> ReadRemoteAsync(Node node, long offset, Memory<byte> dest, CancellationToken ct)
    {
        RemoteRef r;
        lock (Store.Gate) r = RefOf(node);
        if (r.Id.Length == 0 && !IsPathBased) throw new CloudException(CloudErrorKind.NotFound, "File has no cloud copy.");
        return RemoteAsync(c => Provider.ReadAsync(r, offset, dest, offset + dest.Length, c), ct);
    }

    // ------------------------------------------------------------------ namespace: lookup & listing

    public string PathOf(Node n) { lock (Store.Gate) return Store.PathOf(n); }

    /// <summary>Resolves a path, listing directories from the cloud the first time they are traversed.</summary>
    public Node? Lookup(string path, CancellationToken ct = default)
    {
        Node node;
        lock (Store.Gate) node = Store.Root;
        foreach (var part in VirtualPath.Split(path))
        {
            if (!node.IsDirectory) return null;
            EnsureListed(node, ct);
            lock (Store.Gate)
            {
                var child = Store.Child(node, part);
                if (child == null) return null;
                node = child;
            }
        }
        return node;
    }

    public bool IsListed(Node dir) { lock (Store.Gate) return dir.ChildrenLoadedTicks != 0; }

    /// <summary>Makes sure the children of <paramref name="dir"/> are known. Offline, whatever is cached is used.</summary>
    public void EnsureListed(Node dir, CancellationToken ct = default, bool force = false)
    {
        bool needed;
        lock (Store.Gate) needed = dir.IsDirectory && (force || dir.ChildrenLoadedTicks == 0);
        if (!needed) return;
        try
        {
            ListFromCloudAsync(dir, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Debug($"[{Account.DisplayName}] listing {PathOf(dir)} failed: {ex.Message}");
        }
    }

    private readonly Dictionary<long, Task> _listings = new();

    public Task ListFromCloudAsync(Node dir, CancellationToken ct)
    {
        lock (_listings)
        {
            if (_listings.TryGetValue(dir.Id, out var running)) return running;
            var t = DoListAsync(dir, ct);
            _listings[dir.Id] = t;
            t.ContinueWith(_ => { lock (_listings) _listings.Remove(dir.Id); }, TaskScheduler.Default);
            return t;
        }
    }

    private async Task DoListAsync(Node dir, CancellationToken ct)
    {
        await Task.Yield();
        RemoteRef r;
        lock (Store.Gate)
        {
            if (dir.Removed) return;
            if (!dir.HasRemote)
            {
                // Created locally and not uploaded yet: nothing can exist in the cloud below it.
                if (!dir.IsRoot) { dir.ChildrenLoadedTicks = DateTime.UtcNow.Ticks; Store.Save(dir); return; }
            }
        }
        if (dir.IsRoot) await EnsureRootAsync(ct).ConfigureAwait(false);
        lock (Store.Gate) r = RefOf(dir);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var items = await RemoteAsync(c => Provider.ListAsync(r, c), timeout.Token).ConfigureAwait(false);
        ApplyListing(dir, items);
    }

    /// <summary>Merges a fresh cloud listing into the local tree, preserving local changes.</summary>
    public void ApplyListing(Node dir, IReadOnlyList<CloudItem> items)
    {
        var changes = new List<RemoteChange>();
        var invalidate = new List<(Node Node, long Size)>();
        var removedAll = new List<Node>();
        lock (Store.Gate)
        {
            if (dir.Removed || dir.Children == null) return;
            var wasListed = dir.ChildrenLoadedTicks != 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Store.Batch(() =>
            {
                foreach (var item in items)
                {
                    if (item.Id.Length == 0) continue;
                    seen.Add(item.Id);
                    var existing = Store.FindByRemoteId(item.Id);
                    if (existing != null)
                    {
                        if (_nodesWithOps.Contains(existing.Id)) continue;
                        if (existing.Parent != dir)
                        {
                            // Moved in the cloud by someone else.
                            var oldPath = Store.PathOf(existing);
                            var name = Store.UniqueName(dir, VirtualPath.SanitizeName(item.Name));
                            Store.Move(existing, dir, name);
                            existing.RemoteParentId = dir.Id;
                            existing.RemoteName = item.Name;
                            Store.Save(existing);
                            changes.Add(new RemoteChange(RemoteChangeKind.Renamed, oldPath, Store.PathOf(existing)));
                        }
                        else if (existing.RemoteName != item.Name)
                        {
                            var oldPath = Store.PathOf(existing);
                            var desired = VirtualPath.SanitizeName(item.Name);
                            var name = string.Equals(desired, existing.Name, StringComparison.OrdinalIgnoreCase) ? desired : Store.UniqueName(dir, desired);
                            Store.Move(existing, dir, name);
                            existing.RemoteName = item.Name;
                            Store.Save(existing);
                            changes.Add(new RemoteChange(RemoteChangeKind.Renamed, oldPath, Store.PathOf(existing)));
                        }
                        if (!existing.IsDirectory) UpdateContentFromCloud(existing, item, changes, invalidate);
                        continue;
                    }
                    var localName = Store.UniqueName(dir, VirtualPath.SanitizeName(item.Name));
                    var node = Store.CreateNode(dir, localName, item.IsDirectory, n =>
                    {
                        n.RemoteId = item.Id;
                        n.RemoteName = item.Name;
                        n.RemoteParentId = dir.Id;
                        n.Size = item.IsDirectory ? 0 : item.Size;
                        n.RemoteSize = n.Size;
                        n.RemoteHash = item.Md5;
                        n.RemoteModifiedTicks = item.ModifiedUtc.Ticks;
                        n.CreationTime = FileTime.FromUtc(item.CreatedUtc);
                        n.LastWriteTime = n.ChangeTime = n.LastAccessTime = FileTime.FromUtc(item.ModifiedUtc);
                        n.Pinned = dir.Pinned;
                    });
                    if (wasListed) changes.Add(new RemoteChange(RemoteChangeKind.Added, Store.PathOf(node)));
                }

                foreach (var child in Store.ChildrenOf(dir))
                {
                    if (!child.HasRemote || seen.Contains(child.RemoteId) || _nodesWithOps.Contains(child.Id)) continue;
                    // Items that are in the cloud elsewhere (moved by us, change pending) are kept.
                    if (child.RemoteParentId != 0 && child.RemoteParentId != dir.Id) continue;
                    var path = Store.PathOf(child);
                    if (KeepLocalChanges(child, removedAll)) { changes.Add(new RemoteChange(RemoteChangeKind.Modified, path)); continue; }
                    removedAll.AddRange(Store.Remove(child));
                    changes.Add(new RemoteChange(RemoteChangeKind.Removed, path));
                }
                dir.ChildrenLoadedTicks = DateTime.UtcNow.Ticks;
                Store.Save(dir);
            });
        }
        AfterRemoteUpdate(changes, invalidate, removedAll);
    }

    /// <summary>
    /// The cloud copy of a node disappeared. Unsynced local changes are kept: dirty files (and the folders that
    /// contain them) become new local items that are uploaded again; everything clean below is removed.
    /// Returns true when the node was kept.
    /// </summary>
    private bool KeepLocalChanges(Node n, List<Node> removed)
    {
        bool HasDirty(Node x) => Store.Descendants(x).Any(d => d.IsDirty);
        if (!HasDirty(n)) return false;
        void Detach(Node x)
        {
            Store.SetRemoteId(x, "");
            x.RemoteParentId = 0;
            x.RemoteName = x.Name;
            if (x.IsDirectory)
            {
                foreach (var c in Store.ChildrenOf(x))
                {
                    if (HasDirty(c)) Detach(c);
                    else removed.AddRange(Store.Remove(c));
                }
                x.ChildrenLoadedTicks = DateTime.UtcNow.Ticks;
                Enqueue("mkdir", x.Id, "");
            }
            Store.Save(x);
        }
        Detach(n);
        return true;
    }

    private void UpdateContentFromCloud(Node n, CloudItem item, List<RemoteChange> changes, List<(Node, long)> invalidate)
    {
        bool changed = CloudCopyChanged(n, item);
        if (!changed) return;
        if (n.IsDirty)
        {
            CreateConflictCopy(n, item, changes);
            return;
        }
        n.Size = item.Size;
        n.RemoteSize = item.Size;
        n.RemoteHash = item.Md5;
        n.RemoteModifiedTicks = item.ModifiedUtc.Ticks;
        n.LastWriteTime = n.ChangeTime = FileTime.FromUtc(item.ModifiedUtc);
        Store.Save(n);
        invalidate.Add((n, item.Size));
        changes.Add(new RemoteChange(RemoteChangeKind.Modified, Store.PathOf(n)));
    }

    /// <summary>
    /// Both the local file and the cloud file changed. The cloud version keeps the original name; the local
    /// version is renamed to "name (Conflict yyyy-MM-dd HHmm).ext" and uploaded as a separate file.
    /// </summary>
    /// <summary>
    /// Called right before replacing the cloud copy of <paramref name="node"/>. Returns true if the cloud copy is
    /// still the one this node was based on. If somebody else changed or deleted it meanwhile, the local version
    /// becomes a new file (a conflict copy, or simply a re-created file) and false is returned.
    /// </summary>
    public bool PrepareOverwrite(Node node, CloudItem? current)
    {
        var changes = new List<RemoteChange>();
        lock (Store.Gate)
        {
            if (node.Removed || !node.HasRemote) return false;
            if (current == null)
            {
                Store.DeleteUploadSession(node.Id);
                Store.SetRemoteId(node, "");
                node.RemoteParentId = 0;
                node.RemoteName = node.Name;
                Store.Save(node);
                return false;
            }
            if (!CloudCopyChanged(node, current)) return true;
            Store.DeleteUploadSession(node.Id);
            CreateConflictCopy(node, current, changes);
        }
        AfterRemoteUpdate(changes, new List<(Node, long)>(), new List<Node>());
        return false;
    }

    /// <summary>True when the cloud item differs from the version this node was last synchronized with.</summary>
    private static bool CloudCopyChanged(Node n, CloudItem item)
    {
        if (n.RemoteSize != item.Size) return true;
        if (item.Md5.Length > 0 && n.RemoteHash.Length > 0)
            return !string.Equals(item.Md5, n.RemoteHash, StringComparison.OrdinalIgnoreCase);
        // Without a content hash: compare modification times at one-second resolution (TeraBox reports seconds).
        return n.RemoteModifiedTicks != 0 && item.ModifiedUtc != default &&
               n.RemoteModifiedTicks / TimeSpan.TicksPerSecond != item.ModifiedUtc.Ticks / TimeSpan.TicksPerSecond;
    }

    private void CreateConflictCopy(Node local, CloudItem cloud, List<RemoteChange> changes)
    {
        var dir = local.Parent!;
        var originalName = local.Name;
        var conflictName = Store.UniqueName(dir, VirtualPath.WithSuffix(originalName, "Conflict " + DateTime.Now.ToString("yyyy-MM-dd HHmm")));
        var oldPath = Store.PathOf(local);
        Store.Move(local, dir, conflictName);
        Store.SetRemoteId(local, "");
        local.RemoteParentId = 0;
        local.RemoteName = conflictName;
        Store.Save(local);
        var fresh = Store.CreateNode(dir, originalName, false, n =>
        {
            n.RemoteId = cloud.Id;
            n.RemoteName = cloud.Name;
            n.RemoteParentId = dir.Id;
            n.Size = cloud.Size;
            n.RemoteSize = cloud.Size;
            n.RemoteHash = cloud.Md5;
            n.RemoteModifiedTicks = cloud.ModifiedUtc.Ticks;
            n.LastWriteTime = n.ChangeTime = n.LastAccessTime = FileTime.FromUtc(cloud.ModifiedUtc);
            n.Pinned = local.Pinned;
        });
        AddConflict(oldPath, Store.PathOf(local));
        changes.Add(new RemoteChange(RemoteChangeKind.Renamed, oldPath, Store.PathOf(local)));
        changes.Add(new RemoteChange(RemoteChangeKind.Added, Store.PathOf(fresh)));
        Log.Warn($"[{Account.DisplayName}] conflict on {oldPath}: local version kept as {conflictName}.");
    }

    private void AddConflict(string path, string conflictPath)
    {
        var list = Conflicts();
        list.Add(new ConflictRecord { Path = path, ConflictCopy = conflictPath, Utc = DateTime.UtcNow });
        if (list.Count > 200) list.RemoveRange(0, list.Count - 200);
        Store.SetValue("conflicts", JsonSerializer.Serialize(list));
    }

    public sealed class ConflictRecord
    {
        public string Path { get; set; } = "";
        public string ConflictCopy { get; set; } = "";
        public DateTime Utc { get; set; }
    }

    /// <summary>Conflicts recorded so far (call under Gate or from any thread for a snapshot).</summary>
    public List<ConflictRecord> Conflicts()
    {
        var json = Store.GetValue("conflicts");
        if (string.IsNullOrEmpty(json)) return new List<ConflictRecord>();
        try { return JsonSerializer.Deserialize<List<ConflictRecord>>(json) ?? new(); } catch { return new(); }
    }

    public void ClearConflicts() { lock (Store.Gate) Store.SetValue("conflicts", null); }

    /// <summary>Applies an incremental change feed batch (Google Drive).</summary>
    public void ApplyChanges(ChangeBatch batch)
    {
        var changes = new List<RemoteChange>();
        var invalidate = new List<(Node Node, long Size)>();
        var removedAll = new List<Node>();
        lock (Store.Gate)
        {
            Store.Batch(() =>
            {
                foreach (var id in batch.RemovedIds)
                {
                    var n = Store.FindByRemoteId(id);
                    if (n == null || n.IsRoot || _nodesWithOps.Contains(n.Id)) continue;
                    var path = Store.PathOf(n);
                    if (KeepLocalChanges(n, removedAll)) continue;
                    removedAll.AddRange(Store.Remove(n));
                    changes.Add(new RemoteChange(RemoteChangeKind.Removed, path));
                }
                foreach (var item in batch.Changed)
                {
                    var n = Store.FindByRemoteId(item.Id);
                    var parent = item.ParentIds.Select(Store.FindByRemoteId).FirstOrDefault(p => p != null && p.IsDirectory);
                    if (n != null)
                    {
                        if (n.IsRoot || _nodesWithOps.Contains(n.Id)) continue;
                        if (parent == null)
                        {
                            // Moved outside the part of the drive we know about.
                            if (KeepLocalChanges(n, removedAll)) continue;
                            var p = Store.PathOf(n);
                            removedAll.AddRange(Store.Remove(n));
                            changes.Add(new RemoteChange(RemoteChangeKind.Removed, p));
                            continue;
                        }
                        if (parent != n.Parent || n.RemoteName != item.Name)
                        {
                            if (parent == n || Store.Descendants(n).Contains(parent)) continue;
                            var oldPath = Store.PathOf(n);
                            var desired = VirtualPath.SanitizeName(item.Name);
                            var name = parent == n.Parent && string.Equals(desired, n.Name, StringComparison.OrdinalIgnoreCase) ? desired : Store.UniqueName(parent, desired);
                            Store.Move(n, parent, name);
                            n.RemoteParentId = parent.Id;
                            n.RemoteName = item.Name;
                            Store.Save(n);
                            changes.Add(new RemoteChange(RemoteChangeKind.Renamed, oldPath, Store.PathOf(n)));
                        }
                        if (!n.IsDirectory) UpdateContentFromCloud(n, item, changes, invalidate);
                        continue;
                    }
                    if (parent == null || parent.ChildrenLoadedTicks == 0) continue; // will be seen when listed
                    var localName = Store.UniqueName(parent, VirtualPath.SanitizeName(item.Name));
                    var created = Store.CreateNode(parent, localName, item.IsDirectory, x =>
                    {
                        x.RemoteId = item.Id;
                        x.RemoteName = item.Name;
                        x.RemoteParentId = parent.Id;
                        x.Size = item.IsDirectory ? 0 : item.Size;
                        x.RemoteSize = x.Size;
                        x.RemoteHash = item.Md5;
                        x.RemoteModifiedTicks = item.ModifiedUtc.Ticks;
                        x.CreationTime = FileTime.FromUtc(item.CreatedUtc);
                        x.LastWriteTime = x.ChangeTime = x.LastAccessTime = FileTime.FromUtc(item.ModifiedUtc);
                        x.Pinned = parent.Pinned;
                    });
                    changes.Add(new RemoteChange(RemoteChangeKind.Added, Store.PathOf(created)));
                }
            });
        }
        AfterRemoteUpdate(changes, invalidate, removedAll);
    }

    private void AfterRemoteUpdate(List<RemoteChange> changes, List<(Node Node, long Size)> invalidate, List<Node> removed)
    {
        foreach (var (n, size) in invalidate) Cache.Invalidate(n, size);
        if (removed.Count > 0) Cache.Forget(removed);
        if (changes.Count > 0)
        {
            try { RemoteChanged?.Invoke(changes); } catch (Exception ex) { Log.Warn("Change notification failed", ex); }
        }
    }

    // ------------------------------------------------------------------ namespace: local changes

    public Node CreateFile(Node parent, string name, uint attributes)
    {
        Node node;
        lock (Store.Gate)
        {
            if (parent.Removed) throw new IOException("Parent was deleted.");
            node = Store.CreateNode(parent, name, false, n =>
            {
                n.Attributes = (attributes | 0x20u) & ~0x10u & ~0x80u;
                n.Version = 1;
                n.UploadedVersion = 0;
                n.Pinned = parent.Pinned;
                n.LastLocalWriteTicks = DateTime.UtcNow.Ticks;
            });
        }
        Cache.CreateLocal(node);
        return node;
    }

    public Node CreateDirectory(Node parent, string name, uint attributes)
    {
        lock (Store.Gate)
        {
            if (parent.Removed) throw new IOException("Parent was deleted.");
            var node = Store.CreateNode(parent, name, true, n =>
            {
                n.Attributes = attributes | 0x10u;
                n.ChildrenLoadedTicks = DateTime.UtcNow.Ticks; // new folder: nothing to list
                n.Pinned = parent.Pinned;
            });
            Enqueue("mkdir", node.Id, "");
            return node;
        }
    }

    private void Enqueue(string kind, long nodeId, string data)
    {
        Store.EnqueueOp(kind, nodeId, data);
        if (nodeId != 0) _nodesWithOps.Add(nodeId);
        OpsChanged?.Invoke();
    }

    public event Action? OpsChanged;

    internal void OpCompleted(OpRecord op)
    {
        lock (Store.Gate)
        {
            Store.DeleteOp(op.Id);
            if (op.NodeId != 0 && !Store.Ops().Any(o => o.NodeId == op.NodeId)) _nodesWithOps.Remove(op.NodeId);
        }
    }

    internal sealed class DeleteOpData
    {
        public string RemoteId { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public long RemoteParentNodeId { get; set; }
        public string RemoteName { get; set; } = "";
        public bool IsDirectory { get; set; }
    }

    /// <summary>Deletes a node (directories must be empty). The cloud copy is deleted by the sync engine.</summary>
    public void Delete(Node node)
    {
        List<Node> removed;
        lock (Store.Gate)
        {
            if (node.Removed || node.IsRoot) return;
            if (node.HasRemote && (node.RemoteParentId != 0 || node.Parent != null))
            {
                var data = new DeleteOpData
                {
                    RemoteId = node.RemoteId,
                    RemotePath = RemotePath(node),
                    RemoteParentNodeId = Store.RemoteParentOf(node)?.Id ?? 0,
                    RemoteName = node.RemoteName,
                    IsDirectory = node.IsDirectory,
                };
                Enqueue("delete", 0, JsonSerializer.Serialize(data));
            }
            removed = Store.Remove(node);
            _pendingMetaSaves.ExceptWith(removed);
        }
        Cache.Forget(removed);
    }

    /// <summary>Renames/moves a node. Returns false if the target exists and <paramref name="replace"/> is false.</summary>
    public bool Rename(Node node, Node newParent, string newName, bool replace)
    {
        Node? target;
        lock (Store.Gate)
        {
            target = Store.Child(newParent, newName);
            if (target != null && target != node && !replace) return false;
        }
        if (target != null && target != node) Delete(target);
        lock (Store.Gate)
        {
            if (node.Removed) throw new IOException("Item was deleted.");
            Store.Move(node, newParent, newName);
            if (node.HasRemote) Enqueue("move", node.Id, "");
        }
        return true;
    }

    // ------------------------------------------------------------------ data

    public int Read(Node node, long offset, Span<byte> buffer, CancellationToken ct = default)
    {
        long size;
        lock (Store.Gate) { size = node.Size; node.LastAccessTime = FileTime.Now(); }
        return Cache.Read(node, size, offset, buffer, ct);
    }

    public void Write(Node node, long offset, ReadOnlySpan<byte> data, CancellationToken ct = default)
    {
        long before;
        lock (Store.Gate) before = node.Size;
        Cache.Write(node, before, offset, data, ct);
        MarkModified(node, Math.Max(before, offset + data.Length));
    }

    public void SetSize(Node node, long newSize, CancellationToken ct = default)
    {
        Cache.SetLength(node, newSize, ct);
        lock (Store.Gate)
        {
            if (node.Size == newSize && node.IsDirty) return;
        }
        MarkModified(node, newSize, exactSize: true);
    }

    private void MarkModified(Node node, long newSize, bool exactSize = false)
    {
        lock (Store.Gate)
        {
            if (node.Removed) return;
            bool wasClean = !node.IsDirty;
            node.Size = exactSize ? newSize : Math.Max(node.Size, newSize);
            node.Version++;
            var now = FileTime.Now();
            node.LastWriteTime = node.ChangeTime = now;
            node.Attributes |= 0x20;
            node.LastLocalWriteTicks = DateTime.UtcNow.Ticks;
            // Persist the transition to "dirty" immediately so a crash can never lose the fact that data must be uploaded.
            if (wasClean) Store.Save(node);
            else _pendingMetaSaves.Add(node);
        }
    }

    public void SaveMetadata(Node node)
    {
        lock (Store.Gate)
        {
            _pendingMetaSaves.Remove(node);
            Store.Save(node);
        }
    }

    public void FlushMetadata()
    {
        try
        {
            lock (Store.Gate)
            {
                if (_pendingMetaSaves.Count == 0) return;
                Store.SaveMany(_pendingMetaSaves.Where(n => !n.Removed).ToList());
                _pendingMetaSaves.Clear();
            }
        }
        catch (Exception ex) { Log.Warn("Metadata flush failed", ex); }
    }

    public void Flush(Node? node)
    {
        if (node == null) { Cache.PersistDirty(); FlushMetadata(); return; }
        Cache.Flush(node);
        SaveMetadata(node);
    }

    public void HandleOpened(Node node)
    {
        lock (Store.Gate) node.OpenHandles++;
        Cache.Retain(node);
    }

    public void HandleClosed(Node node, bool modified)
    {
        lock (Store.Gate)
        {
            if (node.OpenHandles > 0) node.OpenHandles--;
            if (modified && !node.Removed) { _pendingMetaSaves.Remove(node); Store.Save(node); }
        }
        if (modified) Cache.Flush(node);
        Cache.Unretain(node);
        if (modified) UploadReady?.Invoke();
    }

    /// <summary>Raised when a modified file was closed (lets the sync engine react immediately).</summary>
    public event Action? UploadReady;

    // ------------------------------------------------------------------ pinning

    public void SetPinned(Node node, bool pinned)
    {
        lock (Store.Gate)
        {
            var all = Store.Descendants(node);
            foreach (var n in all) n.Pinned = pinned;
            Store.SaveMany(all);
        }
        if (pinned) PinRequested?.Invoke();
    }

    public event Action? PinRequested;

    // ------------------------------------------------------------------ helpers for sync

    public string Md5OfBlob(Node node, long size)
    {
        using var fs = Cache.OpenForUpload(node);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buf = new byte[1 << 20];
        long left = size;
        while (left > 0)
        {
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n == 0) break;
            md5.AppendData(buf, 0, n);
            left -= n;
        }
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    public bool HasPendingOps(long nodeId) { lock (Store.Gate) return _nodesWithOps.Contains(nodeId); }

    /// <summary>Directories enumerated recently (node id → UTC ticks); path-based clouds refresh them periodically.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> RecentlyEnumerated = new();

    public void NoteEnumerated(Node dir)
    {
        RecentlyEnumerated[dir.Id] = DateTime.UtcNow.Ticks;
        if (RecentlyEnumerated.Count > 2000)
            foreach (var kv in RecentlyEnumerated.Where(kv => kv.Value < DateTime.UtcNow.AddMinutes(-30).Ticks).ToList())
                RecentlyEnumerated.TryRemove(kv.Key, out _);
    }

    /// <summary>Lets the sync engine reset its state after credentials were updated.</summary>
    public void ResetConnection()
    {
        Connection = ConnectionState.Unknown;
        LastError = "";
        _lastProbe = DateTime.MinValue;
        StatusChanged?.Invoke();
    }

    internal static string Join(string a, string b) => JoinRemote(a, b);

    public void Dispose()
    {
        _metaTimer.Dispose();
        FlushMetadata();
        Cache.Dispose();
        Store.Dispose();
        Provider.Dispose();
    }
}
