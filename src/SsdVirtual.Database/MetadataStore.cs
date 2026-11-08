using Microsoft.Data.Sqlite;
using SsdVirtual.Core;

namespace SsdVirtual.Database;

/// <summary>One file or directory of a virtual drive. Mutated only while holding <see cref="MetadataStore.Gate"/>.</summary>
public sealed class Node
{
    public long Id;
    public long ParentId;
    /// <summary>Name shown in Windows (sanitized and de-duplicated).</summary>
    public string Name = "";
    /// <summary>Name of the item in the cloud.</summary>
    public string RemoteName = "";
    public bool IsDirectory;
    public long Size;
    public string RemoteId = "";
    /// <summary>Node id of the directory the item currently lives in, in the cloud (may lag a local move until synced).</summary>
    public long RemoteParentId;
    public string RemoteHash = "";
    public long RemoteModifiedTicks;
    public ulong CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
    public uint Attributes;
    /// <summary>Incremented on every local content change.</summary>
    public long Version;
    /// <summary>The <see cref="Version"/> whose content is known to be in the cloud.</summary>
    public long UploadedVersion;
    /// <summary>UTC ticks of the last successful listing of this directory; 0 = never listed.</summary>
    public long ChildrenLoadedTicks;
    public bool Pinned;

    // ---- runtime only ----
    public Node? Parent;
    public Dictionary<string, Node>? Children;
    public int OpenHandles;
    public long LastLocalWriteTicks;
    public bool Removed;

    public bool IsDirty => !IsDirectory && Version != UploadedVersion;
    public bool IsRoot => ParentId == 0;
    public bool HasRemote => RemoteId.Length > 0;
}

public sealed class OpRecord
{
    public long Id;
    public string Kind = "";
    public long NodeId;
    public string Data = "";
    public string State = "Pending";
    public int Attempts;
    public string LastError = "";
    public long NextAttemptTicks;
    public long CreatedTicks;
    public long UpdatedTicks;
}

public sealed class CacheFileRecord
{
    public long NodeId;
    public long Size;
    public byte[] Bitmap = Array.Empty<byte>();
    public long CachedBytes;
    public long LastAccessTicks;
    public bool Complete;
}

/// <summary>
/// Per-account metadata: an in-memory namespace tree backed by SQLite (WAL). Also stores the operation
/// journal, resumable upload sessions and the cache block index. Every account has its own database file,
/// which keeps accounts fully isolated.
/// </summary>
public sealed class MetadataStore : IDisposable
{
    public readonly object Gate = new();
    private readonly SqliteConnection _db;
    private readonly Dictionary<long, Node> _byId = new();
    private readonly Dictionary<string, Node> _byRemoteId = new(StringComparer.Ordinal);
    private long _nextId;
    public Node Root { get; private set; } = null!;
    public string DatabasePath { get; }

    public MetadataStore(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        Exec("PRAGMA foreign_keys=OFF;");
        Exec("PRAGMA busy_timeout=5000;");
        CreateSchema();
        LoadTree();
    }

    private void CreateSchema()
    {
        Exec(@"CREATE TABLE IF NOT EXISTS nodes(
            id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, name TEXT NOT NULL, remote_name TEXT NOT NULL,
            is_dir INTEGER NOT NULL, size INTEGER NOT NULL, remote_id TEXT NOT NULL, remote_parent INTEGER NOT NULL, remote_hash TEXT NOT NULL,
            remote_mtime INTEGER NOT NULL, ctime INTEGER NOT NULL, atime INTEGER NOT NULL, mtime INTEGER NOT NULL,
            chtime INTEGER NOT NULL, attrs INTEGER NOT NULL, version INTEGER NOT NULL, uploaded_version INTEGER NOT NULL,
            children_loaded INTEGER NOT NULL, pinned INTEGER NOT NULL);");
        Exec("CREATE INDEX IF NOT EXISTS ix_nodes_parent ON nodes(parent_id);");
        Exec(@"CREATE TABLE IF NOT EXISTS ops(
            id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, node_id INTEGER NOT NULL, data TEXT NOT NULL,
            state TEXT NOT NULL, attempts INTEGER NOT NULL, last_error TEXT NOT NULL, next_attempt INTEGER NOT NULL,
            created INTEGER NOT NULL, updated INTEGER NOT NULL);");
        Exec(@"CREATE TABLE IF NOT EXISTS upload_sessions(
            node_id INTEGER PRIMARY KEY, version INTEGER NOT NULL, session TEXT NOT NULL, updated INTEGER NOT NULL);");
        Exec(@"CREATE TABLE IF NOT EXISTS cache_files(
            node_id INTEGER PRIMARY KEY, size INTEGER NOT NULL, bitmap BLOB NOT NULL, cached_bytes INTEGER NOT NULL,
            last_access INTEGER NOT NULL, complete INTEGER NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    }

    private void LoadTree()
    {
        var all = new List<Node>();
        using (var cmd = Cmd())
        {
            cmd.CommandText = "SELECT id,parent_id,name,remote_name,is_dir,size,remote_id,remote_hash,remote_mtime,ctime,atime,mtime,chtime,attrs,version,uploaded_version,children_loaded,pinned,remote_parent FROM nodes";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                all.Add(new Node
                {
                    Id = r.GetInt64(0), ParentId = r.GetInt64(1), Name = r.GetString(2), RemoteName = r.GetString(3),
                    IsDirectory = r.GetInt64(4) != 0, Size = r.GetInt64(5), RemoteId = r.GetString(6), RemoteHash = r.GetString(7),
                    RemoteModifiedTicks = r.GetInt64(8), CreationTime = (ulong)r.GetInt64(9), LastAccessTime = (ulong)r.GetInt64(10),
                    LastWriteTime = (ulong)r.GetInt64(11), ChangeTime = (ulong)r.GetInt64(12), Attributes = (uint)r.GetInt64(13),
                    Version = r.GetInt64(14), UploadedVersion = r.GetInt64(15), ChildrenLoadedTicks = r.GetInt64(16), Pinned = r.GetInt64(17) != 0, RemoteParentId = r.GetInt64(18),
                });
            }
        }

        foreach (var n in all) _byId[n.Id] = n;
        _nextId = all.Count == 0 ? 1 : all.Max(n => n.Id) + 1;

        var root = all.FirstOrDefault(n => n.ParentId == 0);
        if (root == null)
        {
            var now = FileTime.Now();
            root = new Node
            {
                Id = _nextId++, ParentId = 0, Name = "", RemoteName = "", IsDirectory = true,
                CreationTime = now, LastAccessTime = now, LastWriteTime = now, ChangeTime = now, Attributes = 0x10,
            };
            _byId[root.Id] = root;
            Insert(root);
        }
        root.Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        Root = root;

        var orphans = new List<Node>();
        foreach (var n in all.Where(n => n.IsDirectory)) n.Children ??= new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in all)
        {
            if (n == root) continue;
            if (_byId.TryGetValue(n.ParentId, out var p) && p.IsDirectory && p.Children != null && !p.Children.ContainsKey(n.Name))
            {
                n.Parent = p;
                p.Children[n.Name] = n;
                if (n.HasRemote) _byRemoteId[n.RemoteId] = n;
            }
            else orphans.Add(n);
        }
        // Orphans can only result from interrupted multi-row updates; attach them to the root under a unique name
        // instead of deleting, so no local data is ever lost.
        foreach (var o in orphans)
        {
            if (o.IsDirectory == false && !o.IsDirty) { _byId.Remove(o.Id); Exec("DELETE FROM nodes WHERE id=$id", ("$id", o.Id)); continue; }
            var name = UniqueName(root, VirtualPath.WithSuffix(o.Name, "recovered"));
            o.Parent = root; o.ParentId = root.Id; o.Name = name;
            root.Children[name] = o;
            Save(o);
        }
        if (orphans.Count > 0) Log.Warn($"{DatabasePath}: re-attached {orphans.Count} orphaned entries.");
    }

    // ------------------------------------------------------------------ tree API (call under Gate)

    public Node? Get(long id) => _byId.TryGetValue(id, out var n) ? n : null;

    public Node? FindByRemoteId(string remoteId) => remoteId.Length > 0 && _byRemoteId.TryGetValue(remoteId, out var n) ? n : null;

    public int NodeCount => _byId.Count;

    public Node? Find(string path)
    {
        var node = Root;
        foreach (var part in VirtualPath.Split(path))
        {
            if (!node.IsDirectory || node.Children == null || !node.Children.TryGetValue(part, out var child)) return null;
            node = child;
        }
        return node;
    }

    public Node? Child(Node dir, string name)
        => dir.Children != null && dir.Children.TryGetValue(name, out var c) ? c : null;

    public List<Node> ChildrenOf(Node dir)
        => dir.Children == null ? new List<Node>() : dir.Children.Values.ToList();

    public string PathOf(Node n)
    {
        if (n.IsRoot) return "\\";
        var parts = new Stack<string>();
        for (var c = n; c != null && !c.IsRoot; c = c.Parent) parts.Push(c.Name);
        return "\\" + string.Join('\\', parts);
    }

    /// <summary>
    /// Path of the item in the cloud relative to the drive's remote root, e.g. "/Games/x.bin", following where items
    /// currently are in the cloud (remote parent + remote name), not where they are locally.
    /// </summary>
    public string RemotePathOf(Node n)
    {
        if (n.IsRoot) return "/";
        var parts = new Stack<string>();
        var guard = 0;
        for (var c = n; c != null && !c.IsRoot && guard++ < 4096;)
        {
            parts.Push(c.RemoteName.Length > 0 ? c.RemoteName : c.Name);
            c = (c.RemoteParentId != 0 ? Get(c.RemoteParentId) : null) ?? c.Parent;
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>The node whose cloud folder currently contains <paramref name="n"/>.</summary>
    public Node? RemoteParentOf(Node n) => (n.RemoteParentId != 0 ? Get(n.RemoteParentId) : null) ?? n.Parent;

    public string UniqueName(Node dir, string desired)
    {
        if (Child(dir, desired) == null) return desired;
        for (int i = 2; ; i++)
        {
            var candidate = VirtualPath.WithSuffix(desired, i.ToString());
            if (Child(dir, candidate) == null) return candidate;
        }
    }

    public Node CreateNode(Node parent, string name, bool isDirectory, Action<Node>? init = null)
    {
        if (!parent.IsDirectory || parent.Children == null) throw new InvalidOperationException("Parent is not a directory.");
        if (parent.Children.ContainsKey(name)) throw new IOException("Name collision: " + name);
        var now = FileTime.Now();
        var n = new Node
        {
            Id = _nextId++, ParentId = parent.Id, Parent = parent, Name = name, RemoteName = name, IsDirectory = isDirectory,
            CreationTime = now, LastAccessTime = now, LastWriteTime = now, ChangeTime = now,
            Attributes = isDirectory ? 0x10u : 0x20u,
        };
        if (isDirectory) n.Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        init?.Invoke(n);
        _byId[n.Id] = n;
        parent.Children[name] = n;
        if (n.HasRemote) _byRemoteId[n.RemoteId] = n;
        Insert(n);
        return n;
    }

    public void SetRemoteId(Node n, string remoteId)
    {
        if (n.RemoteId.Length > 0 && _byRemoteId.TryGetValue(n.RemoteId, out var existing) && existing == n) _byRemoteId.Remove(n.RemoteId);
        n.RemoteId = remoteId;
        if (remoteId.Length > 0) _byRemoteId[remoteId] = n;
    }

    public void Move(Node n, Node newParent, string newName)
    {
        if (n.IsRoot) throw new InvalidOperationException("Cannot move root.");
        if (newParent.Children == null) throw new InvalidOperationException("Target is not a directory.");
        for (var p = newParent; p != null; p = p.Parent)
            if (p == n) throw new IOException("Cannot move a directory into itself.");
        n.Parent!.Children!.Remove(n.Name);
        n.Parent = newParent;
        n.ParentId = newParent.Id;
        n.Name = newName;
        newParent.Children[newName] = n;
        n.ChangeTime = FileTime.Now();
        Save(n);
    }

    /// <summary>Removes the node and all descendants. Returns every removed node.</summary>
    public List<Node> Remove(Node n)
    {
        var removed = new List<Node>();
        void Collect(Node x)
        {
            removed.Add(x);
            if (x.Children != null) foreach (var c in x.Children.Values) Collect(c);
        }
        Collect(n);
        n.Parent?.Children?.Remove(n.Name);
        using var tx = Transaction();
        foreach (var x in removed)
        {
            x.Removed = true;
            _byId.Remove(x.Id);
            if (x.HasRemote && _byRemoteId.TryGetValue(x.RemoteId, out var r) && r == x) _byRemoteId.Remove(x.RemoteId);
            Exec("DELETE FROM nodes WHERE id=$id", ("$id", x.Id));
            Exec("DELETE FROM cache_files WHERE node_id=$id", ("$id", x.Id));
            Exec("DELETE FROM upload_sessions WHERE node_id=$id", ("$id", x.Id));
        }
        tx.Commit();
        return removed;
    }

    public IEnumerable<Node> AllNodes() => _byId.Values;

    public List<Node> DirtyFiles() => _byId.Values.Where(n => n.IsDirty && !n.Removed).ToList();

    public List<Node> Descendants(Node n)
    {
        var list = new List<Node>();
        void Walk(Node x) { list.Add(x); if (x.Children != null) foreach (var c in x.Children.Values) Walk(c); }
        Walk(n);
        return list;
    }

    private void Insert(Node n) => Exec(@"INSERT INTO nodes(id,parent_id,name,remote_name,is_dir,size,remote_id,remote_parent,remote_hash,remote_mtime,ctime,atime,mtime,chtime,attrs,version,uploaded_version,children_loaded,pinned)
        VALUES($id,$p,$n,$rn,$d,$s,$rid,$rp,$rh,$rm,$c,$a,$m,$ch,$at,$v,$uv,$cl,$pin)", NodeParams(n));

    public void Save(Node n)
    {
        if (n.Removed) return;
        Exec(@"UPDATE nodes SET parent_id=$p,name=$n,remote_name=$rn,is_dir=$d,size=$s,remote_id=$rid,remote_parent=$rp,remote_hash=$rh,remote_mtime=$rm,
            ctime=$c,atime=$a,mtime=$m,chtime=$ch,attrs=$at,version=$v,uploaded_version=$uv,children_loaded=$cl,pinned=$pin WHERE id=$id", NodeParams(n));
    }

    public void SaveMany(IEnumerable<Node> nodes)
    {
        using var tx = Transaction();
        foreach (var n in nodes) Save(n);
        tx.Commit();
    }

    /// <summary>Runs <paramref name="action"/> in one SQLite transaction (bulk inserts during directory listing).</summary>
    public void Batch(Action action)
    {
        using var tx = Transaction();
        action();
        tx.Commit();
    }

    private static (string, object?)[] NodeParams(Node n) => new (string, object?)[]
    {
        ("$id", n.Id), ("$p", n.ParentId), ("$n", n.Name), ("$rn", n.RemoteName), ("$d", n.IsDirectory ? 1 : 0), ("$s", n.Size),
        ("$rid", n.RemoteId), ("$rp", n.RemoteParentId), ("$rh", n.RemoteHash), ("$rm", n.RemoteModifiedTicks), ("$c", (long)n.CreationTime),
        ("$a", (long)n.LastAccessTime), ("$m", (long)n.LastWriteTime), ("$ch", (long)n.ChangeTime), ("$at", (long)n.Attributes),
        ("$v", n.Version), ("$uv", n.UploadedVersion), ("$cl", n.ChildrenLoadedTicks), ("$pin", n.Pinned ? 1 : 0),
    };

    // ------------------------------------------------------------------ key/value

    public string? GetValue(string key)
    {
        using var cmd = Cmd();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetValue(string key, string? value)
    {
        if (value == null) Exec("DELETE FROM kv WHERE key=$k", ("$k", key));
        else Exec("INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    // ------------------------------------------------------------------ operation journal

    public long EnqueueOp(string kind, long nodeId, string data)
    {
        var now = DateTime.UtcNow.Ticks;
        Exec("INSERT INTO ops(kind,node_id,data,state,attempts,last_error,next_attempt,created,updated) VALUES($k,$n,$d,'Pending',0,'',0,$t,$t)",
            ("$k", kind), ("$n", nodeId), ("$d", data), ("$t", now));
        using var cmd = Cmd();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    public List<OpRecord> Ops()
    {
        var list = new List<OpRecord>();
        using var cmd = Cmd();
        cmd.CommandText = "SELECT id,kind,node_id,data,state,attempts,last_error,next_attempt,created,updated FROM ops ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OpRecord
            {
                Id = r.GetInt64(0), Kind = r.GetString(1), NodeId = r.GetInt64(2), Data = r.GetString(3), State = r.GetString(4),
                Attempts = (int)r.GetInt64(5), LastError = r.GetString(6), NextAttemptTicks = r.GetInt64(7), CreatedTicks = r.GetInt64(8), UpdatedTicks = r.GetInt64(9),
            });
        return list;
    }

    public void UpdateOp(OpRecord op)
    {
        op.UpdatedTicks = DateTime.UtcNow.Ticks;
        Exec("UPDATE ops SET state=$s,attempts=$a,last_error=$e,next_attempt=$n,updated=$u,data=$d WHERE id=$id",
            ("$s", op.State), ("$a", op.Attempts), ("$e", op.LastError), ("$n", op.NextAttemptTicks), ("$u", op.UpdatedTicks), ("$d", op.Data), ("$id", op.Id));
    }

    public void DeleteOp(long id) => Exec("DELETE FROM ops WHERE id=$id", ("$id", id));

    public int CountOps()
    {
        using var cmd = Cmd();
        cmd.CommandText = "SELECT COUNT(*) FROM ops";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void ResetFailedOps() => Exec("UPDATE ops SET state='Pending', next_attempt=0 WHERE state<>'Pending'");

    // ------------------------------------------------------------------ resumable uploads

    public (long Version, string Session)? GetUploadSession(long nodeId)
    {
        using var cmd = Cmd();
        cmd.CommandText = "SELECT version, session FROM upload_sessions WHERE node_id=$n";
        cmd.Parameters.AddWithValue("$n", nodeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1)) : null;
    }

    public void SetUploadSession(long nodeId, long version, string session)
        => Exec("INSERT INTO upload_sessions(node_id,version,session,updated) VALUES($n,$v,$s,$u) ON CONFLICT(node_id) DO UPDATE SET version=excluded.version, session=excluded.session, updated=excluded.updated",
            ("$n", nodeId), ("$v", version), ("$s", session), ("$u", DateTime.UtcNow.Ticks));

    public void DeleteUploadSession(long nodeId) => Exec("DELETE FROM upload_sessions WHERE node_id=$n", ("$n", nodeId));

    // ------------------------------------------------------------------ cache index

    public CacheFileRecord? GetCacheRecord(long nodeId)
    {
        using var cmd = Cmd();
        cmd.CommandText = "SELECT node_id,size,bitmap,cached_bytes,last_access,complete FROM cache_files WHERE node_id=$n";
        cmd.Parameters.AddWithValue("$n", nodeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCache(r) : null;
    }

    public List<CacheFileRecord> AllCacheRecords()
    {
        var list = new List<CacheFileRecord>();
        using var cmd = Cmd();
        cmd.CommandText = "SELECT node_id,size,bitmap,cached_bytes,last_access,complete FROM cache_files";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadCache(r));
        return list;
    }

    private static CacheFileRecord ReadCache(SqliteDataReader r) => new()
    {
        NodeId = r.GetInt64(0), Size = r.GetInt64(1), Bitmap = (byte[])r.GetValue(2), CachedBytes = r.GetInt64(3),
        LastAccessTicks = r.GetInt64(4), Complete = r.GetInt64(5) != 0,
    };

    public void SaveCacheRecord(CacheFileRecord rec)
        => Exec("INSERT INTO cache_files(node_id,size,bitmap,cached_bytes,last_access,complete) VALUES($n,$s,$b,$c,$l,$x) ON CONFLICT(node_id) DO UPDATE SET size=excluded.size,bitmap=excluded.bitmap,cached_bytes=excluded.cached_bytes,last_access=excluded.last_access,complete=excluded.complete",
            ("$n", rec.NodeId), ("$s", rec.Size), ("$b", rec.Bitmap), ("$c", rec.CachedBytes), ("$l", rec.LastAccessTicks), ("$x", rec.Complete ? 1 : 0));

    public void DeleteCacheRecord(long nodeId) => Exec("DELETE FROM cache_files WHERE node_id=$n", ("$n", nodeId));

    // ------------------------------------------------------------------ helpers

    private SqliteTransaction? _tx;
    private int _txDepth;

    private SqliteCommand Cmd()
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        return cmd;
    }

    /// <summary>Re-entrant transaction scope: only the outermost scope commits.</summary>
    private TxScope Transaction()
    {
        if (_txDepth++ == 0) _tx = _db.BeginTransaction();
        return new TxScope(this);
    }

    private sealed class TxScope : IDisposable
    {
        private readonly MetadataStore _s;
        private bool _committed, _disposed;
        public TxScope(MetadataStore s) => _s = s;
        public void Commit() => _committed = true;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--_s._txDepth == 0)
            {
                var tx = _s._tx!;
                _s._tx = null;
                if (_committed) tx.Commit(); else tx.Rollback();
                tx.Dispose();
            }
        }
    }

    private int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public void Checkpoint()
    {
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (Exception ex) { Log.Warn("WAL checkpoint failed", ex); }
    }

    public void Dispose()
    {
        try { Checkpoint(); } catch { }
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
