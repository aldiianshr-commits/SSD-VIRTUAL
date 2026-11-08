using System.Security.Cryptography;
using System.Text.Json;
using SsdVirtual.Cache;
using SsdVirtual.Core;
using SsdVirtual.Database;
using SsdVirtual.GameStorage;
using SsdVirtual.Security;
using SsdVirtual.TeraBox;

namespace SsdVirtual.Tests;

public class BlockBitmapTests
{
    [Fact]
    public void SetGetClearAndCount()
    {
        var b = new BlockBitmap();
        b.Set(0); b.Set(7); b.Set(8); b.Set(1000);
        Assert.True(b.Get(0) && b.Get(7) && b.Get(8) && b.Get(1000));
        Assert.False(b.Get(1) || b.Get(999) || b.Get(5000));
        Assert.Equal(4, b.CountSet(2000));
        Assert.Equal(3, b.CountSet(9));
        b.Clear(7);
        Assert.False(b.Get(7));
        b.TruncateFrom(8);
        Assert.False(b.Get(8) || b.Get(1000));
        Assert.True(b.Get(0));
        var round = new BlockBitmap(b.ToArray());
        Assert.True(round.Get(0));
    }
}

public class VirtualPathTests
{
    [Theory]
    [InlineData("a:b", "a_b")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("", "_")]
    public void SanitizesCloudNames(string input, string expected) => Assert.Equal(expected, VirtualPath.SanitizeName(input));

    [Fact]
    public void PathHelpers()
    {
        Assert.Equal(new[] { "a", "b" }, VirtualPath.Split("\\a\\b"));
        Assert.Empty(VirtualPath.Split("\\"));
        Assert.Equal("\\a", VirtualPath.GetParent("\\a\\b"));
        Assert.Equal("\\", VirtualPath.GetParent("\\a"));
        Assert.Equal("b", VirtualPath.GetName("\\a\\b"));
        Assert.Equal("report (2).pdf", VirtualPath.WithSuffix("report.pdf", "2"));
        Assert.Equal("folder (2)", VirtualPath.WithSuffix("folder", "2"));
        Assert.Equal("/root/x/y", VirtualPath.ToRemote("root", "\\x\\y"));
        Assert.Equal("/", VirtualPath.ToRemote("", "\\"));
    }
}

public class MetadataStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ssdv-meta-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TreeOperationsPersistAcrossReopen()
    {
        var db = Path.Combine(_dir, "m.db");
        long fileId;
        using (var s = new MetadataStore(db))
        {
            lock (s.Gate)
            {
                var games = s.CreateNode(s.Root, "Games", true);
                var file = s.CreateNode(games, "Game.exe", false, n => { n.Size = 123; n.RemoteId = "r1"; n.Version = 2; n.UploadedVersion = 1; });
                fileId = file.Id;
                Assert.Same(file, s.Find("\\games\\GAME.EXE")); // case-insensitive
                Assert.Same(file, s.FindByRemoteId("r1"));
                Assert.Throws<IOException>(() => s.CreateNode(games, "game.exe", false));
                var other = s.CreateNode(s.Root, "Other", true);
                s.Move(file, other, "Renamed.exe");
                Assert.Equal("\\Other\\Renamed.exe", s.PathOf(file));
                s.SetValue("k", "v");
                var op = s.EnqueueOp("move", file.Id, "{}");
                Assert.True(op > 0);
                s.SaveCacheRecord(new CacheFileRecord { NodeId = file.Id, Size = 123, Bitmap = new byte[] { 1 }, CachedBytes = 100, Complete = false });
            }
        }
        using (var s = new MetadataStore(db))
        {
            lock (s.Gate)
            {
                var file = s.Find("\\Other\\Renamed.exe");
                Assert.NotNull(file);
                Assert.Equal(fileId, file!.Id);
                Assert.True(file.IsDirty);
                Assert.Equal(123, file.Size);
                Assert.Equal("v", s.GetValue("k"));
                Assert.Single(s.Ops());
                Assert.Equal(100, s.GetCacheRecord(fileId)!.CachedBytes);
                var removed = s.Remove(s.Find("\\Other")!);
                Assert.Equal(2, removed.Count);
                Assert.Null(s.GetCacheRecord(fileId));
                Assert.Null(s.Find("\\Other"));
            }
        }
    }

    [Fact]
    public void RemotePathFollowsCloudLocationUntilSynced()
    {
        using var s = new MetadataStore(Path.Combine(_dir, "r.db"));
        lock (s.Gate)
        {
            var a = s.CreateNode(s.Root, "A", true, n => { n.RemoteId = "a"; n.RemoteName = "A"; n.RemoteParentId = s.Root.Id; });
            var b = s.CreateNode(s.Root, "B", true, n => { n.RemoteId = "b"; n.RemoteName = "B"; n.RemoteParentId = s.Root.Id; });
            var f = s.CreateNode(a, "f.txt", false, n => { n.RemoteId = "f"; n.RemoteName = "f.txt"; n.RemoteParentId = a.Id; });
            s.Move(f, b, "g.txt");
            // Locally moved, but the cloud still has it at /A/f.txt until the move is synchronized.
            Assert.Equal("/A/f.txt", s.RemotePathOf(f));
            Assert.Equal("\\B\\g.txt", s.PathOf(f));
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class VdfTests
{
    private const string Manifest = """
        "AppState"
        {
            "appid"		"620"
            "Universe"		"1"
            "name"		"Portal 2"
            "StateFlags"		"4"
            "installdir"		"Portal 2"
            "SizeOnDisk"		"12884901888"
            "InstalledDepots"
            {
                "621" { "manifest" "123" "size" "100" }
            }
        }
        """;

    [Fact]
    public void ParsesAppManifest()
    {
        var g = GameScanner.ParseManifest(Manifest, @"E:\SteamLibrary")!;
        Assert.Equal("620", g.AppId);
        Assert.Equal("Portal 2", g.Name);
        Assert.Equal(12884901888, g.SizeOnDisk);
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Portal 2", g.InstallPath);
        var root = VdfNode.Parse(Manifest);
        Assert.Equal("100", root.Child("AppState")!.Child("InstalledDepots")!.Child("621")!.Get("size"));
    }
}

public class TeraBoxTests
{
    [Fact]
    public void DownloadSignatureMatchesReferenceImplementation()
        => Assert.Equal("ZgCZ52LKP43xDhvqMPK5Gg==", TeraBoxProvider.Sign("d76b7c0f9a2e", "1a2b3c4d5e6f7g8h"));

    [Theory]
    [InlineData(1024L, 1024L)]
    [InlineData(100L * 1024 * 1024, 4L * 1024 * 1024)]
    [InlineData(5L * 1024 * 1024 * 1024, 8L * 1024 * 1024)]
    public void ChunkSizes(long length, long expected) => Assert.Equal(expected, TeraBoxProvider.ChunkSizeFor(length));
}

public class SecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ssdv-sec-" + Guid.NewGuid().ToString("N"));

    public SecurityTests()
    {
        ProtectedFile.Scope = DataProtectionScope.CurrentUser;
        ProtectedFile.RestrictAcl = false;
    }

    [Fact]
    public void SecretsAreEncryptedAndRoundTrip()
    {
        var path = Path.Combine(_dir, "secrets.dat");
        var store = new SecretStore(path);
        store.Set("acc1", new AccountSecrets { RefreshToken = "very-secret-refresh-token", Cookie = "ndus=abc" });
        store.Set("acc2", new AccountSecrets { RefreshToken = "other" });
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("very-secret-refresh-token", raw);
        var reopened = new SecretStore(path);
        Assert.Equal("very-secret-refresh-token", reopened.Get("acc1").RefreshToken);
        Assert.Equal("ndus=abc", reopened.Get("acc1").Cookie);
        reopened.Remove("acc1");
        Assert.Equal("", new SecretStore(path).Get("acc1").RefreshToken);
        Assert.Equal("other", new SecretStore(path).Get("acc2").RefreshToken); // removing one account never touches another
    }

    [Fact]
    public void ConfigRoundTrip()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.dat"));
        var cfg = new ServiceConfig();
        cfg.Accounts.Add(new AccountConfig { Provider = ProviderKind.GoogleDrive, DisplayName = "Google 01", DriveLetter = 'G' });
        store.Save(cfg);
        var back = store.Load();
        Assert.Equal('G', back.Accounts[0].DriveLetter);
        Assert.Equal("SsdVirtual - Google 01", back.Accounts[0].VolumeLabel);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class LocalizationTests
{
    private static string StringsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SsdVirtual.Desktop", "Strings"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SsdVirtual.Desktop", "Strings");
    }

    [Fact]
    public void IndonesianAndEnglishHaveTheSameKeys()
    {
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(StringsDir(), "en.json")))!;
        var id = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(StringsDir(), "id.json")))!;
        Assert.Empty(en.Keys.Except(id.Keys));
        Assert.Empty(id.Keys.Except(en.Keys));
        Assert.All(id, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
        // Placeholders must match so string.Format never throws.
        foreach (var k in en.Keys)
            for (int i = 0; i < 5; i++)
                Assert.Equal(en[k].Contains("{" + i + "}"), id[k].Contains("{" + i + "}"));
    }
}
