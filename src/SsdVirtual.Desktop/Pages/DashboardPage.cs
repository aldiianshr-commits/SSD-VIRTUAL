using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;

namespace SsdVirtual.Desktop.Pages;

public sealed class DashboardPage : PageBase
{
    private WrapPanel _stats = new();
    private StackPanel _drives = new();
    private StackPanel _alerts = new();

    public override void Rebuild()
    {
        _stats = new WrapPanel();
        _drives = new StackPanel();
        _alerts = new StackPanel();
        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Dashboard")),
            Ui.Dim(Loc.T("Dashboard.Subtitle")),
            new Border { Height = 18 },
            _alerts,
            _stats,
            Ui.Heading(Loc.T("Dashboard.Drives")),
            new Border { Height = 8 },
            _drives));
        Render();
    }

    public override Task RefreshAsync()
    {
        Render();
        return Task.CompletedTask;
    }

    private void Render()
    {
        _alerts.Children.Clear();
        _stats.Children.Clear();
        _drives.Children.Clear();
        var st = Svc.LastStatus;
        if (!Svc.Reachable || st == null)
        {
            _alerts.Children.Add(Ui.Card(Ui.Stack(
                Ui.Heading(Loc.T("Service.NotRunning")),
                Ui.Dim(Loc.T("Dashboard.ServiceHelp")),
                Ui.Row(Ui.Btn(Loc.T("Dashboard.StartService"), StartService, true)))));
            return;
        }
        if (!st.WinFspInstalled)
            _alerts.Children.Add(Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Dashboard.WinFspMissing")), Ui.Dim(Loc.T("Dashboard.WinFspHelp")))));

        var accounts = st.Accounts;
        var connected = accounts.Count(a => a.Connection == ConnectionState.Online);
        var mounted = accounts.Count(a => a.Mount == MountState.Mounted);
        var total = accounts.Sum(a => a.TotalBytes);
        var used = accounts.Sum(a => a.UsedBytes);
        var cache = accounts.Sum(a => a.CacheUsedBytes);
        var transfers = accounts.Sum(a => a.ActiveTransfers);
        var pending = accounts.Sum(a => a.PendingUploads + a.PendingOperations);
        var worst = accounts.Count == 0 ? SyncStatus.Synced : accounts.Select(a => a.Sync).OrderByDescending(Severity).First();

        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.TotalDisks"), accounts.Count.ToString()));
        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.ConnectedAccounts"), $"{connected} / {accounts.Count}"));
        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.CloudCapacity"), Bytes.Format(total), Loc.F("Dashboard.UsedOf", Bytes.Format(used))));
        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.CacheUsage"), Bytes.Format(cache)));
        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.ActiveTransfers"), transfers.ToString()));
        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.MountedDrives"), mounted.ToString()));
        _stats.Children.Add(Ui.Stat(Loc.T("Dashboard.SyncStatus"), Ui.SyncText(worst), pending > 0 ? Loc.F("Dashboard.PendingChanges", pending) : null, Ui.StatusBrush(worst)));

        if (accounts.Count == 0)
        {
            _drives.Children.Add(Ui.Card(Ui.Stack(
                Ui.Heading(Loc.T("Dashboard.NoDrives")),
                Ui.Dim(Loc.T("Dashboard.NoDrivesHelp")),
                Ui.Row(Ui.Btn(Loc.T("Accounts.Add"), () => { new AddAccountWindow { Owner = Window.GetWindow(this) }.ShowDialog(); }, true)))));
            return;
        }
        foreach (var a in accounts.OrderBy(a => a.Account.DriveLetter))
        {
            var frac = a.TotalBytes > 0 ? (double)a.UsedBytes / a.TotalBytes : 0;
            var left = Ui.Stack(
                Ui.Row(Ui.Dot(Ui.ConnectionBrush(a.Connection)), Ui.Text($"{a.Account.DisplayName}  ({a.Account.DriveLetter}:)", 14, "Text", FontWeights.SemiBold)),
                Ui.Dim($"{Ui.ProviderText(a.Account.Provider)} · {(a.Account.Email.Length > 0 ? a.Account.Email : a.Account.AccountName)}"),
                Ui.UsageBar(frac),
                Ui.Dim(a.TotalBytes > 0
                    ? Loc.F("Disks.UsedFree", Bytes.Format(a.UsedBytes), Bytes.Format(Math.Max(0, a.TotalBytes - a.UsedBytes)), Bytes.Format(a.TotalBytes))
                    : Loc.T("Disks.CapacityUnknown")));
            var right = Ui.Stack(
                Ui.Text(Ui.SyncText(a.Sync), 13, Ui.StatusBrush(a.Sync), FontWeights.SemiBold),
                Ui.Dim(Ui.MountText(a.Mount) + " · " + Ui.ConnectionText(a.Connection)));
            right.HorizontalAlignment = HorizontalAlignment.Right;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.Children.Add(left);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            _drives.Children.Add(Ui.Card(grid));
        }
    }

    private static int Severity(SyncStatus s) => s switch
    {
        SyncStatus.Error => 9, SyncStatus.RetryRequired => 8, SyncStatus.Conflict => 7, SyncStatus.Offline => 6,
        SyncStatus.Uploading => 5, SyncStatus.Downloading => 4, SyncStatus.Processing => 3, SyncStatus.PendingUpload => 2, _ => 0,
    };

    private static async Task StartService()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("sc.exe", "start " + AppPaths.ServiceName) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p != null) await p.WaitForExitAsync();
        }
        catch (Exception ex) { Ui.ShowError(ex); }
        await Task.Delay(1500);
        await Svc.RefreshStatusAsync();
    }
}
