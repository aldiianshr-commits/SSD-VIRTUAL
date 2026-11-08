using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

public sealed class SettingsPage : PageBase
{
    private AppSettings? _s;

    public override void Rebuild()
    {
        Content = Ui.Scroll(Ui.Stack(Ui.Title(Loc.T("Nav.Settings")), Ui.Dim(Loc.T("Common.Loading"))));
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { _s = await Svc.Call<AppSettings>(IpcMethods.GetSettings, null, 5000); }
        catch (Exception) { _s = null; }
        Build();
    }

    private void Build()
    {
        var s = _s;
        var root = Ui.Stack(Ui.Title(Loc.T("Nav.Settings")), new Border { Height = 12 });

        // ---- appearance & language (per user, works without the service)
        var language = Ui.Combo(Loc.Available().Select(l => ((object)l.Code, l.Name)).ToArray());
        language.Select(Loc.I.Language);
        language.SelectionChanged += (_, _) =>
        {
            if (language.Selected() is string code && code != Loc.I.Language)
            {
                App.Prefs.Language = code;
                App.Prefs.Save();
                Dispatcher.InvokeAsync(() => Loc.I.SetLanguage(code));
            }
        };
        var theme = Ui.Combo(("Dark", Loc.T("Settings.Dark")), ("Light", Loc.T("Settings.Light")));
        theme.Select(App.Prefs.Theme);
        theme.SelectionChanged += (_, _) => { if (theme.Selected() is string t) { App.ApplyTheme(t); App.Prefs.Save(); } };
        var startWithWindows = new CheckBox { Content = Loc.T("Settings.StartWithWindows"), IsChecked = App.IsStartWithWindows() };
        startWithWindows.Click += (_, _) => App.SetStartWithWindows(startWithWindows.IsChecked == true);
        root.Children.Add(Ui.Card(Ui.Stack(
            Ui.Heading(Loc.T("Settings.Appearance")),
            Ui.Field(Loc.T("Settings.Language"), language),
            Ui.Row(Ui.Btn(Loc.T("Settings.ExportTemplate"), () =>
            {
                var path = Loc.ExportTemplate();
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }), Ui.Btn(Loc.T("Settings.CustomFolder"), () =>
            {
                Directory.CreateDirectory(Loc.CustomLanguageFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Loc.CustomLanguageFolder}\"") { UseShellExecute = true });
            })),
            Ui.Dim(Loc.T("Settings.CustomLanguageHelp"), 11.5),
            Ui.Field(Loc.T("Settings.Theme"), theme),
            startWithWindows)));

        if (s == null)
        {
            root.Children.Add(Ui.Card(Ui.Dim(Loc.T("Error.ServiceUnavailable"))));
            Content = Ui.Scroll(root);
            return;
        }

        // ---- startup & mounting
        var mountStartup = new CheckBox { Content = Loc.T("Settings.MountOnStartup"), IsChecked = s.MountOnStartup };
        var keepMounted = new CheckBox { Content = Loc.T("Settings.KeepMounted"), IsChecked = s.KeepMounted };
        var autoReconnect = new CheckBox { Content = Loc.T("Settings.AutoReconnect"), IsChecked = s.AutoReconnect };
        var restore = new CheckBox { Content = Loc.T("Settings.RestoreConfig"), IsChecked = s.RestorePreviousConfiguration };
        root.Children.Add(Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Settings.Startup")), mountStartup, keepMounted, autoReconnect, restore,
            Ui.Dim(Loc.T("Settings.StartupHelp"), 11.5))));

        // ---- transfers & sync
        var parallel = Ui.Input(s.MaxParallelTransfers.ToString(), 100);
        var down = Ui.Input((s.DownloadLimitBytesPerSec / 1024).ToString(), 140);
        var up = Ui.Input((s.UploadLimitBytesPerSec / 1024).ToString(), 140);
        var interval = Ui.Input(s.SyncIntervalSeconds.ToString(), 100);
        var quiet = Ui.Input(s.UploadQuietSeconds.ToString(), 100);
        var window = Ui.Input(s.UploadWindow, 160);
        var readAhead = Ui.Input(s.ReadAheadMaxMiB.ToString(), 100);
        root.Children.Add(Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Settings.Transfers")),
            Ui.Wrap(
                Pad(Ui.Field(Loc.T("Settings.Parallel"), parallel)),
                Pad(Ui.Field(Loc.T("Settings.DownloadLimit"), down)),
                Pad(Ui.Field(Loc.T("Settings.UploadLimit"), up)),
                Pad(Ui.Field(Loc.T("Settings.ReadAhead"), readAhead))),
            Ui.Wrap(
                Pad(Ui.Field(Loc.T("Settings.SyncInterval"), interval)),
                Pad(Ui.Field(Loc.T("Settings.UploadQuiet"), quiet)),
                Pad(Ui.Field(Loc.T("Settings.UploadWindow"), window))),
            Ui.Dim(Loc.T("Settings.TransfersHelp"), 11.5))));

        // ---- notifications, logging, diagnostics
        var notify = new CheckBox { Content = Loc.T("Settings.Notifications"), IsChecked = s.Notifications };
        var logLevel = Ui.Combo(("Debug", "Debug"), ("Info", "Info"), ("Warning", "Warning"), ("Error", "Error"));
        logLevel.Select(s.LogLevel);
        root.Children.Add(Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Settings.Diagnostics")), notify, Ui.Field(Loc.T("Settings.LogLevel"), logLevel),
            Ui.Row(
                Ui.Btn(Loc.T("Settings.OpenLogs"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true })),
                Ui.Btn(Loc.T("Settings.ExportDiagnostics"), ExportDiagnosticsAsync),
                Ui.Btn(Loc.T("Settings.ViewLog"), async () =>
                {
                    var lines = await Svc.Call<string[]>(IpcMethods.GetLogs, new LogsParams { MaxLines = 60 });
                    Ui.Info(string.Join("\n", lines ?? Array.Empty<string>()));
                })))));

        // ---- Google OAuth client
        var clientId = Ui.Input(s.GoogleClientId, 420);
        var clientSecret = Ui.Input(s.GoogleClientSecret, 420);
        root.Children.Add(Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Settings.GoogleClient")), Ui.Dim(Loc.T("Settings.GoogleClientHelp"), 11.5),
            Ui.Field("Client ID", clientId), Ui.Field("Client secret", clientSecret),
            Ui.Btn(Loc.T("Settings.ImportClientJson"), () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
                if (dlg.ShowDialog() != true) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
                var node = doc.RootElement.TryGetProperty("installed", out var i) ? i : doc.RootElement;
                clientId.Text = node.GetProperty("client_id").GetString() ?? "";
                clientSecret.Text = node.TryGetProperty("client_secret", out var cs) ? cs.GetString() ?? "" : "";
            }))));

        // ---- backup configuration
        root.Children.Add(Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Settings.Backup")), Ui.Dim(Loc.T("Settings.BackupHelp"), 11.5),
            Ui.Row(
                Ui.Btn(Loc.T("Settings.ExportConfig"), async () =>
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "SsdVirtual-config.json", Filter = "JSON|*.json" };
                    if (dlg.ShowDialog() != true) return;
                    var json = await Svc.Call<string>(IpcMethods.ExportConfig);
                    File.WriteAllText(dlg.FileName, json);
                }),
                Ui.Btn(Loc.T("Settings.ImportConfig"), async () =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
                    if (dlg.ShowDialog() != true) return;
                    await Svc.Call(IpcMethods.ImportConfig, new ImportConfigParams { Json = File.ReadAllText(dlg.FileName) });
                    await LoadAsync();
                })))));

        var save = Ui.Btn(Loc.T("Settings.Save"), async () =>
        {
            s.MountOnStartup = mountStartup.IsChecked == true;
            s.KeepMounted = keepMounted.IsChecked == true;
            s.AutoReconnect = autoReconnect.IsChecked == true;
            s.RestorePreviousConfiguration = restore.IsChecked == true;
            s.MaxParallelTransfers = ParseInt(parallel.Text, 4, 1, 16);
            s.DownloadLimitBytesPerSec = ParseInt(down.Text, 0, 0, int.MaxValue) * 1024L;
            s.UploadLimitBytesPerSec = ParseInt(up.Text, 0, 0, int.MaxValue) * 1024L;
            s.SyncIntervalSeconds = ParseInt(interval.Text, 60, 15, 86400);
            s.UploadQuietSeconds = ParseInt(quiet.Text, 5, 0, 3600);
            s.UploadWindow = window.Text.Trim();
            s.ReadAheadMaxMiB = ParseInt(readAhead.Text, 32, 1, 512);
            s.Notifications = notify.IsChecked == true;
            s.LogLevel = logLevel.Selected() as string ?? "Info";
            s.GoogleClientId = clientId.Text.Trim();
            s.GoogleClientSecret = clientSecret.Text.Trim();
            s.Language = Loc.I.Language;
            s.Theme = App.Prefs.Theme;
            _s = await Svc.Call<AppSettings>(IpcMethods.SaveSettings, s, 600_000);
            Ui.Info(Loc.T("Settings.Saved"));
        }, true);
        root.Children.Add(save);
        Content = Ui.Scroll(root);
    }

    private static FrameworkElement Pad(UIElement e) => new Border { Child = e, Margin = new Thickness(0, 0, 18, 0) };

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private static async Task ExportDiagnosticsAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"SsdVirtual-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip", Filter = "ZIP|*.zip" };
        if (dlg.ShowDialog() != true) return;
        var status = await Svc.Call<ServiceStatusDto>(IpcMethods.GetStatus);
        var perf = await Svc.Call<PerformanceDto>(IpcMethods.GetPerformance);
        var logs = await Svc.Call<string[]>(IpcMethods.GetLogs, new LogsParams { MaxLines = 2000 });
        await Task.Run(() =>
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            void AddText(string name, string text)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }
            // Status contains no credentials (they never leave the service).
            AddText("status.json", JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            AddText("performance.json", JsonSerializer.Serialize(perf, new JsonSerializerOptions { WriteIndented = true }));
            AddText("service-log-tail.txt", string.Join(Environment.NewLine, logs ?? Array.Empty<string>()));
            AddText("system.txt", $"{Environment.OSVersion}\n.NET {Environment.Version}\nx64={Environment.Is64BitOperatingSystem}\nCPUs={Environment.ProcessorCount}");
            var uiLogs = Path.Combine(AppPaths.UserRoot, "logs");
            if (Directory.Exists(uiLogs))
                foreach (var f in Directory.EnumerateFiles(uiLogs, "*.log").OrderByDescending(File.GetLastWriteTime).Take(3))
                    zip.CreateEntryFromFile(f, "ui/" + Path.GetFileName(f));
        });
        Ui.Info(Loc.T("Settings.DiagnosticsSaved"));
    }
}
