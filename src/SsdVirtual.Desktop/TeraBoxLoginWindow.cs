using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SsdVirtual.Core;

namespace SsdVirtual.Desktop;

/// <summary>
/// Shows the official TeraBox sign-in page in an isolated embedded browser (a fresh profile per sign-in, so
/// accounts never mix). After the user has signed in, SsdVirtual asks for explicit permission before it reads
/// the session cookie for terabox.com; nothing is captured silently and the profile is deleted afterwards.
/// </summary>
public sealed class TeraBoxLoginWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly TextBlock _hint = Ui.Dim("");
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "SsdVirtual-TeraBox-" + Guid.NewGuid().ToString("N"));
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _asked;
    public string CookieHeader { get; private set; } = "";

    private static readonly string[] Domains = { "https://www.terabox.com", "https://terabox.com", "https://www.1024terabox.com", "https://www.terabox.app", "https://www.4funbox.com" };

    public TeraBoxLoginWindow()
    {
        Title = Loc.T("TeraBox.LoginTitle");
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Res(BackgroundProperty, "Bg");
        WindowChrome.ApplyDarkTitleBar(this, App.Prefs.Theme == "Dark");
        _hint.Text = Loc.T("TeraBox.LoginHint");
        _hint.Margin = new Thickness(14, 10, 14, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(_hint, Dock.Top);
        dock.Children.Add(_hint);
        dock.Children.Add(_web);
        Content = dock;
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => Cleanup();
        _poll.Tick += async (_, _) => await CheckLoginAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _profile);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Navigate("https://www.terabox.com/login");
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Ui.ShowError(new InvalidOperationException(Loc.T("TeraBox.NoWebView2")));
            DialogResult = false;
        }
    }

    private async Task<List<CoreWebView2Cookie>> SessionCookiesAsync()
    {
        var all = new List<CoreWebView2Cookie>();
        var hosts = Domains.ToList();
        var current = _web.CoreWebView2?.Source;
        if (!string.IsNullOrEmpty(current) && Uri.TryCreate(current, UriKind.Absolute, out var u) && u.Host.Contains("terabox", StringComparison.OrdinalIgnoreCase))
            hosts.Insert(0, u.Scheme + "://" + u.Host);
        foreach (var h in hosts.Distinct())
        {
            var cookies = await _web.CoreWebView2!.CookieManager.GetCookiesAsync(h);
            foreach (var c in cookies)
                if (!all.Any(x => x.Name == c.Name && x.Domain == c.Domain)) all.Add(c);
        }
        return all;
    }

    private async Task CheckLoginAsync()
    {
        if (_asked || _web.CoreWebView2 == null) return;
        List<CoreWebView2Cookie> cookies;
        try { cookies = await SessionCookiesAsync(); }
        catch (Exception ex) { Log.Debug("Cookie check failed: " + ex.Message); return; }
        // "ndus" is the TeraBox session cookie that exists only after a successful sign-in.
        if (!cookies.Any(c => c.Name.Equals("ndus", StringComparison.OrdinalIgnoreCase))) return;
        _asked = true;
        _poll.Stop();
        var consent = MessageBox.Show(this, Loc.T("TeraBox.Consent"), "SsdVirtual", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (consent != MessageBoxResult.Yes)
        {
            DialogResult = false;
            return;
        }
        CookieHeader = string.Join("; ", cookies
            .Where(c => c.Domain.Contains("terabox", StringComparison.OrdinalIgnoreCase) || c.Domain.Contains("4funbox", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Name).Select(g => g.First())
            .Select(c => c.Name + "=" + c.Value));
        DialogResult = true;
    }

    private void Cleanup()
    {
        _poll.Stop();
        try { _web.Dispose(); } catch { }
        // The browser process may hold the folder briefly.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); return; }
                catch { await Task.Delay(1000); }
            }
        });
    }
}
