using System.Security.Cryptography;
using SsdVirtual.Core;

namespace SsdVirtual.Cloud;

/// <summary>
/// Provider backed by a local or UNC folder (e.g. a NAS share). Items are addressed by their path relative
/// to the folder. Besides being useful on its own, it lets the whole virtual-drive stack be tested end to
/// end without cloud credentials. <see cref="SimulateOffline"/> makes every call fail like a network outage.
/// </summary>
public sealed class LocalFolderProvider : ICloudProvider
{
    private readonly string _root;
    public bool SimulateOffline { get; set; }
    public int ReadCalls;

    public LocalFolderProvider(string rootFolder)
    {
        _root = Path.GetFullPath(rootFolder);
        Directory.CreateDirectory(_root);
    }

    public ProviderKind Kind => ProviderKind.LocalFolder;
    public ProviderCapabilities Capabilities => ProviderCapabilities.PathBased | ProviderCapabilities.ContentMd5;
    public string Note => "Local or network folder.";

    private void Check()
    {
        if (SimulateOffline) throw new CloudException(CloudErrorKind.Network, "Simulated network outage.");
    }

    private string Full(string rel)
    {
        var p = Path.GetFullPath(Path.Combine(_root, rel.TrimStart('/', '\\').Replace('/', '\\')));
        if (!p.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new CloudException(CloudErrorKind.InvalidRequest, "Path escapes root.");
        return p;
    }

    private string Rel(string full)
    {
        var rel = Path.GetRelativePath(_root, full);
        return rel == "." ? "/" : "/" + rel.Replace('\\', '/');
    }

    private CloudItem Item(FileSystemInfo fi) => new()
    {
        Id = Rel(fi.FullName),
        Path = Rel(fi.FullName),
        Name = fi.Name,
        IsDirectory = fi is DirectoryInfo,
        Size = fi is FileInfo f ? f.Length : 0,
        ModifiedUtc = fi.LastWriteTimeUtc,
        CreatedUtc = fi.CreationTimeUtc,
    };

    public Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct)
    {
        Check();
        var drive = new DriveInfo(Path.GetPathRoot(_root)!);
        long total = 0, free = 0;
        try { total = drive.TotalSize; free = drive.AvailableFreeSpace; } catch { }
        return Task.FromResult(new CloudAccountInfo { Name = _root, Email = "", TotalBytes = total, UsedBytes = total - free });
    }

    public Task<CloudItem> ResolveRootAsync(string remoteRoot, CancellationToken ct)
    {
        Check();
        var dir = Directory.CreateDirectory(Full(remoteRoot));
        var item = Item(dir);
        if (item.Path.Length == 0) { item.Path = "/"; item.Id = "/"; }
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<CloudItem>> ListAsync(RemoteRef folder, CancellationToken ct)
    {
        Check();
        var dir = new DirectoryInfo(Full(folder.Path));
        if (!dir.Exists) throw new CloudException(CloudErrorKind.NotFound, "Folder not found: " + folder.Path);
        IReadOnlyList<CloudItem> list = dir.EnumerateFileSystemInfos().Select(Item).ToList();
        return Task.FromResult(list);
    }

    public Task<CloudItem> CreateFolderAsync(RemoteRef parent, string parentPath, string name, CancellationToken ct)
    {
        Check();
        var full = Path.Combine(Full(parentPath), name);
        return Task.FromResult(Item(Directory.CreateDirectory(full)));
    }

    public Task DeleteAsync(RemoteRef item, bool isDirectory, bool toTrash, CancellationToken ct)
    {
        Check();
        var full = Full(item.Path);
        if (isDirectory) { if (Directory.Exists(full)) Directory.Delete(full, true); }
        else if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public Task<CloudItem> MoveAsync(RemoteRef item, RemoteRef oldParent, RemoteRef newParent, string newParentPath, string newName, bool isDirectory, CancellationToken ct)
    {
        Check();
        var src = Full(item.Path);
        var dst = Path.Combine(Full(newParentPath), newName);
        if (isDirectory) Directory.Move(src, dst);
        else File.Move(src, dst, overwrite: true);
        return Task.FromResult(Item(isDirectory ? new DirectoryInfo(dst) : new FileInfo(dst)));
    }

    public async Task<int> ReadAsync(RemoteRef item, long offset, Memory<byte> buffer, long fileSize, CancellationToken ct)
    {
        Check();
        Interlocked.Increment(ref ReadCalls);
        await using var fs = new FileStream(Full(item.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, true);
        fs.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await fs.ReadAsync(buffer[total..], ct);
            if (n == 0) break;
            total += n;
        }
        TransferStats.AddDownloaded(total);
        return total;
    }

    public async Task<CloudItem> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        Check();
        var target = request.Existing is { } e ? Full(e.Path) : Path.Combine(Full(request.ParentPath), request.Name);
        var tmp = target + ".ssdv-upload";
        request.Content.Position = 0;
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var buf = new byte[1 << 20];
            long done = 0;
            while (true)
            {
                Check();
                int n = await request.Content.ReadAsync(buf, ct);
                if (n == 0) break;
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                TransferStats.AddUploaded(n);
                request.Progress?.Invoke(done);
            }
        }
        File.Move(tmp, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, request.ModifiedUtc == default ? DateTime.UtcNow : request.ModifiedUtc);
        var item = Item(new FileInfo(target));
        item.Md5 = Md5Of(target);
        return item;
    }

    public static string Md5Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
    }

    public Task<CloudItem?> GetItemAsync(RemoteRef item, CancellationToken ct)
    {
        Check();
        var full = Full(item.Path);
        CloudItem? r = File.Exists(full) ? Item(new FileInfo(full)) : Directory.Exists(full) ? Item(new DirectoryInfo(full)) : null;
        return Task.FromResult(r);
    }

    public Task<string> GetChangeCursorAsync(CancellationToken ct) => throw new CloudException(CloudErrorKind.Unsupported, "No change feed.");
    public Task<ChangeBatch> GetChangesAsync(string cursor, CancellationToken ct) => throw new CloudException(CloudErrorKind.Unsupported, "No change feed.");
    public void Dispose() { }
}
