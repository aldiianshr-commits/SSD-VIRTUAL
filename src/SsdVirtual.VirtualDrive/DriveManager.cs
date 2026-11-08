using System.Diagnostics;
using SsdVirtual.Cache;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;
using SsdVirtual.GoogleDrive;
using SsdVirtual.Security;
using SsdVirtual.Sync;
using SsdVirtual.TeraBox;

namespace SsdVirtual.VirtualDrive;

/// <summary>
/// Owns every account and its drive: persistence of configuration and credentials, creation of providers,
/// mounting at startup, the keep-mounted watchdog and all management operations requested by the UI.
/// </summary>
public sealed class DriveManager : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly SecretStore _secrets;
    private readonly HttpClient _http = Http.CreateClient();
    private readonly Dictionary<string, MountedDrive> _drives = new();
    private readonly object _gate = new();
    private ServiceConfig _config;
    private Timer? _watchdog;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public DriveManager(string configPath, string secretsPath)
    {
        _configStore = new ConfigStore(configPath);
        _secrets = new SecretStore(secretsPath);
        _config = _configStore.Load();
        ApplySettings(_config.Settings);
    }

    public AppSettings Settings { get { lock (_gate) return _config.Settings; } }

    private void ApplySettings(AppSettings s)
    {
        TransferStats.Download.BytesPerSecond = s.DownloadLimitBytesPerSec;
        TransferStats.Upload.BytesPerSecond = s.UploadLimitBytesPerSec;
        Log.SetLevel(s.LogLevel);
    }

    private void Save() { lock (_gate) _configStore.Save(_config); }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        List<AccountConfig> accounts;
        lock (_gate) accounts = _config.Accounts.ToList();
        var mount = Settings.MountOnStartup && Settings.RestorePreviousConfiguration;
        foreach (var a in accounts)
        {
            if (!a.Connected) continue;
            try { Activate(a, mount && a.AutoMount); }
            catch (Exception ex) { Log.Error($"[{a.DisplayName}] could not be started", ex); }
        }
        _watchdog = new Timer(_ => Watchdog(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void Watchdog()
    {
        if (!Settings.KeepMounted) return;
        List<MountedDrive> drives;
        lock (_gate) drives = _drives.Values.ToList();
        foreach (var d in drives)
        {
            if (!d.Account.AutoMount || d.UserUnmounted) continue;
            if (d.State == MountState.Mounted && d.IsHealthy()) continue;
            Log.Warn($"[{d.Account.DisplayName}] drive not available; remounting.");
            try { d.Unmount(); d.Mount(); } catch (Exception ex) { Log.Error("Remount failed", ex); }
        }
    }

    private ICloudProvider CreateProvider(AccountConfig a)
    {
        var secrets = _secrets.Get(a.Id);
        switch (a.Provider)
        {
            case ProviderKind.GoogleDrive:
            {
                var (clientId, clientSecret) = secrets.ClientId.Length > 0
                    ? (secrets.ClientId, secrets.ClientSecret)
                    : GoogleOAuth.ResolveClient(Settings.GoogleClientId, Settings.GoogleClientSecret);
                var tokens = new GoogleTokens { AccessToken = secrets.AccessToken, RefreshToken = secrets.RefreshToken, ExpiresUtc = secrets.AccessTokenExpiresUtc };
                var source = new GoogleTokenSource(_http, clientId, clientSecret, tokens, t => _secrets.Update(a.Id, s =>
                {
                    s.AccessToken = t.AccessToken;
                    s.RefreshToken = t.RefreshToken;
                    s.AccessTokenExpiresUtc = t.ExpiresUtc;
                }));
                return new GoogleDriveProvider(_http, source);
            }
            case ProviderKind.TeraBox:
                return new TeraBoxProvider(_http, secrets.Cookie);
            case ProviderKind.LocalFolder:
                return new LocalFolderProvider(a.LocalFolderPath);
            default:
                throw new NotSupportedException(a.Provider.ToString());
        }
    }

    private MountedDrive Activate(AccountConfig a, bool mount)
    {
        MountedDrive drive;
        lock (_gate)
        {
            if (_drives.TryGetValue(a.Id, out var existing)) drive = existing;
            else
            {
                var dir = AppPaths.AccountCacheDir(Settings.CacheRoot, a.Id);
                Directory.CreateDirectory(dir);
                var volume = new Volume(a, Settings, CreateProvider(a), dir);
                drive = new MountedDrive(a, volume);
                _drives[a.Id] = drive;
                drive.StartSync();
            }
        }
        if (mount) drive.Mount();
        return drive;
    }

    private void Deactivate(string accountId)
    {
        MountedDrive? d;
        lock (_gate)
        {
            if (!_drives.TryGetValue(accountId, out d)) return;
            _drives.Remove(accountId);
        }
        d.Dispose();
    }

    private AccountConfig Find(string id)
    {
        lock (_gate) return _config.Accounts.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Account not found.");
    }

    private MountedDrive? DriveOf(string id) { lock (_gate) return _drives.TryGetValue(id, out var d) ? d : null; }

    // ------------------------------------------------------------------ drive letters

    public IReadOnlyList<char> UsedLetters()
    {
        var used = new HashSet<char>();
        foreach (var d in DriveInfo.GetDrives()) used.Add(char.ToUpperInvariant(d.Name[0]));
        lock (_gate) foreach (var a in _config.Accounts) if (a.DriveLetter != '\0') used.Add(char.ToUpperInvariant(a.DriveLetter));
        return used.OrderBy(c => c).ToList();
    }

    public char SuggestLetter()
    {
        var used = UsedLetters();
        for (var c = 'D'; c <= 'Z'; c++) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("No free drive letter.");
    }

    private char ValidateLetter(char requested, string? accountId)
    {
        if (requested == '\0') return SuggestLetter();
        var letter = char.ToUpperInvariant(requested);
        if (letter < 'D' || letter > 'Z') throw new InvalidOperationException("Choose a drive letter between D and Z.");
        lock (_gate)
            if (_config.Accounts.Any(a => a.Id != accountId && char.ToUpperInvariant(a.DriveLetter) == letter))
                throw new InvalidOperationException($"Drive {letter}: is already used by another SsdVirtual account.");
        var own = accountId != null && DriveOf(accountId) is { State: MountState.Mounted } d && char.ToUpperInvariant(d.Account.DriveLetter) == letter;
        if (!own && DriveInfo.GetDrives().Any(x => char.ToUpperInvariant(x.Name[0]) == letter))
            throw new InvalidOperationException($"Drive {letter}: is already in use by Windows.");
        return letter;
    }

    // ------------------------------------------------------------------ account management

    private AccountConfig AddAccount(AccountConfig a, AccountSecrets s)
    {
        lock (_gate)
        {
            a.DriveLetter = ValidateLetter(a.DriveLetter, null);
            if (string.IsNullOrWhiteSpace(a.DisplayName))
            {
                var prefix = a.Provider switch { ProviderKind.GoogleDrive => "Google", ProviderKind.TeraBox => "TeraBox", _ => "Folder" };
                var n = _config.Accounts.Count(x => x.Provider == a.Provider) + 1;
                a.DisplayName = $"{prefix} {n:00}";
            }
            if (a.CacheQuotaBytes <= 0) a.CacheQuotaBytes = 50 * Bytes.GiB;
            _config.Accounts.Add(a);
        }
        _secrets.Set(a.Id, s);
        Save();
        Log.Info($"Added account {a.DisplayName} ({a.Provider}) as {a.DriveLetter}:");
        Activate(a, true);
        return a;
    }

    public AccountConfig AddGoogle(AddGoogleAccountParams p)
    {
        if (string.IsNullOrEmpty(p.RefreshToken)) throw new InvalidOperationException("Missing refresh token.");
        lock (_gate)
            if (!string.IsNullOrEmpty(p.Email) && _config.Accounts.Any(a => a.Provider == ProviderKind.GoogleDrive && string.Equals(a.Email, p.Email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{p.Email} is already connected.");
        return AddAccount(new AccountConfig
        {
            Provider = ProviderKind.GoogleDrive, DisplayName = p.DisplayName, DriveLetter = p.DriveLetter, Email = p.Email,
            AccountName = p.AccountName, CacheQuotaBytes = p.CacheQuotaBytes,
        }, new AccountSecrets
        {
            RefreshToken = p.RefreshToken, AccessToken = p.AccessToken, AccessTokenExpiresUtc = p.AccessTokenExpiresUtc,
            ClientId = p.ClientId, ClientSecret = p.ClientSecret,
        });
    }

    public AccountConfig AddTeraBox(AddTeraBoxAccountParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Cookie)) throw new InvalidOperationException("TeraBox sign-in did not complete.");
        return AddAccount(new AccountConfig
        {
            Provider = ProviderKind.TeraBox, DisplayName = p.DisplayName, DriveLetter = p.DriveLetter, CacheQuotaBytes = p.CacheQuotaBytes,
        }, new AccountSecrets { Cookie = p.Cookie });
    }

    public AccountConfig AddLocalFolder(AddLocalFolderAccountParams p)
    {
        if (!Directory.Exists(p.FolderPath)) throw new InvalidOperationException("Folder does not exist: " + p.FolderPath);
        return AddAccount(new AccountConfig
        {
            Provider = ProviderKind.LocalFolder, DisplayName = p.DisplayName, DriveLetter = p.DriveLetter, LocalFolderPath = p.FolderPath,
            CacheQuotaBytes = p.CacheQuotaBytes, AccountName = p.FolderPath,
        }, new AccountSecrets());
    }

    public void Remove(RemoveAccountParams p)
    {
        var a = Find(p.AccountId);
        var d = DriveOf(a.Id);
        if (d != null && !p.Force)
        {
            int dirty;
            lock (d.Volume.Store.Gate) dirty = d.Volume.Store.DirtyFiles().Count + d.Volume.Store.CountOps();
            if (dirty > 0) throw new InvalidOperationException($"{a.DisplayName} still has {dirty} changes that are not uploaded. Wait for sync or remove with force.");
        }
        Deactivate(a.Id);
        var secrets = _secrets.Get(a.Id);
        lock (_gate) _config.Accounts.RemoveAll(x => x.Id == a.Id);
        _secrets.Remove(a.Id);
        Save();
        if (p.DeleteCache)
        {
            // Only this account's own folder is deleted; other accounts are never touched.
            var dir = AppPaths.AccountCacheDir(Settings.CacheRoot, a.Id);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Log.Warn("Cache folder could not be deleted: " + dir, ex); }
        }
        if (a.Provider == ProviderKind.GoogleDrive && secrets.RefreshToken.Length > 0)
            _ = GoogleOAuth.RevokeAsync(secrets.RefreshToken, CancellationToken.None);
        Log.Info($"Removed account {a.DisplayName}.");
    }

    public void Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Name cannot be empty.");
        var a = Find(id);
        a.DisplayName = name.Trim();
        Save();
        DriveOf(id)?.UpdateLabel();
    }

    public void ChangeDriveLetter(string id, char letter)
    {
        var a = Find(id);
        var newLetter = ValidateLetter(letter, id);
        if (char.ToUpperInvariant(a.DriveLetter) == newLetter) return;
        var d = DriveOf(id);
        var wasMounted = d?.State == MountState.Mounted;
        d?.Unmount();
        a.DriveLetter = newLetter;
        Save();
        if (wasMounted) d!.Mount();
    }

    public void SetOptions(SetAccountOptionsParams p)
    {
        var a = Find(p.AccountId);
        if (p.AutoMount is { } am) a.AutoMount = am;
        if (p.CacheQuotaBytes is { } q && q > 0) a.CacheQuotaBytes = q;
        if (p.WritePolicy is { } wp) a.WritePolicy = wp;
        if (p.DeleteToCloudTrash is { } t) a.DeleteToCloudTrash = t;
        Save();
        var d = DriveOf(a.Id);
        if (d != null) d.Volume.Cache.QuotaBytes = a.CacheQuotaBytes;
    }

    public void Mount(string id) { var a = Find(id); if (!a.Connected) throw new InvalidOperationException("Account is disconnected."); if (!Activate(a, true).Mount()) throw new InvalidOperationException(DriveOf(id)?.MountError ?? "Mount failed."); }
    public void Unmount(string id)
    {
        var d = DriveOf(id);
        if (d == null) return;
        d.UserUnmounted = true;
        d.Unmount();
    }

    public void Remount(string id)
    {
        var d = DriveOf(id);
        if (d == null) { Mount(id); return; }
        d.Unmount();
        if (!d.Mount()) throw new InvalidOperationException(d.MountError);
    }

    public void Disconnect(string id)
    {
        var a = Find(id);
        a.Connected = false;
        Save();
        Deactivate(id);
    }

    public void Reconnect(string id)
    {
        var a = Find(id);
        a.Connected = true;
        Save();
        Deactivate(id);
        Activate(a, a.AutoMount);
    }

    public void UpdateCredentials(UpdateCredentialsParams p)
    {
        var a = Find(p.AccountId);
        _secrets.Update(a.Id, s =>
        {
            if (p.RefreshToken != null) s.RefreshToken = p.RefreshToken;
            if (p.AccessToken != null) s.AccessToken = p.AccessToken;
            if (p.AccessTokenExpiresUtc is { } e) s.AccessTokenExpiresUtc = e;
            if (p.Cookie != null) s.Cookie = p.Cookie;
        });
        a.Connected = true;
        Save();
        var wasMounted = DriveOf(a.Id)?.State == MountState.Mounted;
        Deactivate(a.Id);
        Activate(a, wasMounted || a.AutoMount);
    }

    public async Task RefreshInfoAsync(string id, CancellationToken ct)
    {
        var d = DriveOf(id) ?? throw new InvalidOperationException("Account is not active.");
        d.Volume.ResetConnection();
        await d.Volume.RefreshAccountInfoAsync(ct).ConfigureAwait(false);
        var info = d.Volume.Info;
        if (info != null)
        {
            var a = d.Account;
            if (info.Email.Length > 0) a.Email = info.Email;
            if (info.Name.Length > 0) a.AccountName = info.Name;
            Save();
        }
    }

    public void SaveSettings(AppSettings s)
    {
        if (s.MaxParallelTransfers < 1) s.MaxParallelTransfers = 1;
        if (s.MaxParallelTransfers > 16) s.MaxParallelTransfers = 16;
        string oldRoot;
        lock (_gate)
        {
            oldRoot = _config.Settings.CacheRoot;
            _config.Settings = s;
        }
        Save();
        ApplySettings(s);
        lock (_gate) foreach (var d in _drives.Values) d.Volume.Settings = s;
        if (!string.Equals(oldRoot, s.CacheRoot, StringComparison.OrdinalIgnoreCase))
            Log.Warn($"Cache location changed to {s.CacheRoot}; it takes effect after moving existing cache data (restart the service).");
    }

    /// <summary>Moves cache data to the new cache root (all drives are briefly unmounted).</summary>
    public void RelocateCache(string newRoot)
    {
        List<AccountConfig> accounts;
        string oldRoot;
        lock (_gate) { accounts = _config.Accounts.ToList(); oldRoot = _config.Settings.CacheRoot; }
        if (string.Equals(Path.GetFullPath(oldRoot), Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase)) return;
        var mounted = accounts.Where(a => DriveOf(a.Id)?.State == MountState.Mounted).Select(a => a.Id).ToHashSet();
        foreach (var a in accounts) Deactivate(a.Id);
        Directory.CreateDirectory(newRoot);
        foreach (var a in accounts)
        {
            var from = AppPaths.AccountCacheDir(oldRoot, a.Id);
            var to = AppPaths.AccountCacheDir(newRoot, a.Id);
            if (!Directory.Exists(from) || Directory.Exists(to)) continue;
            if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) Directory.Move(from, to);
            else { CopyDirectory(from, to); Directory.Delete(from, true); }
        }
        lock (_gate) _config.Settings.CacheRoot = newRoot;
        Save();
        foreach (var a in accounts.Where(a => a.Connected)) Activate(a, mounted.Contains(a.Id));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public string ExportConfig() { lock (_gate) return ConfigStore.ExportJson(_config); }

    public void ImportConfig(string json)
    {
        var imported = ConfigStore.ImportJson(json);
        SaveSettings(imported.Settings);
        // Accounts can only be added through sign-in (credentials are never exported), so only settings
        // and names/letters/options of accounts that already exist are restored.
        foreach (var ia in imported.Accounts)
        {
            AccountConfig? a;
            lock (_gate) a = _config.Accounts.FirstOrDefault(x => x.Id == ia.Id);
            if (a == null) continue;
            a.DisplayName = ia.DisplayName;
            a.AutoMount = ia.AutoMount;
            a.CacheQuotaBytes = ia.CacheQuotaBytes;
            a.WritePolicy = ia.WritePolicy;
            a.DeleteToCloudTrash = ia.DeleteToCloudTrash;
        }
        Save();
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<MountedDrive> ActiveDrives() { lock (_gate) return _drives.Values.ToList(); }

    public ServiceStatusDto GetStatus()
    {
        var dto = new ServiceStatusDto { ServiceVersion = typeof(DriveManager).Assembly.GetName().Version?.ToString() ?? "", StartedUtc = StartedUtc, Settings = Settings };
        try
        {
            dto.WinFspVersion = Fsp.FileSystemHost.Version().ToString();
            dto.WinFspInstalled = true;
        }
        catch (Exception) { dto.WinFspInstalled = false; }
        List<AccountConfig> accounts;
        lock (_gate) accounts = _config.Accounts.ToList();
        foreach (var a in accounts)
        {
            var s = new AccountStatusDto { Account = a };
            var d = DriveOf(a.Id);
            if (d == null)
            {
                s.Connection = a.Connected ? ConnectionState.Unknown : ConnectionState.Disconnected;
                s.Mount = MountState.Unmounted;
                s.Sync = a.Connected ? SyncStatus.Offline : SyncStatus.Synced;
            }
            else
            {
                var v = d.Volume;
                s.Connection = v.Connection;
                s.Mount = d.State;
                s.MountPoint = d.MountPoint;
                s.Sync = d.Sync.Status;
                s.TotalBytes = v.Info?.TotalBytes ?? 0;
                s.UsedBytes = v.Info?.UsedBytes ?? 0;
                s.CacheUsedBytes = v.Cache.UsedBytes;
                s.PendingUploads = d.Sync.PendingUploads;
                s.PendingOperations = d.Sync.PendingOps;
                s.ActiveTransfers = TransferRegistry.ActiveCount(a.Id);
                s.LastError = d.MountError.Length > 0 ? d.MountError : v.LastError;
                s.LastSyncUtc = v.LastSyncUtc;
                s.ProviderNote = v.Provider.Note;
                lock (v.Store.Gate) s.Conflicts = v.Conflicts().Count;
            }
            dto.Accounts.Add(s);
        }
        return dto;
    }

    /// <summary>Maps "E:\Games\x" to the drive that owns it and the virtual path "\Games\x".</summary>
    public (MountedDrive Drive, string VirtualPath) Resolve(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        var letter = char.ToUpperInvariant(full[0]);
        MountedDrive? d;
        lock (_gate) d = _drives.Values.FirstOrDefault(x => char.ToUpperInvariant(x.Account.DriveLetter) == letter);
        if (d == null) throw new InvalidOperationException($"{letter}: is not an SsdVirtual drive.");
        var rest = full.Length > 2 ? full[2..] : "\\";
        if (rest.Length == 0) rest = "\\";
        return (d, rest);
    }

    private readonly Stopwatch _cpuClock = Stopwatch.StartNew();
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private TimeSpan _lastCpuWall = TimeSpan.Zero;
    private long _lastDown, _lastUp;
    private double _downRate, _upRate;

    public PerformanceDto GetPerformance()
    {
        var p = Process.GetCurrentProcess();
        var wall = _cpuClock.Elapsed;
        var cpu = p.TotalProcessorTime;
        double cpuPct = 0, seconds = (wall - _lastCpuWall).TotalSeconds;
        var down = TransferStats.BytesDownloaded;
        var up = TransferStats.BytesUploaded;
        if (seconds > 0.2)
        {
            cpuPct = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            _downRate = (down - _lastDown) / seconds;
            _upRate = (up - _lastUp) / seconds;
            _lastCpu = cpu; _lastCpuWall = wall; _lastDown = down; _lastUp = up;
        }
        long hits = 0, misses = 0;
        foreach (var d in ActiveDrives()) { hits += d.Volume.Cache.Hits; misses += d.Volume.Cache.Misses; }
        return new PerformanceDto
        {
            ServiceCpuPercent = Math.Round(cpuPct, 1),
            WorkingSetBytes = p.WorkingSet64,
            PrivateBytes = p.PrivateMemorySize64,
            ThreadCount = p.Threads.Count,
            BytesDownloaded = down,
            BytesUploaded = up,
            ReadRequests = Filesystem.CloudFileSystem.TotalReads,
            WriteRequests = Filesystem.CloudFileSystem.TotalWrites,
            CacheHitBlocks = hits,
            CacheMissBlocks = misses,
            AvgFetchLatencyMs = Math.Round(TransferStats.AverageFetchMs, 1),
            DownloadBytesPerSec = _downRate,
            UploadBytesPerSec = _upRate,
            UptimeSeconds = (DateTime.UtcNow - StartedUtc).TotalSeconds,
        };
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        List<string> ids;
        lock (_gate) ids = _drives.Keys.ToList();
        foreach (var id in ids)
        {
            try { Deactivate(id); } catch (Exception ex) { Log.Warn("Shutdown of drive failed", ex); }
        }
        _http.Dispose();
    }
}
