using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace SsdVirtual.Cloud;

/// <summary>Token-bucket bandwidth limiter shared by all transfers in one direction. 0 = unlimited.</summary>
public sealed class BandwidthLimiter
{
    private readonly object _gate = new();
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();
    public long BytesPerSecond { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            var limit = BytesPerSecond;
            if (limit <= 0) return;
            TimeSpan delay;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(limit, _tokens + (now - _lastTicks) * (double)limit / Stopwatch.Frequency);
                _lastTicks = now;
                if (_tokens >= bytes || _tokens >= limit) { _tokens -= bytes; return; }
                delay = TimeSpan.FromSeconds(Math.Max(0.01, (bytes - _tokens) / limit));
            }
            await Task.Delay(delay > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Process-wide transfer counters (real measurements shown on the Performance page).</summary>
public static class TransferStats
{
    public static readonly BandwidthLimiter Download = new();
    public static readonly BandwidthLimiter Upload = new();
    private static long _down, _up, _fetches, _fetchTicks;
    public static long BytesDownloaded => Interlocked.Read(ref _down);
    public static long BytesUploaded => Interlocked.Read(ref _up);
    public static void AddDownloaded(long n) => Interlocked.Add(ref _down, n);
    public static void AddUploaded(long n) => Interlocked.Add(ref _up, n);

    public static void RecordFetch(TimeSpan latency)
    {
        Interlocked.Increment(ref _fetches);
        Interlocked.Add(ref _fetchTicks, latency.Ticks);
    }

    public static double AverageFetchMs
    {
        get
        {
            var n = Interlocked.Read(ref _fetches);
            return n == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref _fetchTicks) / n).TotalMilliseconds;
        }
    }
}

public static class Http
{
    /// <summary>Creates an HttpClient suited to large transfers (no automatic redirects: providers handle them).</summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UseCookies = false,
        };
        var c = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SsdVirtual", "1.0"));
        return c;
    }

    /// <summary>
    /// Sends a request with retries for transient failures (network errors, 429, 5xx). The request factory is
    /// invoked for every attempt because HttpRequestMessage cannot be re-sent.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client, Func<Task<HttpRequestMessage>> factory,
        CancellationToken ct, int maxAttempts = 5, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = await factory().ConfigureAwait(false);
                response = await client.SendAsync(request, completion, ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if ((code == 429 || code >= 500) && attempt < maxAttempts)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    response.Dispose();
                    await Task.Delay(retryAfter ?? Jitter(delay), ct).ConfigureAwait(false);
                    delay *= 2;
                    continue;
                }
                return response;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                response?.Dispose();
                Core.Log.Debug($"HTTP attempt {attempt} failed: {ex.Message}");
                await Task.Delay(Jitter(delay), ct).ConfigureAwait(false);
                delay *= 2;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                response?.Dispose();
                Core.Log.Debug($"HTTP attempt {attempt} timed out: {ex.Message}");
                await Task.Delay(Jitter(delay), ct).ConfigureAwait(false);
                delay *= 2;
            }
            catch (HttpRequestException ex)
            {
                throw new CloudException(CloudErrorKind.Network, "Network error: " + ex.Message, 0, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new CloudException(CloudErrorKind.Network, "Request timed out.", 0, ex);
            }
        }
    }

    private static TimeSpan Jitter(TimeSpan d) => d + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

    /// <summary>Copies a response body into <paramref name="buffer"/>, honoring the download limiter.</summary>
    public static async Task<int> ReadIntoAsync(Stream source, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var slice = buffer.Slice(total, Math.Min(256 * 1024, buffer.Length - total));
            await TransferStats.Download.WaitAsync(slice.Length, ct).ConfigureAwait(false);
            int n = await source.ReadAsync(slice, ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            TransferStats.AddDownloaded(n);
        }
        return total;
    }

    public static CloudErrorKind KindFromStatus(HttpStatusCode code) => (int)code switch
    {
        401 => CloudErrorKind.Auth,
        403 => CloudErrorKind.Auth,
        404 or 410 => CloudErrorKind.NotFound,
        409 => CloudErrorKind.AlreadyExists,
        429 => CloudErrorKind.RateLimited,
        >= 500 => CloudErrorKind.Server,
        _ => CloudErrorKind.InvalidRequest,
    };
}

/// <summary>Stream wrapper that throttles and counts upload bytes and reports progress.</summary>
public sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly Action<long>? _progress;
    private readonly long _baseOffset;
    private long _read;

    public ThrottledReadStream(Stream inner, long length, long baseOffset, Action<long>? progress)
    {
        _inner = inner;
        _length = length;
        _baseOffset = baseOffset;
        _progress = progress;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var remaining = _length - _read;
        if (remaining <= 0) return 0;
        if (buffer.Length > remaining) buffer = buffer[..(int)remaining];
        if (buffer.Length > 256 * 1024) buffer = buffer[..(256 * 1024)];
        await TransferStats.Upload.WaitAsync(buffer.Length, ct).ConfigureAwait(false);
        int n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        _read += n;
        TransferStats.AddUploaded(n);
        _progress?.Invoke(_baseOffset + _read);
        return n;
    }
}
