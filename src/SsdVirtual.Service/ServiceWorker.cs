using Microsoft.Extensions.Hosting;
using SsdVirtual.Core;
using SsdVirtual.VirtualDrive;

namespace SsdVirtual.Service;

public sealed class ServiceWorker : BackgroundService
{
    private DriveManager? _manager;
    private IpcServer? _ipc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Info($"SsdVirtualService {typeof(ServiceWorker).Assembly.GetName().Version} starting (PID {Environment.ProcessId}).");
        Directory.CreateDirectory(AppPaths.DataRoot);
        try
        {
            _manager = new DriveManager(AppPaths.ConfigFile, AppPaths.SecretsFile);
            Log.Initialize(AppPaths.LogDirectory, "service");
            _manager.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            throw;
        }
        _ipc = new IpcServer(new IpcHandler(_manager));
        _ipc.Start(stoppingToken);
        Log.Info("SsdVirtualService ready.");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Info("SsdVirtualService stopping; flushing caches and unmounting drives.");
        await base.StopAsync(cancellationToken);
        _ipc?.Dispose();
        _manager?.Dispose();
        Log.Info("SsdVirtualService stopped.");
        Log.Flush();
    }
}
