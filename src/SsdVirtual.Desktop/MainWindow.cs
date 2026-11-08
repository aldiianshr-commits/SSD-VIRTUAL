using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SsdVirtual.Desktop.Pages;

namespace SsdVirtual.Desktop;

public sealed class MainWindow : Window
{
    private readonly ContentControl _content = new();
    private readonly StackPanel _nav = new();
    private readonly TextBlock _serviceState = Ui.Dim("");
    private readonly System.Windows.Shapes.Ellipse _serviceDot = Ui.Dot("Warn");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<(string Key, Func<PageBase> Factory)> _pages;
    private readonly Dictionary<string, PageBase> _pageCache = new();
    private PageBase? _current;
    private string _currentKey = "Nav.Dashboard";
    private bool _refreshing;

    public MainWindow()
    {
        Title = "SsdVirtual";
        Width = 1240;
        Height = 800;
        MinWidth = 980;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SsdVirtual.ico")); } catch { }
        this.Res(BackgroundProperty, "Bg");
        WindowChrome.ApplyDarkTitleBar(this, App.Prefs.Theme == "Dark");

        _pages = new()
        {
            ("Nav.Dashboard", () => new DashboardPage()),
            ("Nav.Disks", () => new DisksPage()),
            ("Nav.Accounts", () => new AccountsPage()),
            ("Nav.Files", () => new FileManagerPage()),
            ("Nav.Games", () => new GamesPage()),
            ("Nav.Cache", () => new CachePage()),
            ("Nav.Sync", () => new SyncPage()),
            ("Nav.Transfers", () => new TransfersPage()),
            ("Nav.Performance", () => new PerformancePage()),
            ("Nav.Settings", () => new SettingsPage()),
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebar.SetResourceReference(Border.BackgroundProperty, "Sidebar");
        sidebar.SetResourceReference(Border.BorderBrushProperty, "Line");
        var side = new DockPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 20, 18, 22) };
        var logo = new Image { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
        try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
        brand.Children.Add(logo);
        brand.Children.Add(Ui.Stack(Ui.Text("SsdVirtual", 16, "Text", FontWeights.SemiBold), Ui.Dim("Cloud SSD", 11)));
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);

        var status = Ui.Row(_serviceDot, _serviceState);
        status.Margin = new Thickness(20, 12, 12, 18);
        DockPanel.SetDock(status, Dock.Bottom);
        side.Children.Add(status);
        side.Children.Add(_nav);
        sidebar.Child = side;
        root.Children.Add(sidebar);

        var contentHost = new Border { Padding = new Thickness(30, 26, 22, 18), Child = _content };
        Grid.SetColumn(contentHost, 1);
        root.Children.Add(contentHost);
        Content = root;

        BuildNav();
        Loc.I.LanguageChanged += () =>
        {
            BuildNav();
            foreach (var p in _pageCache.Values) p.Rebuild();
        };
        Svc.StatusUpdated += UpdateServiceIndicator;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { Navigate(_currentKey); await RefreshAsync(); _timer.Start(); };
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var (key, _) in _pages)
        {
            var rb = new RadioButton
            {
                Content = Loc.T(key),
                GroupName = "nav",
                Style = (Style)FindResource("NavButton"),
                IsChecked = key == _currentKey,
            };
            var k = key;
            rb.Checked += (_, _) => Navigate(k);
            _nav.Children.Add(rb);
        }
    }

    public void Navigate(string key)
    {
        _currentKey = key;
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = _pages.First(p => p.Key == key).Factory();
            page.Rebuild();
            _pageCache[key] = page;
        }
        _current = page;
        _content.Content = page;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        foreach (RadioButton rb in _nav.Children) rb.IsChecked = Equals(rb.Content, Loc.T(key));
        _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_refreshing || !IsVisible) return;
        _refreshing = true;
        try
        {
            await Svc.RefreshStatusAsync();
            if (_current != null) await _current.RefreshAsync();
        }
        catch (Exception ex) { SsdVirtual.Core.Log.Debug("UI refresh failed: " + ex.Message); }
        finally { _refreshing = false; }
    }

    private void UpdateServiceIndicator()
    {
        _serviceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Svc.Reachable ? "Good" : "Bad");
        _serviceState.Text = Svc.Reachable ? Loc.T("Service.Running") : Loc.T("Service.NotRunning");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps SsdVirtual in the tray; drives keep working through the service anyway.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
