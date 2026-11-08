using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop;

/// <summary>Access to SsdVirtualService plus the latest status snapshot.</summary>
public static class Svc
{
    public static ServiceClient Client { get; } = new();
    public static ServiceStatusDto? LastStatus { get; private set; }
    public static bool Reachable { get; private set; }
    public static event Action? StatusUpdated;

    public static async Task<ServiceStatusDto?> RefreshStatusAsync()
    {
        try
        {
            LastStatus = await Client.CallAsync<ServiceStatusDto>(IpcMethods.GetStatus, null, 5000);
            Reachable = true;
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceCallException or IOException or TimeoutException or OperationCanceledException)
        {
            Reachable = false;
        }
        StatusUpdated?.Invoke();
        return LastStatus;
    }

    public static Task<T> Call<T>(string method, object? p = null, int timeoutMs = 30000) => Client.CallAsync<T>(method, p, timeoutMs);
    public static Task Call(string method, object? p = null, int timeoutMs = 30000) => Client.CallAsync(method, p, timeoutMs);
}

/// <summary>Per-user UI preferences (used before the service is reachable).</summary>
public sealed class UiPrefs
{
    public string Language { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public bool StartWithWindows { get; set; } = true;

    private static string FilePath => Path.Combine(AppPaths.UserRoot, "ui.json");

    public static UiPrefs Load()
    {
        try { return JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(FilePath)) ?? new UiPrefs(); }
        catch { return new UiPrefs(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) { Log.Warn("UI preferences not saved", ex); }
    }
}
