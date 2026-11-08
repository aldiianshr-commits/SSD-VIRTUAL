using System.Security.Cryptography;
using SsdVirtual.Cloud;
using SsdVirtual.Core;
using SsdVirtual.GameStorage;
using SsdVirtual.Sync;
using SsdVirtual.VirtualDrive;
using Xunit.Abstractions;

namespace SsdVirtual.Tests;

/// <summary>
/// Mounts real WinFsp drives and uses them through the ordinary Windows file APIs (what File Explorer,
/// Steam and every other application use). Requires WinFsp and administrator rights; CI sets
/// SSDV_REQUIRE_WINFSP=1 so the tests fail (instead of being skipped) when WinFsp is missing.
/// </summary>
[Collection("WinFsp")]
public class WinFspDriveTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssdv-fs-" + Guid.NewGuid().ToString("N"));
    private readonly List<MountedDrive> _drives = new();

    public WinFspDriveTests(ITestOutputHelper output) => _out = output;

    private static bool WinFspAvailable(out string version)
    {
        try { version = Fsp.FileSystemHost.Version().ToString(); return true; }
        catch (Exception ex) { version = ex.Message; return false; }
    }

    private bool Ready()
    {
        if (WinFspAvailable(out var v)) { _out.WriteLine("WinFsp " + v); return true; }
        if (Environment.GetEnvironmentVariable("SSDV_REQUIRE_WINFSP") == "1") Assert.Fail("WinFsp is required but not available: " + v);
        _out.WriteLine("WinFsp not installed; skipping drive test.");
        return false;
    }

    private static char FreeLetter(params char[] exclude)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).Concat(exclude).ToHashSet();
        for (var c = 'Z'; c >= 'G'; c--) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("No free drive letter");
    }

    private (MountedDrive Drive, string Cloud, LocalFolderProvider Provider) Mount(string name, char letter, string? cloud = null)
    {
        cloud ??= Path.Combine(_root, name + "-cloud");
        Directory.CreateDirectory(cloud);
        var account = new AccountConfig
        {
            Id = name, Provider = ProviderKind.LocalFolder, DisplayName = name, DriveLetter = letter, LocalFolderPath = cloud,
            CacheQuotaBytes = 4L * 1024 * 1024 * 1024,
        };
        var provider = new LocalFolderProvider(cloud);
        var volume = new Volume(account, new AppSettings { UploadQuietSeconds = 0, SyncIntervalSeconds = 15 }, provider, Path.Combine(_root, name + "-cache"));
        var drive = new MountedDrive(account, volume);
        drive.StartSync();
        Assert.True(drive.Mount(), "mount failed: " + drive.MountError);
        _drives.Add(drive);
        return (drive, cloud, provider);
    }

    private static string Md5(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(fs));
    }

    [Fact]
    public async Task DriveBehavesLikeALocalDisk()
    {
        if (!Ready()) return;
        var letter = FreeLetter();
        var (drive, cloud, _) = Mount("Disk1", letter);
        var root = letter + ":\\";

        // Visible as a drive with a label, size and free space.
        var info = new DriveInfo(root);
        Assert.True(info.IsReady);
        _out.WriteLine($"{root} type={info.DriveType} fs={info.DriveFormat} label={info.VolumeLabel} total={info.TotalSize} free={info.AvailableFreeSpace}");
        Assert.Equal("NTFS", info.DriveFormat);
        Assert.Equal("SsdVirtual - Disk1", info.VolumeLabel);
        Assert.True(info.TotalSize > 0);
        Assert.Contains(DriveInfo.GetDrives(), d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));

        // Folders, files, read/write/modify.
        Directory.CreateDirectory(root + @"Games\SteamLibrary\steamapps\common");
        File.WriteAllText(root + "x.txt", "hello");
        Directory.CreateDirectory(root + "Documents");
        File.WriteAllText(root + @"Documents\note.txt", "first");
        File.AppendAllText(root + @"Documents\note.txt", " second");
        Assert.Equal("first second", File.ReadAllText(root + @"Documents\note.txt"));
        Assert.Equal("hello", File.ReadAllText(root + "x.txt"));

        // Large file with random content.
        var big = root + @"Videos\movie.mkv";
        Directory.CreateDirectory(root + "Videos");
        var data = new byte[64 * 1024 * 1024 + 4321];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(big, data);
        Assert.Equal(Convert.ToHexString(MD5.HashData(data)), Md5(big));

        // Random access writes inside the file.
        using (var fs = new FileStream(big, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = 10_000_000;
            fs.Write(new byte[] { 1, 2, 3, 4 });
            data[10_000_000] = 1; data[10_000_001] = 2; data[10_000_002] = 3; data[10_000_003] = 4;
        }
        Assert.Equal(Convert.ToHexString(MD5.HashData(data)), Md5(big));

        // Rename, move, copy, delete.
        File.Move(root + @"Documents\note.txt", root + @"Documents\renamed.txt");
        File.Copy(root + @"Documents\renamed.txt", root + @"Documents\copy.txt");
        File.Move(root + "x.txt", root + @"Games\x.txt");
        Directory.Move(root + "Documents", root + "Docs");
        Assert.True(File.Exists(root + @"Docs\copy.txt"));
        File.Delete(root + @"Docs\copy.txt");
        Assert.False(File.Exists(root + @"Docs\copy.txt"));
        Assert.Throws<IOException>(() => Directory.Delete(root + "Docs"));
        Directory.Delete(root + "Docs", true);
        Assert.False(Directory.Exists(root + "Docs"));

        // Many small files (game-like workload).
        var many = root + @"Games\Small";
        Directory.CreateDirectory(many);
        for (int i = 0; i < 1500; i++) File.WriteAllText(Path.Combine(many, $"asset{i:0000}.dat"), new string('x', i % 300));
        var listed = Directory.GetFiles(many).Select(Path.GetFileName).ToList();
        var expectedNames = Enumerable.Range(0, 1500).Select(i => $"asset{i:0000}.dat").ToHashSet();
        foreach (var extra in listed.Where(n => !expectedNames.Contains(n!))) _out.WriteLine("unexpected: " + extra);
        foreach (var dup in listed.GroupBy(n => n).Where(g => g.Count() > 1)) _out.WriteLine("duplicate: " + dup.Key);
        Assert.True(listed.Count == 1500, $"listed {listed.Count}: extra=[{string.Join(",", listed.Where(n => !expectedNames.Contains(n!)))}] dups=[{string.Join(",", listed.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key))}]");

        // Everything reaches the cloud.
        Assert.True(await drive.Sync.WaitIdleAsync(TimeSpan.FromMinutes(5)), "sync did not finish");
        Assert.Equal(Convert.ToHexString(MD5.HashData(data)), Md5(Path.Combine(cloud, "Videos", "movie.mkv")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(cloud, "Games", "x.txt")));
        Assert.Equal(1500, Directory.GetFiles(Path.Combine(cloud, "Games", "Small")).Length);
        Assert.False(Directory.Exists(Path.Combine(cloud, "Docs")));
        Assert.False(Directory.Exists(Path.Combine(cloud, "Documents")));
    }

    [Fact]
    public async Task SteamStyleLibraryWorks()
    {
        if (!Ready()) return;
        var letter = FreeLetter();
        var (drive, _, _) = Mount("Steam", letter);
        var root = letter + ":\\";
        var lib = GameScanner.CreateSteamLibrary(root).LibraryPath;
        var steamapps = Path.Combine(lib, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Test Game"));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_480.acf"), "\"AppState\"\n{\n \"appid\" \"480\"\n \"name\" \"Spacewar\"\n \"installdir\" \"Test Game\"\n \"SizeOnDisk\" \"1000\"\n}\n");

        // Steam pre-allocates files, writes chunks out of order and verifies them.
        var pak = Path.Combine(steamapps, "common", "Test Game", "data.pak");
        using (var fs = new FileStream(pak, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            fs.SetLength(32L * 1024 * 1024);
            var chunk = new byte[1024 * 1024];
            for (int i = 31; i >= 0; i -= 2) { new Random(i).NextBytes(chunk); fs.Position = (long)i * chunk.Length; fs.Write(chunk); }
            for (int i = 30; i >= 0; i -= 2) { new Random(i).NextBytes(chunk); fs.Position = (long)i * chunk.Length; fs.Write(chunk); }
        }
        using (var fs = new FileStream(pak, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var buf = new byte[1024 * 1024];
            var expected = new byte[1024 * 1024];
            for (int i = 0; i < 32; i++)
            {
                fs.ReadExactly(buf);
                new Random(i).NextBytes(expected);
                Assert.True(buf.AsSpan().SequenceEqual(expected), $"chunk {i} differs");
            }
        }
        // A file open for writing by another handle (Steam keeps files locked).
        using (var locked = new FileStream(Path.Combine(steamapps, "lock.tmp"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(steamapps, "lock.tmp"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        }
        File.Delete(Path.Combine(steamapps, "lock.tmp"));

        var games = GameScanner.ScanSteam(root);
        Assert.Contains(games, g => g.AppId == "480" && g.Name == "Spacewar");
        Assert.True(await drive.Sync.WaitIdleAsync(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task AccountsAreIsolatedAndSurviveRemountAndOutages()
    {
        if (!Ready()) return;
        var l1 = FreeLetter();
        var l2 = FreeLetter(l1);
        var (d1, cloud1, p1) = Mount("Iso1", l1);
        var (d2, cloud2, _) = Mount("Iso2", l2);
        File.WriteAllText($@"{l1}:\only-one.txt", "1");
        File.WriteAllText($@"{l2}:\only-two.txt", "2");
        Assert.False(File.Exists($@"{l2}:\only-one.txt"));
        Assert.False(File.Exists($@"{l1}:\only-two.txt"));
        Assert.True(await d1.Sync.WaitIdleAsync(TimeSpan.FromMinutes(2)));
        Assert.True(await d2.Sync.WaitIdleAsync(TimeSpan.FromMinutes(2)));
        Assert.True(File.Exists(Path.Combine(cloud1, "only-one.txt")) && !File.Exists(Path.Combine(cloud1, "only-two.txt")));
        Assert.True(File.Exists(Path.Combine(cloud2, "only-two.txt")) && !File.Exists(Path.Combine(cloud2, "only-one.txt")));

        // A file that is pinned (fully cached) stays readable while the cloud is unreachable.
        var payload = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(cloud1, "game.bin"), payload);
        var node = d1.Volume.Lookup("\\game.bin");
        if (node == null) { await d1.Volume.ListFromCloudAsync(d1.Volume.Store.Root, CancellationToken.None); node = d1.Volume.Lookup("\\game.bin"); }
        Assert.NotNull(node);
        d1.Volume.SetPinned(node!, true);
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (!d1.Volume.Cache.IsFullyCached(node!, node!.Size) && DateTime.UtcNow < deadline) await Task.Delay(250);
        Assert.True(d1.Volume.Cache.IsFullyCached(node!, node!.Size));
        p1.SimulateOffline = true;
        Assert.Equal(payload, File.ReadAllBytes($@"{l1}:\game.bin"));
        File.WriteAllText($@"{l1}:\offline-note.txt", "written while offline");
        Assert.True(Directory.Exists($@"{l1}:\"), "drive must stay mounted while offline");
        p1.SimulateOffline = false;
        d1.Sync.SyncNow();
        Assert.True(await d1.Sync.WaitIdleAsync(TimeSpan.FromMinutes(2)));
        Assert.Equal("written while offline", File.ReadAllText(Path.Combine(cloud1, "offline-note.txt")));

        // Remount (like a reboot): the namespace and data come back from the local database and cache.
        d1.Unmount();
        Assert.False(Directory.Exists($@"{l1}:\"));
        Assert.True(d1.Mount(), d1.MountError);
        Assert.Equal("1", File.ReadAllText($@"{l1}:\only-one.txt"));
        Assert.Equal(payload, File.ReadAllBytes($@"{l1}:\game.bin"));
    }

    public void Dispose()
    {
        foreach (var d in _drives)
        {
            try { d.Dispose(); } catch (Exception ex) { _out.WriteLine("dispose: " + ex.Message); }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }
}

[CollectionDefinition("WinFsp", DisableParallelization = true)]
public class WinFspCollection { }
