using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using SsdVirtual.Core;

namespace SsdVirtual.Desktop;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private System.Windows.Forms.NotifyIcon? _tray;
    public static UiPrefs Prefs { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize(System.IO.Path.Combine(AppPaths.UserRoot, "logs"), "desktop");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI error", ex.Exception);
            Ui.ShowError(ex.Exception);
            ex.Handled = true;
        };

        var user = Environment.UserName;
        _single = new Mutex(true, "SsdVirtual.Desktop." + user, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "SsdVirtual.Desktop.Show." + user);
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.Invoke(ShowMainWindow);
        }) { IsBackground = true }.Start();

        Prefs = UiPrefs.Load();
        var lang = string.IsNullOrEmpty(Prefs.Language)
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "id" ? "id" : "en")
            : Prefs.Language;
        Loc.I.SetLanguage(lang);
        ApplyTheme(Prefs.Theme);
        CreateTray();

        var main = new MainWindow();
        MainWindow = main;
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) main.Show();
    }

    public static void ApplyTheme(string theme)
    {
        var dicts = Current.Resources.MergedDictionaries;
        var uri = new Uri(theme == "Light" ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        dicts[0] = new ResourceDictionary { Source = uri };
        Prefs.Theme = theme == "Light" ? "Light" : "Dark";
        foreach (Window w in Current.Windows) WindowChrome.ApplyDarkTitleBar(w, Prefs.Theme == "Dark");
    }

    private void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "SsdVirtual",
            Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Tray.Open"), null, (_, _) => ShowMainWindow());
        menu.Items.Add(Loc.T("Tray.SyncNow"), null, async (_, _) =>
        {
            try { await Svc.Call(Core.Ipc.IpcMethods.SyncNow, new Core.Ipc.AccountIdParams()); } catch { }
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Exit"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    public static void Notify(string title, string text)
    {
        if (Current is App app && app._tray != null && Prefs != null)
        {
            try { app._tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info); } catch { }
        }
    }

    private void ShowMainWindow()
    {
        var w = MainWindow ?? (MainWindow = new MainWindow());
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public void ExitApp()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Prefs.Save();
        Log.Flush();
        Shutdown();
    }

    /// <summary>Starts the tray app with Windows for the current user.</summary>
    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("SsdVirtual", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("SsdVirtual", false);
        Prefs.StartWithWindows = enabled;
        Prefs.Save();
    }

    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("SsdVirtual") != null;
    }
}
