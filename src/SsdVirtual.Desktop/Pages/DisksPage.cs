using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

/// <summary>"My Virtual Disks": every drive with capacity, cache, status and management actions.</summary>
public sealed class DisksPage : PageBase
{
    private StackPanel _list = new();
    private StackPanel _chart = new();
    private string _signature = "";

    public override void Rebuild()
    {
        _list = new StackPanel();
        _chart = new StackPanel();
        _signature = "";
        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Disks")),
            Ui.Dim(Loc.T("Disks.Subtitle")),
            new Border { Height = 16 },
            Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Disks.StorageChart")), new Border { Height = 6 }, _chart)),
            _list));
        Render(force: true);
    }

    public override Task RefreshAsync()
    {
        Render(false);
        return Task.CompletedTask;
    }

    private void Render(bool force)
    {
        var st = Svc.LastStatus;
        var accounts = st?.Accounts.OrderBy(a => a.Account.DriveLetter).ToList() ?? new();
        // Rebuild only when something visible changed (keeps buttons responsive).
        var sig = string.Join("|", accounts.Select(a => $"{a.Account.Id}{a.Account.DisplayName}{a.Account.DriveLetter}{a.Mount}{a.Connection}{a.Sync}{a.UsedBytes / Bytes.MiB}{a.CacheUsedBytes / Bytes.MiB}{a.PendingUploads}{a.LastError}"));
        if (!force && sig == _signature) return;
        _signature = sig;
        _list.Children.Clear();
        _chart.Children.Clear();

        if (accounts.Count == 0)
        {
            _chart.Children.Add(Ui.Dim(Loc.T("Dashboard.NoDrives")));
            return;
        }
        foreach (var a in accounts)
        {
            var frac = a.TotalBytes > 0 ? (double)a.UsedBytes / a.TotalBytes : 0;
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            var label = Ui.Text($"{a.Account.DriveLetter}:  {a.Account.DisplayName}");
            var bar = Ui.UsageBar(frac, frac > 0.9 ? "Bad" : frac > 0.75 ? "Warn" : "Accent", 12);
            var pct = Ui.Dim(a.TotalBytes > 0 ? $"{frac * 100:0.#}% · {Bytes.Format(a.UsedBytes)} / {Bytes.Format(a.TotalBytes)}" : Loc.T("Disks.CapacityUnknown"));
            pct.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(pct, 2);
            row.Children.Add(label);
            row.Children.Add(bar);
            row.Children.Add(pct);
            _chart.Children.Add(row);

            _list.Children.Add(DriveCard(a));
        }
    }

    private UIElement DriveCard(AccountStatusDto a)
    {
        var id = a.Account.Id;
        var free = Math.Max(0, a.TotalBytes - a.UsedBytes);
        var details = new Grid();
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var c1 = Ui.Stack(
            Kv(Loc.T("Disks.DriveName"), a.Account.VolumeLabel),
            Kv(Loc.T("Disks.DriveLetter"), a.Account.DriveLetter + ":"),
            Kv(Loc.T("Disks.Provider"), Ui.ProviderText(a.Account.Provider)),
            Kv(Loc.T("Disks.Account"), a.Account.Email.Length > 0 ? a.Account.Email : a.Account.AccountName),
            Kv(Loc.T("Disks.FileSystem"), "NTFS (WinFsp)"));
        var c2 = Ui.Stack(
            Kv(Loc.T("Disks.Total"), a.TotalBytes > 0 ? Bytes.Format(a.TotalBytes) : "—"),
            Kv(Loc.T("Disks.Used"), a.TotalBytes > 0 ? Bytes.Format(a.UsedBytes) : "—"),
            Kv(Loc.T("Disks.Available"), a.TotalBytes > 0 ? Bytes.Format(free) : "—"),
            Kv(Loc.T("Disks.CacheUsage"), Bytes.Format(a.CacheUsedBytes) + " / " + Bytes.Format(a.Account.CacheQuotaBytes)),
            Kv(Loc.T("Disks.Status"), $"{Ui.MountText(a.Mount)} · {Ui.ConnectionText(a.Connection)} · {Ui.SyncText(a.Sync)}"));
        Grid.SetColumn(c2, 1);
        details.Children.Add(c1);
        details.Children.Add(c2);

        var actions = Ui.Wrap(
            Ui.Btn(Loc.T("Disks.Open"), () => Process.Start(new ProcessStartInfo("explorer.exe", a.Account.DriveLetter + ":\\") { UseShellExecute = true })),
            a.Mount == MountState.Mounted
                ? Ui.Btn(Loc.T("Disks.Unmount"), () => Svc.Call(IpcMethods.Unmount, new AccountIdParams { AccountId = id }))
                : Ui.Btn(Loc.T("Disks.Mount"), () => Svc.Call(IpcMethods.Mount, new AccountIdParams { AccountId = id }, 60000), true),
            Ui.Btn(Loc.T("Disks.Remount"), () => Svc.Call(IpcMethods.Remount, new AccountIdParams { AccountId = id }, 60000)),
            Ui.Btn(Loc.T("Disks.ChangeLetter"), async () =>
            {
                var l = Dialogs.PickLetter(Loc.T("Disks.ChangeLetter"), a.Account.DriveLetter, Window.GetWindow(this));
                if (l is { } letter) await Svc.Call(IpcMethods.ChangeDriveLetter, new ChangeDriveLetterParams { AccountId = id, DriveLetter = letter }, 60000);
            }),
            Ui.Btn(Loc.T("Accounts.Rename"), async () =>
            {
                var n = Dialogs.Prompt(Loc.T("Accounts.Rename"), Loc.T("Accounts.NewName"), a.Account.DisplayName, Window.GetWindow(this));
                if (!string.IsNullOrWhiteSpace(n)) await Svc.Call(IpcMethods.RenameAccount, new RenameAccountParams { AccountId = id, DisplayName = n });
            }),
            Ui.Btn(Loc.T("Sync.Now"), () => Svc.Call(IpcMethods.SyncNow, new AccountIdParams { AccountId = id })));

        var header = Ui.Row(Ui.Dot(Ui.ConnectionBrush(a.Connection), 10), Ui.Heading($"{a.Account.DisplayName} ({a.Account.DriveLetter}:)"));
        var stack = Ui.Stack(header, new Border { Height = 10 }, details);
        if (!string.IsNullOrEmpty(a.LastError)) stack.Children.Add(Ui.Text(a.LastError, 12, "Warn"));
        if (!string.IsNullOrEmpty(a.ProviderNote)) stack.Children.Add(Ui.Dim(a.ProviderNote, 11.5));
        stack.Children.Add(actions);
        return Ui.Card(stack);
    }

    private static UIElement Kv(string k, string v)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 12, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var key = Ui.Dim(k);
        var val = Ui.Text(v);
        Grid.SetColumn(val, 1);
        g.Children.Add(key);
        g.Children.Add(val);
        return g;
    }
}
