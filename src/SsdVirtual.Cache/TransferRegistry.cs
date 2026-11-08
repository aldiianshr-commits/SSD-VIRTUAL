using System.Collections.Concurrent;
using System.Diagnostics;

namespace SsdVirtual.Cache;

public sealed class TransferEntry
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string AccountId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public long BytesTotal { get; set; }
    private long _done;
    public long BytesDone { get => Interlocked.Read(ref _done); set => Interlocked.Exchange(ref _done, value); }
    public string State { get; set; } = "Queued";
    public string Error { get; set; } = "";
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public double BytesPerSecond => _clock.Elapsed.TotalSeconds < 0.5 ? 0 : BytesDone / _clock.Elapsed.TotalSeconds;
}

/// <summary>Process-wide list of uploads/downloads/prefetches shown in the Transfer Manager.</summary>
public static class TransferRegistry
{
    private static readonly ConcurrentDictionary<string, TransferEntry> Entries = new();

    public static TransferEntry Begin(string accountId, string kind, string path, long total)
    {
        var e = new TransferEntry { AccountId = accountId, Kind = kind, Path = path, BytesTotal = total, State = "Running" };
        Entries[e.Id] = e;
        Trim();
        return e;
    }

    public static void Finish(TransferEntry e, Exception? error = null)
    {
        e.FinishedUtc = DateTime.UtcNow;
        if (error == null) { e.State = "Completed"; e.BytesDone = Math.Max(e.BytesDone, e.BytesTotal); }
        else if (error is OperationCanceledException) e.State = "Cancelled";
        else { e.State = "Failed"; e.Error = error.Message; }
    }

    public static IReadOnlyList<TransferEntry> List() => Entries.Values.OrderByDescending(e => e.StartedUtc).ToList();

    public static int ActiveCount(string? accountId = null)
        => Entries.Values.Count(e => e.State == "Running" && (accountId == null || e.AccountId == accountId));

    public static bool Cancel(string id)
    {
        if (!Entries.TryGetValue(id, out var e)) return false;
        try { e.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private static void Trim()
    {
        var finished = Entries.Values.Where(e => e.FinishedUtc != null).OrderByDescending(e => e.FinishedUtc).Skip(200).ToList();
        foreach (var e in finished) Entries.TryRemove(e.Id, out _);
        foreach (var e in Entries.Values.Where(e => e.FinishedUtc != null && e.FinishedUtc < DateTime.UtcNow.AddHours(-6)).ToList())
            Entries.TryRemove(e.Id, out _);
    }
}
