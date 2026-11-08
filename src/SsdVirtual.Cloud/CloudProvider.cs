using SsdVirtual.Core;

namespace SsdVirtual.Cloud;

/// <summary>Identifies a remote item. ID-based clouds use <see cref="Id"/>, path-based clouds use <see cref="Path"/>.</summary>
public readonly record struct RemoteRef(string Id, string Path)
{
    public override string ToString() => string.IsNullOrEmpty(Path) ? Id : Path;
}

public sealed class CloudItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Full remote path (path-based providers only).</summary>
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>MD5 (hex, lower case) when the provider exposes a trustworthy content hash; otherwise empty.</summary>
    public string Md5 { get; set; } = "";
    public List<string> ParentIds { get; set; } = new();
    public bool Trashed { get; set; }
}

public sealed class CloudAccountInfo
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public bool Unlimited { get; set; }
}

public sealed class ChangeBatch
{
    public List<CloudItem> Changed { get; } = new();
    public List<string> RemovedIds { get; } = new();
    public string NextCursor { get; set; } = "";
}

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    /// <summary>Supports an incremental change feed (<see cref="ICloudProvider.GetChangesAsync"/>).</summary>
    ChangeFeed = 1,
    /// <summary>Items are addressed by path, so pending renames must be applied in order before uploads.</summary>
    PathBased = 2,
    /// <summary>Exposes MD5 hashes that can verify downloads and uploads.</summary>
    ContentMd5 = 4,
    Trash = 8,
}

/// <summary>Persists provider-specific resumable upload state (e.g. a Google resumable session URI).</summary>
public interface IUploadSessionStore
{
    string? Load();
    void Save(string session);
    void Clear();
}

public sealed class UploadRequest
{
    public RemoteRef Parent { get; init; }
    public string ParentPath { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Existing remote item to replace, or null to create a new item.</summary>
    public RemoteRef? Existing { get; init; }
    /// <summary>Seekable stream with the complete file content.</summary>
    public required Stream Content { get; init; }
    public long Length { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public required IUploadSessionStore Session { get; init; }
    public Action<long>? Progress { get; init; }
}

public interface ICloudProvider : IDisposable
{
    ProviderKind Kind { get; }
    ProviderCapabilities Capabilities { get; }
    /// <summary>Short honest note about the integration shown in the UI.</summary>
    string Note { get; }

    Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct);
    /// <summary>Resolves (and creates if needed) the folder used as drive root.</summary>
    Task<CloudItem> ResolveRootAsync(string remoteRoot, CancellationToken ct);
    Task<IReadOnlyList<CloudItem>> ListAsync(RemoteRef folder, CancellationToken ct);
    Task<CloudItem> CreateFolderAsync(RemoteRef parent, string parentPath, string name, CancellationToken ct);
    Task DeleteAsync(RemoteRef item, bool isDirectory, bool toTrash, CancellationToken ct);
    Task<CloudItem> MoveAsync(RemoteRef item, RemoteRef oldParent, RemoteRef newParent, string newParentPath, string newName, bool isDirectory, CancellationToken ct);
    /// <summary>Reads exactly min(buffer.Length, fileSize - offset) bytes starting at <paramref name="offset"/>.</summary>
    Task<int> ReadAsync(RemoteRef item, long offset, Memory<byte> buffer, long fileSize, CancellationToken ct);
    Task<CloudItem> UploadAsync(UploadRequest request, CancellationToken ct);
    /// <summary>Current metadata of an item, or null when it no longer exists.</summary>
    Task<CloudItem?> GetItemAsync(RemoteRef item, CancellationToken ct);
    Task<string> GetChangeCursorAsync(CancellationToken ct);
    Task<ChangeBatch> GetChangesAsync(string cursor, CancellationToken ct);
}

public enum CloudErrorKind { Unknown, Network, Auth, NotFound, AlreadyExists, QuotaExceeded, RateLimited, Server, Unsupported, InvalidRequest }

public sealed class CloudException : Exception
{
    public CloudErrorKind Kind { get; }
    public int HttpStatus { get; }

    public CloudException(CloudErrorKind kind, string message, int httpStatus = 0, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
    }

    public bool IsTransient => Kind is CloudErrorKind.Network or CloudErrorKind.RateLimited or CloudErrorKind.Server or CloudErrorKind.Unknown;

    public static bool IsOffline(Exception ex) =>
        ex is CloudException { Kind: CloudErrorKind.Network } || ex is HttpRequestException || ex is TaskCanceledException { InnerException: TimeoutException };
}
