using System.Security.AccessControl;
using Fsp;
using Fsp.Interop;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.Database;
using SsdVirtual.Sync;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace SsdVirtual.Filesystem;

/// <summary>
/// WinFsp user-mode file system exposing one <see cref="Volume"/> as a Windows disk volume. Each account gets
/// its own instance, its own namespace and its own drive letter.
/// </summary>
public sealed unsafe class CloudFileSystem : FileSystemBase
{
    private const uint AttrDirectory = 0x10, AttrArchive = 0x20, AttrNormal = 0x80;
    private const int AllocationUnit = 4096;

    private readonly Volume _v;
    private readonly byte[] _securityDescriptor;
    private FileSystemHost? _host;
    private long _reads, _writes;

    public static long TotalReads, TotalWrites;

    private sealed class Handle
    {
        public readonly Node Node;
        public bool Modified;
        public bool Cleaned;
        public Handle(Node node) => Node = node;
    }

    public CloudFileSystem(Volume volume)
    {
        _v = volume;
        // Owner: Administrators; full control for SYSTEM, Administrators and Everyone. Cloud files carry no
        // Windows ACLs, so access is governed by who can use the drive.
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)");
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
        _v.RemoteChanged += OnRemoteChanged;
    }

    public long Reads => Interlocked.Read(ref _reads);
    public long Writes => Interlocked.Read(ref _writes);

    // ------------------------------------------------------------------ setup

    public override int Init(object Host0)
    {
        var host = (FileSystemHost)Host0;
        _host = host;
        host.SectorSize = AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        // Metadata and data may be cached by the kernel indefinitely: every local change goes through this
        // file system, and cloud-side changes are pushed with FspFileSystemNotify (see OnRemoteChanged).
        host.FileInfoTimeout = unchecked((uint)-1);
        host.DirInfoTimeout = 1000;
        host.VolumeInfoTimeout = 5000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = false;
        host.PassQueryDirectoryPattern = false;
        // Written data must reach this file system before the last handle closes, otherwise an upload could
        // start before the Windows cache manager lazily writes the data back.
        host.FlushAndPurgeOnCleanup = true;
        host.VolumeCreationTime = _v.Store.Root.CreationTime != 0 ? _v.Store.Root.CreationTime : FileTime.Now();
        host.VolumeSerialNumber = (uint)StableHash(_v.Account.Id);
        // Reported as NTFS for maximum application compatibility (game launchers check the file system name).
        host.FileSystemName = "NTFS";
        return STATUS_SUCCESS;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    public override int ExceptionHandler(Exception ex)
    {
        var status = MapException(ex);
        if (status == STATUS_UNEXPECTED_IO_ERROR) Log.Error($"[{_v.Account.DisplayName}] file system error", ex);
        else Log.Debug($"[{_v.Account.DisplayName}] file system request failed: {ex.Message}");
        return status;
    }

    public static int MapException(Exception ex) => ex switch
    {
        CloudException { Kind: CloudErrorKind.Network } => STATUS_NETWORK_UNREACHABLE,
        CloudException { Kind: CloudErrorKind.Auth } => STATUS_ACCESS_DENIED,
        CloudException { Kind: CloudErrorKind.NotFound } => STATUS_OBJECT_NAME_NOT_FOUND,
        CloudException { Kind: CloudErrorKind.QuotaExceeded } => STATUS_DISK_FULL,
        CloudException => STATUS_IO_DEVICE_ERROR,
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => STATUS_DISK_FULL,
        IOException => STATUS_IO_DEVICE_ERROR,
        UnauthorizedAccessException => STATUS_ACCESS_DENIED,
        OperationCanceledException => STATUS_IO_DEVICE_ERROR,
        _ => STATUS_UNEXPECTED_IO_ERROR,
    };

    // ------------------------------------------------------------------ helpers

    private FileInfo Info(Node n)
    {
        FileInfo fi = default;
        lock (_v.Store.Gate)
        {
            var attrs = n.Attributes;
            if (n.IsDirectory) attrs = (attrs | AttrDirectory) & ~AttrNormal;
            else
            {
                attrs &= ~AttrDirectory;
                if (attrs == 0) attrs = AttrNormal;
            }
            fi.FileAttributes = attrs;
            fi.ReparseTag = 0;
            fi.FileSize = n.IsDirectory ? 0 : (ulong)n.Size;
            fi.AllocationSize = n.IsDirectory ? 0 : ((ulong)n.Size + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
            fi.CreationTime = n.CreationTime;
            fi.LastAccessTime = n.LastAccessTime;
            fi.LastWriteTime = n.LastWriteTime;
            fi.ChangeTime = n.ChangeTime;
            fi.IndexNumber = (ulong)n.Id;
            fi.HardLinks = 0;
        }
        return fi;
    }

    private int NotFound(string fileName)
    {
        var parent = _v.Lookup(VirtualPath.GetParent(fileName));
        return parent == null || !parent.IsDirectory ? STATUS_OBJECT_PATH_NOT_FOUND : STATUS_OBJECT_NAME_NOT_FOUND;
    }

    // ------------------------------------------------------------------ volume

    public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        var info = _v.Info;
        long cacheFree = 0;
        try { cacheFree = new DriveInfo(Path.GetPathRoot(_v.Cache.Directory)!).AvailableFreeSpace; } catch { }
        long total, free;
        if (info != null && info.TotalBytes > 0 && !info.Unlimited)
        {
            total = info.TotalBytes;
            var cloudFree = Math.Max(0, info.TotalBytes - info.UsedBytes);
            // New data is staged in the local cache before upload, so the cache disk limits what fits right now.
            free = Math.Min(cloudFree, cacheFree);
        }
        else
        {
            total = Math.Max(_v.Account.CacheQuotaBytes, (info?.UsedBytes ?? 0) + cacheFree);
            free = cacheFree;
        }
        VolumeInfo.TotalSize = (ulong)Math.Max(total, 0);
        VolumeInfo.FreeSize = (ulong)Math.Max(0, Math.Min(free, total));
        VolumeInfo.SetVolumeLabel(_v.Account.VolumeLabel);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string VolumeLabel, out VolumeInfo VolumeInfo) => GetVolumeInfo(out VolumeInfo);

    // ------------------------------------------------------------------ open / create

    public override int GetSecurityByName(string FileName, out uint FileAttributes, ref byte[] SecurityDescriptor)
    {
        FileAttributes = 0;
        var node = _v.Lookup(FileName);
        if (node == null) return NotFound(FileName);
        FileAttributes = Info(node).FileAttributes;
        if (SecurityDescriptor != null) SecurityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int Create(string FileName, uint CreateOptions, uint GrantedAccess, uint FileAttributes, byte[] SecurityDescriptor,
        ulong AllocationSize, out object FileNode, out object FileDesc, out FileInfo FileInfo, out string NormalizedName)
    {
        FileNode = default!;
        FileDesc = default!;
        FileInfo = default;
        NormalizedName = default!;

        var name = VirtualPath.GetName(FileName);
        if (name.Length == 0 || name.Length > 255) return STATUS_OBJECT_NAME_INVALID;
        var parent = _v.Lookup(VirtualPath.GetParent(FileName));
        if (parent == null || !parent.IsDirectory) return STATUS_OBJECT_PATH_NOT_FOUND;
        _v.EnsureListed(parent);
        lock (_v.Store.Gate)
            if (_v.Store.Child(parent, name) != null) return STATUS_OBJECT_NAME_COLLISION;

        Node node;
        bool isDir = (CreateOptions & FILE_DIRECTORY_FILE) != 0;
        try
        {
            node = isDir ? _v.CreateDirectory(parent, name, FileAttributes) : _v.CreateFile(parent, name, FileAttributes);
        }
        catch (IOException) when (_v.Store.Child(parent, name) != null)
        {
            return STATUS_OBJECT_NAME_COLLISION;
        }
        _v.HandleOpened(node);
        FileNode = new Handle(node) { Modified = !isDir };
        FileInfo = Info(node);
        return STATUS_SUCCESS;
    }

    public override int Open(string FileName, uint CreateOptions, uint GrantedAccess, out object FileNode, out object FileDesc,
        out FileInfo FileInfo, out string NormalizedName)
    {
        FileNode = default!;
        FileDesc = default!;
        FileInfo = default;
        NormalizedName = default!;
        var node = _v.Lookup(FileName);
        if (node == null) return NotFound(FileName);
        _v.HandleOpened(node);
        FileNode = new Handle(node);
        FileInfo = Info(node);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object FileNode, object FileDesc, uint FileAttributes, bool ReplaceFileAttributes, ulong AllocationSize, out FileInfo FileInfo)
    {
        var h = (Handle)FileNode;
        lock (_v.Store.Gate)
        {
            if (ReplaceFileAttributes) h.Node.Attributes = (FileAttributes | AttrArchive) & ~AttrDirectory & ~AttrNormal;
            else h.Node.Attributes |= FileAttributes & ~AttrNormal;
        }
        _v.SetSize(h.Node, 0);
        h.Modified = true;
        FileInfo = Info(h.Node);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object FileNode, object FileDesc, string FileName, uint Flags)
    {
        var h = (Handle)FileNode;
        bool deleted = false;
        if ((Flags & CleanupDelete) != 0)
        {
            _v.Delete(h.Node);
            deleted = true;
        }
        else if ((Flags & (CleanupSetLastWriteTime | CleanupSetChangeTime | CleanupSetArchiveBit)) != 0 && h.Modified)
        {
            lock (_v.Store.Gate)
            {
                var now = FileTime.Now();
                if ((Flags & CleanupSetLastWriteTime) != 0) h.Node.LastWriteTime = now;
                if ((Flags & CleanupSetChangeTime) != 0) h.Node.ChangeTime = now;
                if ((Flags & CleanupSetArchiveBit) != 0) h.Node.Attributes |= AttrArchive;
            }
        }
        if (!h.Cleaned)
        {
            h.Cleaned = true;
            _v.HandleClosed(h.Node, h.Modified && !deleted);
        }
    }

    public override void Close(object FileNode, object FileDesc)
    {
        var h = (Handle)FileNode;
        if (!h.Cleaned)
        {
            h.Cleaned = true;
            _v.HandleClosed(h.Node, h.Modified && !h.Node.Removed);
        }
    }

    // ------------------------------------------------------------------ data

    public override int Read(object FileNode, object FileDesc, IntPtr Buffer, ulong Offset, uint Length, out uint BytesTransferred)
    {
        BytesTransferred = 0;
        var h = (Handle)FileNode;
        if (h.Node.Removed) return STATUS_FILE_DELETED;
        long size;
        lock (_v.Store.Gate) size = h.Node.Size;
        if ((long)Offset >= size) return STATUS_END_OF_FILE;
        Interlocked.Increment(ref _reads);
        Interlocked.Increment(ref TotalReads);
        var span = new Span<byte>((void*)Buffer, (int)Length);
        BytesTransferred = (uint)_v.Read(h.Node, (long)Offset, span);
        return STATUS_SUCCESS;
    }

    public override int Write(object FileNode, object FileDesc, IntPtr Buffer, ulong Offset, uint Length, bool WriteToEndOfFile,
        bool ConstrainedIo, out uint BytesTransferred, out FileInfo FileInfo)
    {
        var h = (Handle)FileNode;
        BytesTransferred = 0;
        if (h.Node.Removed) { FileInfo = default; return STATUS_SUCCESS; }
        long size;
        lock (_v.Store.Gate) size = h.Node.Size;
        if (ConstrainedIo)
        {
            if ((long)Offset >= size) { FileInfo = Info(h.Node); return STATUS_SUCCESS; }
            if ((long)Offset + Length > size) Length = (uint)(size - (long)Offset);
        }
        else if (WriteToEndOfFile)
        {
            Offset = (ulong)size;
        }
        Interlocked.Increment(ref _writes);
        Interlocked.Increment(ref TotalWrites);
        _v.Write(h.Node, (long)Offset, new ReadOnlySpan<byte>((void*)Buffer, (int)Length));
        h.Modified = true;
        BytesTransferred = Length;
        FileInfo = Info(h.Node);
        return STATUS_SUCCESS;
    }

    public override int Flush(object FileNode, object FileDesc, out FileInfo FileInfo)
    {
        if (FileNode == null)
        {
            _v.Flush(null);
            FileInfo = default;
            return STATUS_SUCCESS;
        }
        var h = (Handle)FileNode;
        _v.Flush(h.Node);
        FileInfo = Info(h.Node);
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object FileNode, object FileDesc, out FileInfo FileInfo)
    {
        FileInfo = Info(((Handle)FileNode).Node);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(object FileNode, object FileDesc, uint FileAttributes, ulong CreationTime, ulong LastAccessTime,
        ulong LastWriteTime, ulong ChangeTime, out FileInfo FileInfo)
    {
        var n = ((Handle)FileNode).Node;
        lock (_v.Store.Gate)
        {
            if (FileAttributes != unchecked((uint)-1))
                n.Attributes = n.IsDirectory ? FileAttributes | AttrDirectory : FileAttributes & ~AttrDirectory;
            if (CreationTime != 0) n.CreationTime = CreationTime;
            if (LastAccessTime != 0) n.LastAccessTime = LastAccessTime;
            if (LastWriteTime != 0) n.LastWriteTime = LastWriteTime;
            if (ChangeTime != 0) n.ChangeTime = ChangeTime;
        }
        _v.SaveMetadata(n);
        FileInfo = Info(n);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(object FileNode, object FileDesc, ulong NewSize, bool SetAllocationSize, out FileInfo FileInfo)
    {
        var h = (Handle)FileNode;
        long size;
        lock (_v.Store.Gate) size = h.Node.Size;
        if (!SetAllocationSize)
        {
            if ((long)NewSize != size) { _v.SetSize(h.Node, (long)NewSize); h.Modified = true; }
        }
        else if ((long)NewSize < size)
        {
            // Shrinking the allocation below the file size truncates the file.
            _v.SetSize(h.Node, (long)NewSize);
            h.Modified = true;
        }
        FileInfo = Info(h.Node);
        return STATUS_SUCCESS;
    }

    // ------------------------------------------------------------------ namespace

    public override int CanDelete(object FileNode, object FileDesc, string FileName)
    {
        var n = ((Handle)FileNode).Node;
        if (n.IsRoot) return STATUS_ACCESS_DENIED;
        if (!n.IsDirectory) return STATUS_SUCCESS;
        _v.EnsureListed(n);
        lock (_v.Store.Gate)
        {
            if (n.ChildrenLoadedTicks == 0) return STATUS_NETWORK_UNREACHABLE; // contents unknown while offline
            return n.Children != null && n.Children.Count > 0 ? STATUS_DIRECTORY_NOT_EMPTY : STATUS_SUCCESS;
        }
    }

    public override int Rename(object FileNode, object FileDesc, string FileName, string NewFileName, bool ReplaceIfExists)
    {
        var n = ((Handle)FileNode).Node;
        var newName = VirtualPath.GetName(NewFileName);
        if (newName.Length == 0 || newName.Length > 255) return STATUS_OBJECT_NAME_INVALID;
        var newParent = _v.Lookup(VirtualPath.GetParent(NewFileName));
        if (newParent == null || !newParent.IsDirectory) return STATUS_OBJECT_PATH_NOT_FOUND;
        _v.EnsureListed(newParent);
        lock (_v.Store.Gate)
        {
            for (var p = newParent; p != null; p = p.Parent)
                if (p == n) return STATUS_INVALID_PARAMETER;
            var target = _v.Store.Child(newParent, newName);
            if (target != null && target != n)
            {
                if (!ReplaceIfExists) return STATUS_OBJECT_NAME_COLLISION;
                if (target.IsDirectory) return STATUS_ACCESS_DENIED;
            }
        }
        return _v.Rename(n, newParent, newName, ReplaceIfExists) ? STATUS_SUCCESS : STATUS_OBJECT_NAME_COLLISION;
    }

    public override int GetSecurity(object FileNode, object FileDesc, ref byte[] SecurityDescriptor)
    {
        SecurityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object FileNode, object FileDesc, AccessControlSections Sections, byte[] SecurityDescriptor)
        => STATUS_SUCCESS; // Cloud storage has no Windows ACLs; accept and ignore.

    private sealed class DirEnum
    {
        public required List<(string Name, Node Node)> Entries;
        public int Index;
    }

    public override bool ReadDirectoryEntry(object FileNode, object FileDesc, string Pattern, string Marker, ref object Context,
        out string FileName, out FileInfo FileInfo)
    {
        var dir = ((Handle)FileNode).Node;
        if (Context is not DirEnum e)
        {
            _v.EnsureListed(dir);
            _v.NoteEnumerated(dir);
            var entries = new List<(string, Node)>();
            lock (_v.Store.Gate)
            {
                if (!dir.IsRoot)
                {
                    entries.Add((".", dir));
                    entries.Add(("..", dir.Parent ?? dir));
                }
                var children = _v.Store.ChildrenOf(dir).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var c in children) entries.Add((c.Name, c));
            }
            int start = 0;
            if (Marker != null)
            {
                if (Marker == ".") start = 1;
                else if (Marker == "..") start = 2;
                else
                {
                    start = entries.Count;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var name = entries[i].Item1;
                        if (name is "." or "..") continue;
                        if (StringComparer.OrdinalIgnoreCase.Compare(name, Marker) > 0) { start = i; break; }
                    }
                }
            }
            Context = e = new DirEnum { Entries = entries, Index = Math.Min(start, entries.Count) };
        }
        if (e.Index < e.Entries.Count)
        {
            var (name, node) = e.Entries[e.Index++];
            FileName = name;
            FileInfo = Info(node);
            return true;
        }
        FileName = default!;
        FileInfo = default;
        return false;
    }

    // ------------------------------------------------------------------ cloud-side change notifications

    private void OnRemoteChanged(IReadOnlyList<RemoteChange> changes)
    {
        var host = _host;
        if (host == null) return;
        var infos = new List<NotifyInfo>();
        foreach (var c in changes)
        {
            switch (c.Kind)
            {
                case RemoteChangeKind.Added:
                    infos.Add(new NotifyInfo { FileName = c.Path, Action = NotifyAction.Added, Filter = NotifyFilter.ChangeFileName | NotifyFilter.ChangeDirName });
                    break;
                case RemoteChangeKind.Removed:
                    infos.Add(new NotifyInfo { FileName = c.Path, Action = NotifyAction.Removed, Filter = NotifyFilter.ChangeFileName | NotifyFilter.ChangeDirName });
                    break;
                case RemoteChangeKind.Modified:
                    infos.Add(new NotifyInfo { FileName = c.Path, Action = NotifyAction.Modified, Filter = NotifyFilter.ChangeSize | NotifyFilter.ChangeLastWrite });
                    break;
                case RemoteChangeKind.Renamed:
                    infos.Add(new NotifyInfo { FileName = c.Path, Action = NotifyAction.RenamedOldName, Filter = NotifyFilter.ChangeFileName | NotifyFilter.ChangeDirName });
                    infos.Add(new NotifyInfo { FileName = c.NewPath ?? c.Path, Action = NotifyAction.RenamedNewName, Filter = NotifyFilter.ChangeFileName | NotifyFilter.ChangeDirName });
                    break;
            }
        }
        if (infos.Count == 0) return;
        // Must not run on a file system dispatcher thread; hand off.
        Task.Run(() =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var r = host.NotifyBegin(500);
                    if (r < 0) { Thread.Sleep(100); continue; }
                    try { host.Notify(infos.ToArray()); }
                    finally { host.NotifyEnd(); }
                    return;
                }
                catch (Exception ex)
                {
                    Log.Debug("Explorer notification failed: " + ex.Message);
                    return;
                }
            }
        });
    }
}
