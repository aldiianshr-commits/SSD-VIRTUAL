using Fsp;
using Microsoft.Win32;
using SsdVirtual.Core;
using SsdVirtual.Filesystem;
using SsdVirtual.Sync;

namespace SsdVirtual.VirtualDrive;

/// <summary>Runtime of one account: volume + sync engine + WinFsp file system + drive letter.</summary>
public sealed class MountedDrive : IDisposable
{
    public AccountConfig Account { get; }
    public Volume Volume { get; }
    public SyncEngine Sync { get; }
    public CloudFileSystem FileSystem { get; }
    public MountState State { get; private set; } = MountState.Unmounted;
    public string MountPoint { get; private set; } = "";
    public string MountError { get; private set; } = "";
    /// <summary>Set when the user unmounted the drive on purpose (the keep-mounted watchdog leaves it alone).</summary>
    public bool UserUnmounted { get; set; }
    private FileSystemHost? _host;
    private readonly object _gate = new();

    public MountedDrive(AccountConfig account, Volume volume)
    {
        Account = account;
        Volume = volume;
        Sync = new SyncEngine(volume);
        FileSystem = new CloudFileSystem(volume);
    }

    public void StartSync() => Sync.Start();

    public bool Mount()
    {
        lock (_gate)
        {
            UserUnmounted = false;
            if (State == MountState.Mounted) return true;
            State = MountState.Mounting;
            var letter = char.ToUpperInvariant(Account.DriveLetter);
            if (letter < 'A' || letter > 'Z') { Fail("No drive letter assigned."); return false; }

            var threads = (uint)Math.Clamp(Environment.ProcessorCount * 2, 8, 32);
            foreach (var mountPoint in new[] { $"\\\\.\\{letter}:", $"{letter}:" })
            {
                var host = new FileSystemHost(FileSystem);
                int status;
                try
                {
                    status = host.MountEx(mountPoint, threads, null, false, 0);
                }
                catch (Exception ex)
                {
                    host.Dispose();
                    Fail("WinFsp is not available: " + ex.Message);
                    return false;
                }
                if (status >= 0)
                {
                    _host = host;
                    MountPoint = $"{letter}:";
                    State = MountState.Mounted;
                    MountError = "";
                    SetExplorerLabel(letter, Account.VolumeLabel);
                    Log.Info($"[{Account.DisplayName}] mounted at {MountPoint} ({(mountPoint.StartsWith("\\\\") ? "mount manager" : "DOS device")}).");
                    return true;
                }
                host.Dispose();
                Log.Warn($"[{Account.DisplayName}] mount at {mountPoint} failed with NTSTATUS 0x{status:X8}.");
                MountError = $"Mount failed (NTSTATUS 0x{status:X8}). Is drive {letter}: already in use?";
            }
            State = MountState.Failed;
            return false;
        }
    }

    private void Fail(string message)
    {
        State = MountState.Failed;
        MountError = message;
        Log.Error($"[{Account.DisplayName}] {message}");
    }

    public void Unmount()
    {
        lock (_gate)
        {
            if (_host == null) { State = MountState.Unmounted; return; }
            try { Volume.Flush(null); } catch (Exception ex) { Log.Warn("Flush before unmount failed", ex); }
            try { _host.Unmount(); } catch (Exception ex) { Log.Warn("Unmount failed", ex); }
            try { _host.Dispose(); } catch { }
            _host = null;
            RemoveExplorerLabel(char.ToUpperInvariant(Account.DriveLetter));
            State = MountState.Unmounted;
            MountPoint = "";
            Log.Info($"[{Account.DisplayName}] unmounted.");
        }
    }

    /// <summary>True when the drive is mounted and still reachable through its drive letter.</summary>
    public bool IsHealthy()
    {
        lock (_gate)
        {
            if (_host == null || State != MountState.Mounted) return false;
            try { return Directory.Exists(MountPoint + "\\"); } catch { return false; }
        }
    }

    public void UpdateLabel() => SetExplorerLabel(char.ToUpperInvariant(Account.DriveLetter), Account.VolumeLabel);

    private const string DriveIconsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

    /// <summary>Gives the drive the SsdVirtual icon and label in Explorer ("This PC").</summary>
    private static void SetExplorerLabel(char letter, string label)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"{DriveIconsKey}\{letter}");
            using (var l = key.CreateSubKey("DefaultLabel")) l.SetValue("", label);
            var exe = Path.Combine(AppContext.BaseDirectory, "SsdVirtual.exe");
            if (File.Exists(exe)) using (var i = key.CreateSubKey("DefaultIcon")) i.SetValue("", $"\"{exe}\",0");
        }
        catch (Exception ex)
        {
            Log.Debug("Explorer label not set (requires the service/administrator): " + ex.Message);
        }
    }

    private static void RemoveExplorerLabel(char letter)
    {
        try { Registry.LocalMachine.DeleteSubKeyTree($@"{DriveIconsKey}\{letter}", false); }
        catch (Exception ex) { Log.Debug("Explorer label not removed: " + ex.Message); }
    }

    public void Dispose()
    {
        Unmount();
        Sync.Dispose();
        Volume.Dispose();
    }
}
