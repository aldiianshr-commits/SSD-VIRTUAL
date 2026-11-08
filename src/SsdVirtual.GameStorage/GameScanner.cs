using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.GameStorage;

/// <summary>Finds game libraries and installed games on a drive (Steam manifests and plain game folders).</summary>
public static class GameScanner
{
    public static IEnumerable<string> FindSteamLibraries(string driveRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(driveRoot, "SteamLibrary"),
            Path.Combine(driveRoot, "Steam"),
            Path.Combine(driveRoot, "Program Files (x86)", "Steam"),
            Path.Combine(driveRoot, "Games", "SteamLibrary"),
            driveRoot,
        };
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(driveRoot))
                if (!candidates.Contains(dir, StringComparer.OrdinalIgnoreCase)) candidates.Add(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var lib in candidates)
        {
            bool ok;
            try { ok = Directory.Exists(Path.Combine(lib, "steamapps")); } catch { ok = false; }
            if (ok) yield return lib;
        }
    }

    public static List<GameEntryDto> ScanSteam(string driveRoot)
    {
        var result = new List<GameEntryDto>();
        foreach (var lib in FindSteamLibraries(driveRoot))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var m in manifests)
            {
                try
                {
                    var entry = ParseManifest(File.ReadAllText(m), lib);
                    if (entry != null) result.Add(entry);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Could not read Steam manifest {m}: {ex.Message}");
                }
            }
        }
        return result;
    }

    public static GameEntryDto? ParseManifest(string text, string libraryPath)
    {
        var root = VdfNode.Parse(text);
        var app = root.Child("AppState");
        if (app == null) return null;
        var installDir = app.Get("installdir") ?? "";
        long.TryParse(app.Get("SizeOnDisk"), out var size);
        return new GameEntryDto
        {
            Launcher = "Steam",
            AppId = app.Get("appid") ?? "",
            Name = app.Get("name") ?? installDir,
            LibraryPath = libraryPath,
            InstallPath = Path.Combine(libraryPath, "steamapps", "common", installDir),
            SizeOnDisk = size,
        };
    }

    /// <summary>Folders directly under X:\Games (or X:\Epic Games, X:\XboxGames) are listed as games too.</summary>
    public static List<GameEntryDto> ScanGameFolders(string driveRoot)
    {
        var result = new List<GameEntryDto>();
        foreach (var (folder, launcher) in new[] { ("Games", "Folder"), ("Epic Games", "Epic"), ("XboxGames", "Xbox"), ("EA Games", "EA"), ("Ubisoft", "Ubisoft") })
        {
            var dir = Path.Combine(driveRoot, folder);
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var g in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(g);
                    if (name.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new GameEntryDto { Launcher = launcher, Name = name, InstallPath = g, LibraryPath = dir });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Creates X:\SteamLibrary. Steam then adopts it from Settings → Storage → Add drive.</summary>
    public static CreateSteamLibraryResultDto CreateSteamLibrary(string driveRoot)
    {
        var path = Path.Combine(driveRoot, "SteamLibrary");
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        return new CreateSteamLibraryResultDto { LibraryPath = path, AlreadyExisted = existed };
    }
}
