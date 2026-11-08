using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SsdVirtual.Core;
using SsdVirtual.Service;

// SsdVirtualService: runs as a Windows service (LocalSystem) and owns every virtual drive.
// Run with --console for interactive debugging (must be elevated).
var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
Log.Initialize(AppPaths.LogDirectory, "service");
Log.AlsoConsole = console;
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Error("Unhandled exception", e.ExceptionObject as Exception);
    Log.Flush();
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Warn("Unobserved task exception", e.Exception);
    e.SetObserved();
};

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);
builder.Services.AddHostedService<ServiceWorker>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
var host = builder.Build();
await host.RunAsync();
Log.Flush();
