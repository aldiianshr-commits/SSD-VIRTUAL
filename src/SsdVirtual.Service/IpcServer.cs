using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Service;

/// <summary>
/// Named-pipe server (newline-delimited JSON). Interactive users may connect so the desktop UI works
/// without elevation; credentials are write-only through this channel and are never returned.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly IpcHandler _handler;
    private CancellationTokenSource? _cts;

    public IpcServer(IpcHandler handler) => _handler = handler;

    public void Start(CancellationToken stopping)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        for (int i = 0; i < 4; i++) _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(AppPaths.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, CreateSecurity());
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connection = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connection, ct));
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Warn("IPC accept failed", ex);
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return;
            IpcResponse response;
            IpcRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options) ?? throw new InvalidDataException("Empty request.");
                var result = await _handler.HandleAsync(request.Method, request.Params, ct).ConfigureAwait(false);
                response = new IpcResponse
                {
                    Id = request.Id,
                    Ok = true,
                    Result = result == null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), IpcJson.Options),
                };
            }
            catch (Exception ex)
            {
                if (ex is not InvalidOperationException) Log.Warn($"IPC {request?.Method} failed", ex);
                response = new IpcResponse { Id = request?.Id ?? 0, Ok = false, Error = ex.Message };
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options).AsMemory(), ct).ConfigureAwait(false);
            try { pipe.WaitForPipeDrain(); } catch { }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // client went away
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
    }
}
