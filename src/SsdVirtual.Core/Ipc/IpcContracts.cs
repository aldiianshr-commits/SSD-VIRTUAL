using System.Text.Json;
using System.Text.Json.Serialization;

namespace SsdVirtual.Core.Ipc;

public sealed class IpcRequest
{
    public long Id { get; set; }
    public string Method { get; set; } = "";
    public JsonElement? Params { get; set; }
}

public sealed class IpcResponse
{
    public long Id { get; set; }
    public bool Ok { get; set; }
    public JsonElement? Result { get; set; }
    public string? Error { get; set; }
}

public static class IpcMethods
{
    public const string Ping = "ping";
    public const string GetStatus = "status.get";
    public const string GetSettings = "settings.get";
    public const string SaveSettings = "settings.save";
    public const string AddGoogleAccount = "account.addGoogle";
    public const string AddTeraBoxAccount = "account.addTeraBox";
    public const string AddLocalFolderAccount = "account.addLocalFolder";
    public const string RemoveAccount = "account.remove";
    public const string RenameAccount = "account.rename";
    public const string ChangeDriveLetter = "account.changeDriveLetter";
    public const string SetAccountOptions = "account.setOptions";
    public const string Remount = "account.remount";
    public const string Mount = "account.mount";
    public const string Unmount = "account.unmount";
    public const string Disconnect = "account.disconnect";
    public const string Reconnect = "account.reconnect";
    public const string UpdateCredentials = "account.updateCredentials";
    public const string RefreshAccountInfo = "account.refreshInfo";
    public const string ListTransfers = "transfers.list";
    public const string CancelTransfer = "transfers.cancel";
    public const string GetCacheStats = "cache.stats";
    public const string ClearCache = "cache.clear";
    public const string VerifyCache = "cache.verify";
    public const string SetPinned = "cache.setPinned";
    public const string ListPinned = "cache.listPinned";
    public const string GetPathInfo = "fs.pathInfo";
    public const string SyncNow = "sync.now";
    public const string ListSyncItems = "sync.items";
    public const string RetryFailed = "sync.retryFailed";
    public const string GetPerformance = "perf.get";
    public const string ListGames = "games.list";
    public const string CreateSteamLibrary = "games.createSteamLibrary";
    public const string GetLogs = "diag.logs";
    public const string ExportConfig = "config.export";
    public const string ImportConfig = "config.import";
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };
}

// ---------- parameter DTOs ----------

public sealed class AccountIdParams { public string AccountId { get; set; } = ""; }

public sealed class AddGoogleAccountParams
{
    public string DisplayName { get; set; } = "";
    public char DriveLetter { get; set; }
    public string Email { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public long CacheQuotaBytes { get; set; }
}

public sealed class AddTeraBoxAccountParams
{
    public string DisplayName { get; set; } = "";
    public char DriveLetter { get; set; }
    /// <summary>Cookie header captured from the user's own interactive login, sent only after explicit consent.</summary>
    public string Cookie { get; set; } = "";
    public long CacheQuotaBytes { get; set; }
}

public sealed class AddLocalFolderAccountParams
{
    public string DisplayName { get; set; } = "";
    public char DriveLetter { get; set; }
    public string FolderPath { get; set; } = "";
    public long CacheQuotaBytes { get; set; }
}

public sealed class RemoveAccountParams { public string AccountId { get; set; } = ""; public bool DeleteCache { get; set; } = true; public bool Force { get; set; } }
public sealed class RenameAccountParams { public string AccountId { get; set; } = ""; public string DisplayName { get; set; } = ""; }
public sealed class ChangeDriveLetterParams { public string AccountId { get; set; } = ""; public char DriveLetter { get; set; } }

public sealed class SetAccountOptionsParams
{
    public string AccountId { get; set; } = "";
    public bool? AutoMount { get; set; }
    public long? CacheQuotaBytes { get; set; }
    public CacheWritePolicy? WritePolicy { get; set; }
    public bool? DeleteToCloudTrash { get; set; }
}

public sealed class UpdateCredentialsParams
{
    public string AccountId { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? AccessToken { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public string? Cookie { get; set; }
}

public sealed class PathParams { public string Path { get; set; } = ""; }
public sealed class SetPinnedParams { public string Path { get; set; } = ""; public bool Pinned { get; set; } }
public sealed class CancelTransferParams { public string TransferId { get; set; } = ""; }
public sealed class ClearCacheParams { public string? AccountId { get; set; } }
public sealed class LogsParams { public int MaxLines { get; set; } = 300; }
public sealed class ImportConfigParams { public string Json { get; set; } = ""; }

// ---------- result DTOs ----------

public sealed class ServiceStatusDto
{
    public string ServiceVersion { get; set; } = "";
    public bool WinFspInstalled { get; set; }
    public string WinFspVersion { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public AppSettings Settings { get; set; } = new();
    public List<AccountStatusDto> Accounts { get; set; } = new();
}

public sealed class AccountStatusDto
{
    public AccountConfig Account { get; set; } = new();
    public ConnectionState Connection { get; set; }
    public MountState Mount { get; set; }
    public SyncStatus Sync { get; set; }
    public string MountPoint { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long CacheUsedBytes { get; set; }
    public long CacheDirtyBytes { get; set; }
    public int PendingUploads { get; set; }
    public int PendingOperations { get; set; }
    public int Conflicts { get; set; }
    public int ActiveTransfers { get; set; }
    public string LastError { get; set; } = "";
    public DateTime? LastSyncUtc { get; set; }
    public string ProviderNote { get; set; } = "";
}

public sealed class TransferDto
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public string State { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public double BytesPerSecond { get; set; }
}

public sealed class CacheStatsDto
{
    public string AccountId { get; set; } = "";
    public string CacheDirectory { get; set; } = "";
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    public long DirtyBytes { get; set; }
    public long PinnedBytes { get; set; }
    public int CachedFiles { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long FreeSpaceOnCacheDisk { get; set; }
}

public sealed class SyncItemDto
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string State { get; set; } = "";
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

public sealed class PerformanceDto
{
    public double ServiceCpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public int ThreadCount { get; set; }
    public long BytesDownloaded { get; set; }
    public long BytesUploaded { get; set; }
    public long ReadRequests { get; set; }
    public long WriteRequests { get; set; }
    public long CacheHitBlocks { get; set; }
    public long CacheMissBlocks { get; set; }
    public double AvgFetchLatencyMs { get; set; }
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public double UptimeSeconds { get; set; }
}

public sealed class PathInfoDto
{
    public string Path { get; set; } = "";
    public string AccountId { get; set; } = "";
    public bool Exists { get; set; }
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long CachedBytes { get; set; }
    public bool Pinned { get; set; }
    public bool Dirty { get; set; }
    public string SyncState { get; set; } = "";
    public string RemoteId { get; set; } = "";
    public DateTime ModifiedUtc { get; set; }
}

public sealed class VerifyResultDto
{
    public int FilesChecked { get; set; }
    public int FilesSkipped { get; set; }
    public int Corrupted { get; set; }
    public List<string> Problems { get; set; } = new();
}

public sealed class GameEntryDto
{
    public string Launcher { get; set; } = "Steam";
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string LibraryPath { get; set; } = "";
    public long SizeOnDisk { get; set; }
    public long CachedBytes { get; set; }
    public bool Pinned { get; set; }
}

public sealed class CreateSteamLibraryResultDto
{
    public string LibraryPath { get; set; } = "";
    public bool AlreadyExisted { get; set; }
}
