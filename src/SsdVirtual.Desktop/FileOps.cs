using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SsdVirtual.Desktop;

/// <summary>Recursive copy/move with progress; works on any path including SsdVirtual drives.</summary>
public static class FileOps
{
    public sealed class Progress
    {
        public long BytesDone;
        public long BytesTotal;
        public int Files;
        public string Current = "";
    }

    public static long MeasureSize(string path, CancellationToken ct)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        long total = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            total += f.Length;
        }
        return total;
    }

    public static string UniqueTarget(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Directory.Exists(Path.Combine(folder, name)) ? "" : Path.GetExtension(name);
        if (ext.Length == 0) stem = name;
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static async Task CopyAsync(string source, string targetFolder, Progress p, CancellationToken ct)
    {
        var target = UniqueTarget(targetFolder, Path.GetFileName(source.TrimEnd('\\')));
        if (File.Exists(source)) await CopyFileAsync(source, target, p, ct);
        else await CopyDirectoryAsync(source, target, p, ct);
    }

    private static async Task CopyDirectoryAsync(string source, string target, Progress p, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var f in Directory.EnumerateFiles(source))
            await CopyFileAsync(f, Path.Combine(target, Path.GetFileName(f)), p, ct);
        foreach (var d in Directory.EnumerateDirectories(source))
            await CopyDirectoryAsync(d, Path.Combine(target, Path.GetFileName(d)), p, ct);
    }

    private static async Task CopyFileAsync(string source, string target, Progress p, CancellationToken ct)
    {
        p.Current = source;
        const int bufferSize = 4 * 1024 * 1024;
        await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous))
        {
            dst.SetLength(src.Length);
            var buffer = new byte[bufferSize];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                Interlocked.Add(ref p.BytesDone, n);
            }
        }
        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            File.SetAttributes(target, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
        }
        catch { }
        Interlocked.Increment(ref p.Files);
    }

    public static async Task MoveAsync(string source, string targetFolder, Progress p, CancellationToken ct)
    {
        var name = Path.GetFileName(source.TrimEnd('\\'));
        var target = UniqueTarget(targetFolder, name);
        var sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(targetFolder), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            if (File.Exists(source)) File.Move(source, target);
            else Directory.Move(source, target);
            return;
        }
        await CopyAsync(source, targetFolder, p, ct);
        if (File.Exists(source)) File.Delete(source);
        else Directory.Delete(source, true);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        else if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
