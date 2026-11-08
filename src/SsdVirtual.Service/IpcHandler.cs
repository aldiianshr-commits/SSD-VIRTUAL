using System.Text.Json;
using SsdVirtual.Cache;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;
using SsdVirtual.Database;
using SsdVirtual.GameStorage;
using SsdVirtual.VirtualDrive;

namespace SsdVirtual.Service;

public sealed class IpcHandler
{
    private readonly DriveManager _m;

    public IpcHandler(DriveManager manager) => _m = manager;

    private static T P<T>(JsonElement? p) where T : new()
        => p is { } e && e.ValueKind == JsonValueKind.Object ? e.Deserialize<T>(IpcJson.Options) ?? new T() : new T();

    public async Task<object?> HandleAsync(string method, JsonElement? p, CancellationToken ct)
    {
        switch (method)
        {
            case IpcMethods.Ping: return "pong";
            case IpcMethods.GetStatus: return _m.GetStatus();
            case IpcMethods.GetSettings: return _m.Settings;
            case IpcMethods.SaveSettings:
            {
                var s = P<AppSettings>(p);
                var oldRoot = _m.Settings.CacheRoot;
                var newRoot = s.CacheRoot;
                s.CacheRoot = oldRoot;
                _m.SaveSettings(s);
                if (!string.IsNullOrWhiteSpace(newRoot) && !string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
                    await Task.Run(() => _m.RelocateCache(newRoot), ct);
                return _m.Settings;
            }
            case IpcMethods.AddGoogleAccount: return await Task.Run(() => _m.AddGoogle(P<AddGoogleAccountParams>(p)), ct);
            case IpcMethods.AddTeraBoxAccount: return await Task.Run(() => _m.AddTeraBox(P<AddTeraBoxAccountParams>(p)), ct);
            case IpcMethods.AddLocalFolderAccount: return await Task.Run(() => _m.AddLocalFolder(P<AddLocalFolderAccountParams>(p)), ct);
            case IpcMethods.RemoveAccount: await Task.Run(() => _m.Remove(P<RemoveAccountParams>(p)), ct); return true;
            case IpcMethods.RenameAccount: { var x = P<RenameAccountParams>(p); _m.Rename(x.AccountId, x.DisplayName); return true; }
            case IpcMethods.ChangeDriveLetter: { var x = P<ChangeDriveLetterParams>(p); await Task.Run(() => _m.ChangeDriveLetter(x.AccountId, x.DriveLetter), ct); return true; }
            case IpcMethods.SetAccountOptions: _m.SetOptions(P<SetAccountOptionsParams>(p)); return true;
            case IpcMethods.Mount: { var id = P<AccountIdParams>(p).AccountId; await Task.Run(() => _m.Mount(id), ct); return true; }
            case IpcMethods.Unmount: { var id = P<AccountIdParams>(p).AccountId; await Task.Run(() => _m.Unmount(id), ct); return true; }
            case IpcMethods.Remount: { var id = P<AccountIdParams>(p).AccountId; await Task.Run(() => _m.Remount(id), ct); return true; }
            case IpcMethods.Disconnect: { var id = P<AccountIdParams>(p).AccountId; await Task.Run(() => _m.Disconnect(id), ct); return true; }
            case IpcMethods.Reconnect: { var id = P<AccountIdParams>(p).AccountId; await Task.Run(() => _m.Reconnect(id), ct); return true; }
            case IpcMethods.UpdateCredentials: { var x = P<UpdateCredentialsParams>(p); await Task.Run(() => _m.UpdateCredentials(x), ct); return true; }
            case IpcMethods.RefreshAccountInfo: await _m.RefreshInfoAsync(P<AccountIdParams>(p).AccountId, ct); return true;
            case IpcMethods.ListTransfers: return ListTransfers();
            case IpcMethods.CancelTransfer: return TransferRegistry.Cancel(P<CancelTransferParams>(p).TransferId);
            case IpcMethods.GetCacheStats: return CacheStats();
            case IpcMethods.ClearCache: return ClearCache(P<ClearCacheParams>(p).AccountId);
            case IpcMethods.VerifyCache: return await Task.Run(() => VerifyCache(P<ClearCacheParams>(p).AccountId, ct), ct);
            case IpcMethods.SetPinned: { var x = P<SetPinnedParams>(p); await Task.Run(() => SetPinned(x.Path, x.Pinned), ct); return true; }
            case IpcMethods.ListPinned: return ListPinned();
            case IpcMethods.GetPathInfo: return await Task.Run(() => PathInfo(P<PathParams>(p).Path), ct);
            case IpcMethods.SyncNow:
            {
                var id = P<AccountIdParams>(p).AccountId;
                foreach (var d in _m.ActiveDrives().Where(d => string.IsNullOrEmpty(id) || d.Account.Id == id)) d.Sync.SyncNow();
                return true;
            }
            case IpcMethods.ListSyncItems: return SyncItems();
            case IpcMethods.RetryFailed:
                foreach (var d in _m.ActiveDrives()) d.Sync.SyncNow();
                return true;
            case IpcMethods.GetPerformance: return _m.GetPerformance();
            case IpcMethods.ListGames: return await Task.Run(ListGames, ct);
            case IpcMethods.CreateSteamLibrary:
            {
                var id = P<AccountIdParams>(p).AccountId;
                var d = _m.ActiveDrives().FirstOrDefault(x => x.Account.Id == id) ?? throw new InvalidOperationException("Drive is not active.");
                if (d.State != MountState.Mounted) throw new InvalidOperationException("Mount the drive first.");
                return await Task.Run(() => GameScanner.CreateSteamLibrary(d.MountPoint + "\\"), ct);
            }
            case IpcMethods.GetLogs:
            {
                var max = Math.Clamp(P<LogsParams>(p).MaxLines, 10, 2000);
                return Log.RecentLines().TakeLast(max).ToList();
            }
            case IpcMethods.ExportConfig: return _m.ExportConfig();
            case IpcMethods.ImportConfig: _m.ImportConfig(P<ImportConfigParams>(p).Json); return true;
            default: throw new InvalidOperationException("Unknown method: " + method);
        }
    }

    private static List<TransferDto> ListTransfers() => TransferRegistry.List().Select(e => new TransferDto
    {
        Id = e.Id, AccountId = e.AccountId, Kind = e.Kind, Path = e.Path, BytesDone = e.BytesDone, BytesTotal = e.BytesTotal,
        State = e.State, Error = e.Error, StartedUtc = e.StartedUtc, BytesPerSecond = e.State == "Running" ? e.BytesPerSecond : 0,
    }).ToList();

    private List<CacheStatsDto> CacheStats()
    {
        var list = new List<CacheStatsDto>();
        foreach (var d in _m.ActiveDrives())
        {
            var c = d.Volume.Cache;
            var (dirty, pinned, files) = c.Breakdown();
            long free = 0;
            try { free = new DriveInfo(Path.GetPathRoot(c.Directory)!).AvailableFreeSpace; } catch { }
            list.Add(new CacheStatsDto
            {
                AccountId = d.Account.Id, CacheDirectory = c.Directory, QuotaBytes = c.QuotaBytes, UsedBytes = c.UsedBytes,
                DirtyBytes = dirty, PinnedBytes = pinned, CachedFiles = files, Hits = c.Hits, Misses = c.Misses, FreeSpaceOnCacheDisk = free,
            });
        }
        return list;
    }

    private int ClearCache(string? accountId)
    {
        int n = 0;
        foreach (var d in _m.ActiveDrives().Where(d => string.IsNullOrEmpty(accountId) || d.Account.Id == accountId))
            n += d.Volume.Cache.ClearClean();
        return n;
    }

    private VerifyResultDto VerifyCache(string? accountId, CancellationToken ct)
    {
        var r = new VerifyResultDto();
        foreach (var d in _m.ActiveDrives().Where(d => string.IsNullOrEmpty(accountId) || d.Account.Id == accountId))
        {
            var (checkedCount, skipped, problems) = d.Volume.Cache.Verify(n => d.MountPoint + d.Volume.PathOf(n), ct);
            r.FilesChecked += checkedCount;
            r.FilesSkipped += skipped;
            r.Corrupted += problems.Count;
            r.Problems.AddRange(problems);
        }
        return r;
    }

    private Node ResolveNode(string windowsPath, out MountedDrive drive)
    {
        var (d, vpath) = _m.Resolve(windowsPath);
        drive = d;
        return d.Volume.Lookup(vpath) ?? throw new InvalidOperationException("Not found: " + windowsPath);
    }

    private void SetPinned(string path, bool pinned)
    {
        var node = ResolveNode(path, out var d);
        d.Volume.SetPinned(node, pinned);
        d.Sync.Wake();
    }

    private List<PathInfoDto> ListPinned()
    {
        var list = new List<PathInfoDto>();
        foreach (var d in _m.ActiveDrives())
        {
            List<Node> roots;
            lock (d.Volume.Store.Gate)
                roots = d.Volume.Store.AllNodes().Where(n => n.Pinned && !n.Removed && (n.Parent == null || !n.Parent.Pinned)).ToList();
            foreach (var n in roots) list.Add(Info(d, n));
        }
        return list;
    }

    private PathInfoDto PathInfo(string path)
    {
        try
        {
            var n = ResolveNode(path, out var d);
            return Info(d, n);
        }
        catch (InvalidOperationException)
        {
            return new PathInfoDto { Path = path, Exists = false };
        }
    }

    private static PathInfoDto Info(MountedDrive d, Node n)
    {
        var v = d.Volume;
        List<Node> files;
        string vpath;
        bool dirty, pinned;
        string remoteId;
        DateTime modified;
        lock (v.Store.Gate)
        {
            files = n.IsDirectory ? v.Store.Descendants(n).Where(x => !x.IsDirectory).ToList() : new List<Node> { n };
            vpath = v.Store.PathOf(n);
            dirty = files.Any(f => f.IsDirty);
            pinned = n.Pinned;
            remoteId = n.RemoteId;
            modified = FileTime.ToUtc(n.LastWriteTime);
        }
        long size = 0, cached = 0;
        foreach (var f in files)
        {
            long s;
            lock (v.Store.Gate) s = f.Size;
            size += s;
            cached += v.Cache.CachedBytes(f, s);
        }
        return new PathInfoDto
        {
            Path = d.Account.DriveLetter + ":" + vpath, AccountId = d.Account.Id, Exists = true, IsDirectory = n.IsDirectory,
            Size = size, CachedBytes = cached, Pinned = pinned, Dirty = dirty,
            SyncState = dirty ? "Pending Upload" : cached >= size ? "Synced (available offline)" : "Synced (cloud)",
            RemoteId = remoteId, ModifiedUtc = modified,
        };
    }

    private List<SyncItemDto> SyncItems()
    {
        var list = new List<SyncItemDto>();
        foreach (var d in _m.ActiveDrives())
        {
            var v = d.Volume;
            lock (v.Store.Gate)
            {
                foreach (var op in v.Store.Ops())
                {
                    var n = op.NodeId != 0 ? v.Store.Get(op.NodeId) : null;
                    string path = n != null ? v.Store.PathOf(n) : "";
                    if (op.Kind == "delete")
                    {
                        try { path = JsonDocument.Parse(op.Data).RootElement.GetProperty("RemotePath").GetString() ?? path; } catch { }
                    }
                    list.Add(new SyncItemDto
                    {
                        Id = op.Id, AccountId = d.Account.Id, Kind = op.Kind, Path = d.Account.DriveLetter + ":" + path.Replace('/', '\\'),
                        State = op.State, Attempts = op.Attempts, LastError = op.LastError, UpdatedUtc = new DateTime(op.UpdatedTicks, DateTimeKind.Utc),
                    });
                }
                foreach (var n in v.Store.DirtyFiles())
                    list.Add(new SyncItemDto
                    {
                        AccountId = d.Account.Id, Kind = "upload", Path = d.Account.DriveLetter + ":" + v.Store.PathOf(n),
                        State = n.OpenHandles > 0 ? "Open" : "Pending Upload", UpdatedUtc = new DateTime(Math.Max(n.LastLocalWriteTicks, 0), DateTimeKind.Utc),
                    });
                foreach (var c in v.Conflicts())
                    list.Add(new SyncItemDto
                    {
                        AccountId = d.Account.Id, Kind = "conflict", Path = d.Account.DriveLetter + ":" + c.Path,
                        State = "Conflict", LastError = "Local version kept as " + c.ConflictCopy, UpdatedUtc = c.Utc,
                    });
            }
        }
        return list;
    }

    private List<GameEntryDto> ListGames()
    {
        var all = new List<GameEntryDto>();
        foreach (var d in _m.ActiveDrives().Where(d => d.State == MountState.Mounted))
        {
            var root = d.MountPoint + "\\";
            var games = GameScanner.ScanSteam(root).Concat(GameScanner.ScanGameFolders(root)).ToList();
            foreach (var g in games)
            {
                try
                {
                    var info = PathInfo(g.InstallPath);
                    if (info.Exists)
                    {
                        g.CachedBytes = info.CachedBytes;
                        g.Pinned = info.Pinned;
                        if (g.SizeOnDisk == 0) g.SizeOnDisk = info.Size;
                    }
                }
                catch (Exception ex) { Log.Debug("Game info failed: " + ex.Message); }
            }
            all.AddRange(games);
        }
        return all;
    }
}
