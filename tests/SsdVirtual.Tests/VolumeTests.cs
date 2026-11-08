using System.Security.Cryptography;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.Database;
using SsdVirtual.Sync;

namespace SsdVirtual.Tests;

/// <summary>
/// End-to-end tests of the volume (namespace + cache + sync) against a real folder-backed provider.
/// They exercise the same code paths the WinFsp file system uses, without needing a mounted drive.
/// </summary>
public class VolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssdv-vol-" + Guid.NewGuid().ToString("N"));
    private string Cloud => Path.Combine(_root, "cloud");
    private string CacheDir => Path.Combine(_root, "cache");

    private (Volume V, SyncEngine S, LocalFolderProvider P) Open(long quota = 1L << 30, bool offline = false)
    {
        var account = new AccountConfig { Provider = ProviderKind.LocalFolder, DisplayName = "Test", DriveLetter = 'Q', CacheQuotaBytes = quota, LocalFolderPath = Cloud };
        var settings = new AppSettings { UploadQuietSeconds = 0, SyncIntervalSeconds = 15, MaxParallelTransfers = 4 };
        var provider = new LocalFolderProvider(Cloud) { SimulateOffline = offline };
        var v = new Volume(account, settings, provider, CacheDir);
        var s = new SyncEngine(v);
        s.Start();
        return (v, s, provider);
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }

    private static void WriteFile(Volume v, Node parent, string name, byte[] data)
    {
        var n = v.CreateFile(parent, name, 0);
        v.HandleOpened(n);
        for (int off = 0; off < data.Length; off += 1 << 20)
            v.Write(n, off, data.AsSpan(off, Math.Min(1 << 20, data.Length - off)));
        v.HandleClosed(n, true);
    }

    private static byte[] ReadAll(Volume v, Node n)
    {
        var buf = new byte[n.Size];
        int off = 0;
        while (off < buf.Length) off += v.Read(n, off, buf.AsSpan(off, Math.Min(1 << 20, buf.Length - off)));
        return buf;
    }

    [Fact]
    public async Task LocalChangesAreUploadedAndVerified()
    {
        var (v, s, _) = Open();
        using (v) using (s)
        {
            var dir = v.CreateDirectory(v.Store.Root, "Docs", 0);
            var small = RandomBytes(5000);
            var big = RandomBytes(9 * 1024 * 1024 + 123);
            WriteFile(v, dir, "small.txt", small);
            WriteFile(v, dir, "big.bin", big);
            WriteFile(v, v.Store.Root, "empty.dat", Array.Empty<byte>());
            Assert.True(await s.WaitIdleAsync(TimeSpan.FromSeconds(60)), "sync did not finish");
            Assert.Equal(small, File.ReadAllBytes(Path.Combine(Cloud, "Docs", "small.txt")));
            Assert.Equal(big, File.ReadAllBytes(Path.Combine(Cloud, "Docs", "big.bin")));
            Assert.True(File.Exists(Path.Combine(Cloud, "empty.dat")));
            Assert.Empty(v.Store.DirtyFiles());
        }
    }

    [Fact]
    public async Task RenameMoveDeleteReachTheCloud()
    {
        var (v, s, _) = Open();
        using (v) using (s)
        {
            var a = v.CreateDirectory(v.Store.Root, "A", 0);
            var b = v.CreateDirectory(v.Store.Root, "B", 0);
            WriteFile(v, a, "one.txt", RandomBytes(100));
            WriteFile(v, a, "two.txt", RandomBytes(200));
            Assert.True(await s.WaitIdleAsync(TimeSpan.FromSeconds(60)));

            var one = v.Lookup("\\A\\one.txt")!;
            Assert.True(v.Rename(one, b, "uno.txt", false));
            v.Rename(a, v.Store.Root, "A2", false);
            v.Delete(v.Lookup("\\A2\\two.txt")!);
            Assert.True(await s.WaitIdleAsync(TimeSpan.FromSeconds(60)));

            Assert.True(File.Exists(Path.Combine(Cloud, "B", "uno.txt")));
            Assert.True(Directory.Exists(Path.Combine(Cloud, "A2")));
            Assert.False(Directory.Exists(Path.Combine(Cloud, "A")));
            Assert.False(File.Exists(Path.Combine(Cloud, "A2", "two.txt")));
        }
    }

    [Fact]
    public async Task CloudFilesAreListedAndReadThroughTheCache()
    {
        Directory.CreateDirectory(Path.Combine(Cloud, "Games", "Sample"));
        var data = RandomBytes(7 * 1024 * 1024 + 77);
        File.WriteAllBytes(Path.Combine(Cloud, "Games", "Sample", "data.pak"), data);
        var (v, s, p) = Open();
        using (v) using (s)
        {
            await v.EnsureRootAsync(CancellationToken.None);
            var node = v.Lookup("\\Games\\Sample\\data.pak");
            Assert.NotNull(node);
            Assert.Equal(data.Length, node!.Size);
            // Random read in the middle, then the whole file.
            var mid = new byte[4096];
            v.Read(node, 3 * 1024 * 1024 + 5, mid);
            Assert.Equal(data.AsSpan(3 * 1024 * 1024 + 5, 4096).ToArray(), mid);
            Assert.Equal(data, ReadAll(v, node));
            var callsAfterFirst = p.ReadCalls;
            Assert.Equal(data, ReadAll(v, node)); // second pass is served from the cache
            Assert.Equal(callsAfterFirst, p.ReadCalls);
            Assert.True(v.Cache.IsFullyCached(node, node.Size));
        }
    }

    [Fact]
    public async Task OfflineReadsUseCacheAndWritesAreQueued()
    {
        Directory.CreateDirectory(Cloud);
        var cached = RandomBytes(2 * 1024 * 1024);
        var uncached = RandomBytes(3 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(Cloud, "cached.bin"), cached);
        File.WriteAllBytes(Path.Combine(Cloud, "uncached.bin"), uncached);
        var (v, s, p) = Open();
        using (v) using (s)
        {
            await v.EnsureRootAsync(CancellationToken.None);
            var c = v.Lookup("\\cached.bin")!;
            var u = v.Lookup("\\uncached.bin")!;
            Assert.Equal(cached, ReadAll(v, c));

            p.SimulateOffline = true;
            Assert.Equal(cached, ReadAll(v, c)); // cached data still readable
            var ex = Assert.ThrowsAny<Exception>(() => ReadAll(v, u));
            Assert.True(ex is CloudException, ex.ToString());
            WriteFile(v, v.Store.Root, "written-offline.txt", RandomBytes(1234));
            await Task.Delay(3000);
            Assert.False(File.Exists(Path.Combine(Cloud, "written-offline.txt")));
            Assert.NotEmpty(v.Store.DirtyFiles());

            p.SimulateOffline = false;
            s.SyncNow();
            Assert.True(await s.WaitIdleAsync(TimeSpan.FromSeconds(90)), "queued change was not uploaded after reconnect");
            Assert.True(File.Exists(Path.Combine(Cloud, "written-offline.txt")));
        }
    }

    [Fact]
    public async Task PartialOverwriteOfCloudFileKeepsTheRest()
    {
        Directory.CreateDirectory(Cloud);
        var data = RandomBytes(5 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(Cloud, "save.dat"), data);
        var (v, s, _) = Open();
        using (v) using (s)
        {
            await v.EnsureRootAsync(CancellationToken.None);
            var n = v.Lookup("\\save.dat")!;
            v.HandleOpened(n);
            var patch = RandomBytes(100);
            v.Write(n, 2 * 1024 * 1024 + 10, patch); // touches one block that was never downloaded
            v.HandleClosed(n, true);
            Array.Copy(patch, 0, data, 2 * 1024 * 1024 + 10, patch.Length);
            Assert.True(await s.WaitIdleAsync(TimeSpan.FromSeconds(60)));
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(Cloud, "save.dat")));
        }
    }

    [Fact]
    public async Task UnsyncedChangesSurviveRestart()
    {
        var data = RandomBytes(300_000);
        var (v, s, _) = Open(offline: true);
        WriteFile(v, v.Store.Root, "pending.bin", data);
        s.Dispose();
        v.Dispose();

        var (v2, s2, _) = Open();
        using (v2) using (s2)
        {
            var n = v2.Lookup("\\pending.bin")!;
            Assert.True(n.IsDirty);
            Assert.Equal(data, ReadAll(v2, n));
            Assert.True(await s2.WaitIdleAsync(TimeSpan.FromSeconds(60)));
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(Cloud, "pending.bin")));
        }
    }

    [Fact]
    public async Task ConflictKeepsBothVersions()
    {
        Directory.CreateDirectory(Cloud);
        File.WriteAllText(Path.Combine(Cloud, "notes.txt"), "original");
        var (v, s, p) = Open();
        using (v) using (s)
        {
            await v.EnsureRootAsync(CancellationToken.None);
            var n = v.Lookup("\\notes.txt")!;
            p.SimulateOffline = true; // keep the local edit from uploading
            v.HandleOpened(n);
            v.SetSize(n, 0);
            v.Write(n, 0, "local edit"u8);
            v.HandleClosed(n, true);
            File.WriteAllText(Path.Combine(Cloud, "notes.txt"), "remote edit, longer");
            p.SimulateOffline = false;
            v.ResetConnection();
            await v.ListFromCloudAsync(v.Store.Root, CancellationToken.None);
            Assert.True(await s.WaitIdleAsync(TimeSpan.FromSeconds(60)));
            var files = Directory.GetFiles(Cloud).Select(Path.GetFileName).ToList();
            Assert.Contains("notes.txt", files);
            Assert.Contains(files, f => f!.StartsWith("notes (Conflict "));
            Assert.Equal("remote edit, longer", File.ReadAllText(Path.Combine(Cloud, "notes.txt")));
            var conflict = files.First(f => f!.StartsWith("notes (Conflict "))!;
            Assert.Equal("local edit", File.ReadAllText(Path.Combine(Cloud, conflict)));
        }
    }

    [Fact]
    public async Task EvictionNeverDropsDirtyOrPinnedData()
    {
        Directory.CreateDirectory(Cloud);
        for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(Cloud, $"f{i}.bin"), RandomBytes(2 * 1024 * 1024 + i));
        var (v, s, _) = Open(quota: 3 * 1024 * 1024);
        using (v) using (s)
        {
            await v.EnsureRootAsync(CancellationToken.None);
            var pinned = v.Lookup("\\f0.bin")!;
            v.SetPinned(pinned, true);
            foreach (var i in Enumerable.Range(0, 4)) ReadAll(v, v.Lookup($"\\f{i}.bin")!);
            v.Cache.PersistDirty();
            v.Cache.EnforceQuota();
            Assert.True(v.Cache.IsFullyCached(pinned, pinned.Size));
            Assert.True(v.Cache.UsedBytes <= 3 * 1024 * 1024 + 2 * 1024 * 1024 + 16, $"used {v.Cache.UsedBytes}");
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (int i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch { Thread.Sleep(300); }
        }
    }
}
