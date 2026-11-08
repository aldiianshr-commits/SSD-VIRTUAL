using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SsdVirtual.Core;

namespace SsdVirtual.Security;

/// <summary>
/// Reads/writes small files encrypted with Windows DPAPI. The service runs as LocalSystem, so the
/// machine scope is used and, in addition, the file ACL is restricted to SYSTEM and Administrators.
/// Writes are atomic (temp file + replace) so a power loss never leaves a half-written file.
/// </summary>
public static class ProtectedFile
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SsdVirtual/2026/protected-store/v1");

    public static DataProtectionScope Scope { get; set; } = DataProtectionScope.LocalMachine;

    /// <summary>Set to false in tests or when running unelevated.</summary>
    public static bool RestrictAcl { get; set; } = true;

    public static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, Scope);
    public static byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, Entropy, Scope);

    public static string? ReadText(string path)
    {
        if (!File.Exists(path))
        {
            var bak = path + ".bak";
            if (!File.Exists(bak)) return null;
            path = bak;
        }
        try
        {
            return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
        }
        catch (CryptographicException ex)
        {
            Log.Error($"Could not decrypt {path}; trying backup.", ex);
            var bak = path + ".bak";
            if (!path.EndsWith(".bak") && File.Exists(bak))
                return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(bak)));
            throw;
        }
    }

    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var data = Protect(Encoding.UTF8.GetBytes(text));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(data, 0, data.Length);
            fs.Flush(true);
        }
        if (RestrictAcl) TryRestrict(tmp);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void TryRestrict(string file)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restrict ACL on {file}", ex);
        }
    }
}
