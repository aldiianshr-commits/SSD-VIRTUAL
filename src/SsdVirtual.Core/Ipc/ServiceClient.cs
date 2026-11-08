using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SsdVirtual.Core.Ipc;

public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ServiceCallException : Exception
{
    public ServiceCallException(string message) : base(message) { }
}

/// <summary>
/// Client for the SsdVirtualService named pipe. Each call uses a short-lived connection, so the UI keeps
/// working across service restarts without reconnect logic.
/// </summary>
public sealed class ServiceClient
{
    private long _nextId;
    public string PipeName { get; }

    public ServiceClient(string? pipeName = null) => PipeName = pipeName ?? AppPaths.PipeName;

    public async Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 15000, CancellationToken ct = default)
    {
        var element = await CallRawAsync(method, parameters, timeoutMs, ct).ConfigureAwait(false);
        if (element is null || element.Value.ValueKind == JsonValueKind.Null || element.Value.ValueKind == JsonValueKind.Undefined)
            return default!;
        return element.Value.Deserialize<T>(IpcJson.Options)!;
    }

    public Task CallAsync(string method, object? parameters = null, int timeoutMs = 15000, CancellationToken ct = default)
        => CallRawAsync(method, parameters, timeoutMs, ct);

    public async Task<JsonElement?> CallRawAsync(string method, object? parameters, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var _ = pipe.ConfigureAwait(false);
        try
        {
            await pipe.ConnectAsync(Math.Min(timeoutMs, 3000), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            throw new ServiceUnavailableException("SsdVirtualService is not running or not reachable.", ex);
        }

        var request = new IpcRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = method,
            Params = parameters == null ? null : JsonSerializer.SerializeToElement(parameters, parameters.GetType(), IpcJson.Options),
        };
        var line = JsonSerializer.Serialize(request, IpcJson.Options);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(line.AsMemory(), cts.Token).ConfigureAwait(false);
        var responseLine = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
            ?? throw new ServiceUnavailableException("Service closed the connection.");
        var response = JsonSerializer.Deserialize<IpcResponse>(responseLine, IpcJson.Options)
            ?? throw new ServiceCallException("Invalid response from service.");
        if (!response.Ok) throw new ServiceCallException(response.Error ?? "Unknown service error.");
        return response.Result;
    }
}
