namespace SsdVirtual.Core;

public static class AppPaths
{
    public const string ServiceName = "SsdVirtualService";
    public const string PipeName = "SsdVirtual.Service.v1";

    /// <summary>%ProgramData%\SsdVirtual — configuration and logs owned by the service.</summary>
    public static string DataRoot
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("SSDVIRTUAL_DATA");
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SsdVirtual");
        }
    }

    public static string ConfigFile => Path.Combine(DataRoot, "config.dat");
    public static string SecretsFile => Path.Combine(DataRoot, "secrets.dat");
    public static string LogDirectory => Path.Combine(DataRoot, "logs");

    public static string DefaultCacheRoot
    {
        get
        {
            var sys = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(sys)) sys = "C:\\";
            return Path.Combine(sys, "SsdVirtualCache");
        }
    }

    /// <summary>Per-user folder used by the desktop UI (custom languages, UI preferences).</summary>
    public static string UserRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SsdVirtual");

    public static string AccountCacheDir(string cacheRoot, string accountId) => Path.Combine(cacheRoot, accountId);
}
