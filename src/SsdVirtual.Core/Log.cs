using System.Collections.Concurrent;
using System.Text;

namespace SsdVirtual.Core;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Small, dependency-free rolling file logger. Writes are queued and flushed by a background thread so
/// logging never blocks filesystem request threads.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 50_000);
    private static readonly ConcurrentQueue<string> Recent = new();
    private static string? _directory;
    private static string _prefix = "ssdvirtual";
    private static Thread? _writer;
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static bool AlsoConsole { get; set; }

    public static void Initialize(string directory, string prefix)
    {
        _directory = directory;
        _prefix = prefix;
        try { Directory.CreateDirectory(directory); } catch { }
        if (_writer != null) return;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SsdVirtual.Log" };
        _writer.Start();
    }

    public static void SetLevel(string level)
    {
        if (Enum.TryParse<LogLevel>(level, true, out var l)) MinimumLevel = l;
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static IReadOnlyList<string> RecentLines() => Recent.ToArray();

    public static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < MinimumLevel) return;
        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
          .Append(level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" })
          .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
        if (ex != null) sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        var line = sb.ToString();
        Recent.Enqueue(line);
        while (Recent.Count > 500 && Recent.TryDequeue(out _)) { }
        if (AlsoConsole) Console.WriteLine(line);
        if (ex != null && level >= LogLevel.Error) line += Environment.NewLine + ex;
        Queue.TryAdd(line);
    }

    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
    }

    private static void WriterLoop()
    {
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            if (_directory == null) continue;
            try
            {
                var file = Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");
                using var w = new StreamWriter(file, append: true, Encoding.UTF8);
                w.WriteLine(first);
                while (Queue.TryTake(out var more)) w.WriteLine(more);
                RotateIfNeeded();
            }
            catch
            {
                // Logging must never take the process down.
            }
        }
    }

    private static DateTime _lastRotate = DateTime.MinValue;

    private static void RotateIfNeeded()
    {
        if (_directory == null || DateTime.UtcNow - _lastRotate < TimeSpan.FromHours(1)) return;
        _lastRotate = DateTime.UtcNow;
        foreach (var f in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
        {
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f); } catch { }
        }
    }
}
