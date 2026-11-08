using System.Text.Json.Serialization;

namespace SsdVirtual.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderKind
{
    GoogleDrive,
    TeraBox,
    /// <summary>A plain local/UNC folder. Used for diagnostics and automated tests.</summary>
    LocalFolder,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheWritePolicy
{
    /// <summary>Writes land in the local cache and are uploaded after the file has been closed and is quiet.</summary>
    WriteBack,
    /// <summary>Upload starts as soon as the file is closed; FlushFileBuffers waits for the upload to finish.</summary>
    WriteThrough,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionState { Unknown, Online, Offline, AuthRequired, Disconnected, Error }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MountState { Unmounted, Mounting, Mounted, Failed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncStatus { Synced, PendingUpload, Uploading, Downloading, Processing, Offline, Conflict, RetryRequired, Error }

/// <summary>Persistent, non-secret configuration of one cloud account / one virtual drive.</summary>
public sealed class AccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProviderKind Provider { get; set; }
    /// <summary>Friendly name, e.g. "Google 01". Used in the drive label.</summary>
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string AccountName { get; set; } = "";
    /// <summary>Drive letter without colon, e.g. 'D'.</summary>
    public char DriveLetter { get; set; }
    public bool AutoMount { get; set; } = true;
    /// <summary>When false the account is "disconnected": not mounted, no network access, data kept.</summary>
    public bool Connected { get; set; } = true;
    public long CacheQuotaBytes { get; set; } = 50L * 1024 * 1024 * 1024;
    public CacheWritePolicy WritePolicy { get; set; } = CacheWritePolicy.WriteBack;
    /// <summary>Remote folder used as the drive root ("" or "/" means the cloud root).</summary>
    public string RemoteRoot { get; set; } = "";
    /// <summary>Only for <see cref="ProviderKind.LocalFolder"/>.</summary>
    public string LocalFolderPath { get; set; } = "";
    public bool DeleteToCloudTrash { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string VolumeLabel
    {
        get
        {
            var label = "SsdVirtual - " + (string.IsNullOrWhiteSpace(DisplayName) ? Provider.ToString() : DisplayName);
            return label.Length > 32 ? label[..32] : label;
        }
    }
}

public sealed class AppSettings
{
    public string CacheRoot { get; set; } = AppPaths.DefaultCacheRoot;
    public bool MountOnStartup { get; set; } = true;
    public bool KeepMounted { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool RestorePreviousConfiguration { get; set; } = true;
    public int MaxParallelTransfers { get; set; } = 4;
    /// <summary>0 = unlimited.</summary>
    public long DownloadLimitBytesPerSec { get; set; }
    /// <summary>0 = unlimited.</summary>
    public long UploadLimitBytesPerSec { get; set; }
    /// <summary>Seconds between remote change polls.</summary>
    public int SyncIntervalSeconds { get; set; } = 60;
    /// <summary>Seconds a closed, modified file must stay untouched before upload (write-back).</summary>
    public int UploadQuietSeconds { get; set; } = 5;
    /// <summary>Optional daily window "HH:mm-HH:mm" in local time when background uploads are allowed. Empty = always.</summary>
    public string UploadWindow { get; set; } = "";
    public int ReadAheadMaxMiB { get; set; } = 32;
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Dark";
    public bool Notifications { get; set; } = true;
    public string LogLevel { get; set; } = "Info";
    /// <summary>Optional Google OAuth desktop client override (when not embedded at build time).</summary>
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
}

/// <summary>The whole persisted service configuration.</summary>
public sealed class ServiceConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<AccountConfig> Accounts { get; set; } = new();
}
