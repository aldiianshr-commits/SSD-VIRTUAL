using System.Globalization;

namespace SsdVirtual.Core;

public static class Bytes
{
    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[i];
    }

    public const long KiB = 1024, MiB = 1024 * KiB, GiB = 1024 * MiB;
}

public static class FileTime
{
    public static ulong FromUtc(DateTime utc) => utc <= DateTime.FromFileTimeUtc(0) ? 0 : (ulong)utc.ToFileTimeUtc();
    public static DateTime ToUtc(ulong ft) => ft == 0 ? DateTime.FromFileTimeUtc(0) : DateTime.FromFileTimeUtc((long)ft);
    public static ulong Now() => (ulong)DateTime.UtcNow.ToFileTimeUtc();
    public static ulong FromUnixSeconds(long s) => FromUtc(DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime);
}

public static class VirtualPath
{
    /// <summary>Splits "\a\b\c" into ["a","b","c"]. Root ("\" or "") yields an empty array.</summary>
    public static string[] Split(string path)
        => path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    public static string Combine(string parent, string name)
        => parent == "\\" || parent.Length == 0 ? "\\" + name : parent.TrimEnd('\\') + "\\" + name;

    public static string GetName(string path)
    {
        var parts = Split(path);
        return parts.Length == 0 ? "" : parts[^1];
    }

    public static string GetParent(string path)
    {
        var parts = Split(path);
        if (parts.Length <= 1) return "\\";
        return "\\" + string.Join('\\', parts, 0, parts.Length - 1);
    }

    /// <summary>Converts a Windows virtual path to the cloud's forward-slash path below <paramref name="remoteRoot"/>.</summary>
    public static string ToRemote(string remoteRoot, string path)
    {
        var root = "/" + remoteRoot.Trim('/', '\\').Replace('\\', '/');
        if (root == "/") root = "";
        var rel = string.Join('/', Split(path));
        var full = root + "/" + rel;
        return full.Length > 1 ? full.TrimEnd('/') : "/";
    }

    private static readonly char[] Invalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Makes a cloud name valid on Windows (clouds allow characters Windows forbids).</summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] < 32 || Array.IndexOf(Invalid, chars[i]) >= 0) chars[i] = '_';
        var s = new string(chars).TrimEnd(' ', '.');
        if (s.Length == 0) s = "_";
        var upper = Path.GetFileNameWithoutExtension(s).ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || (upper.Length == 4 && (upper.StartsWith("COM") || upper.StartsWith("LPT")) && char.IsDigit(upper[3])))
            s = "_" + s;
        return s.Length > 255 ? s[..255] : s;
    }

    /// <summary>"name.ext" -> "name (n).ext"</summary>
    public static string WithSuffix(string name, string suffix)
    {
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
        if (ext.Length == name.Length) ext = "";
        return $"{stem} ({suffix}){ext}";
    }
}
