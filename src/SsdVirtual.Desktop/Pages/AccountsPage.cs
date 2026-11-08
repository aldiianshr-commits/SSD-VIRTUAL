using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

public sealed class AccountsPage : PageBase
{
    private StackPanel _list = new();
    private string _signature = "";

    public override void Rebuild()
    {
        _list = new StackPanel();
        _signature = "";
        var add = Ui.Btn(Loc.T("Accounts.Add"), () => { new AddAccountWindow { Owner = Window.GetWindow(this) }.ShowDialog(); }, true);
        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Accounts")),
            Ui.Dim(Loc.T("Accounts.Subtitle")),
            Ui.Row(add),
            new Border { Height = 16 },
            _list));
        Render(true);
    }

    public override Task RefreshAsync() { Render(false); return Task.CompletedTask; }

    private void Render(bool force)
    {
        var accounts = Svc.LastStatus?.Accounts.OrderBy(a => a.Account.Provider).ThenBy(a => a.Account.DisplayName).ToList() ?? new();
        var sig = string.Join("|", accounts.Select(a => $"{a.Account.Id}{a.Account.DisplayName}{a.Account.DriveLetter}{a.Account.Email}{a.Connection}{a.Account.Connected}{a.TotalBytes}{a.UsedBytes}"));
        if (!force && sig == _signature) return;
        _signature = sig;
        _list.Children.Clear();
        foreach (var group in accounts.GroupBy(a => a.Account.Provider))
        {
            _list.Children.Add(Ui.Heading(Ui.ProviderText(group.Key)));
            _list.Children.Add(new Border { Height = 8 });
            foreach (var a in group) _list.Children.Add(AccountCard(a));
        }
        if (accounts.Count == 0) _list.Children.Add(Ui.Card(Ui.Dim(Loc.T("Dashboard.NoDrivesHelp"))));
    }

    private UIElement AccountCard(AccountStatusDto a)
    {
        var id = a.Account.Id;
        var owner = Window.GetWindow(this);
        var info = Ui.Stack(
            Ui.Row(Ui.Dot(Ui.ConnectionBrush(a.Connection)), Ui.Text($"{a.Account.DisplayName}  →  {a.Account.DriveLetter}:", 14, "Text", FontWeights.SemiBold)),
            Ui.Dim($"{(a.Account.Email.Length > 0 ? a.Account.Email : "—")}   {a.Account.AccountName}"),
            Ui.Dim(a.TotalBytes > 0
                ? Loc.F("Disks.UsedFree", Bytes.Format(a.UsedBytes), Bytes.Format(Math.Max(0, a.TotalBytes - a.UsedBytes)), Bytes.Format(a.TotalBytes))
                : Loc.T("Disks.CapacityUnknown")),
            Ui.Dim(Ui.ConnectionText(a.Connection) + (string.IsNullOrEmpty(a.LastError) ? "" : " — " + a.LastError)));

        var actions = Ui.Wrap(
            Ui.Btn(Loc.T("Accounts.Info"), () => Ui.Info(
                $"{a.Account.DisplayName}\n{Ui.ProviderText(a.Account.Provider)}\n{a.Account.Email}\n{a.Account.AccountName}\n" +
                $"{Loc.T("Disks.DriveLetter")}: {a.Account.DriveLetter}:\nID: {a.Account.Id}\n{Loc.T("Accounts.Added")}: {a.Account.CreatedUtc.ToLocalTime():g}\n\n{a.ProviderNote}")),
            Ui.Btn(Loc.T("Accounts.Rename"), async () =>
            {
                var n = Dialogs.Prompt(Loc.T("Accounts.Rename"), Loc.T("Accounts.NewName"), a.Account.DisplayName, owner);
                if (!string.IsNullOrWhiteSpace(n)) await Svc.Call(IpcMethods.RenameAccount, new RenameAccountParams { AccountId = id, DisplayName = n });
            }),
            Ui.Btn(Loc.T("Disks.ChangeLetter"), async () =>
            {
                var l = Dialogs.PickLetter(Loc.T("Disks.ChangeLetter"), a.Account.DriveLetter, owner);
                if (l is { } letter) await Svc.Call(IpcMethods.ChangeDriveLetter, new ChangeDriveLetterParams { AccountId = id, DriveLetter = letter }, 60000);
            }),
            Ui.Btn(Loc.T("Disks.Remount"), () => Svc.Call(IpcMethods.Remount, new AccountIdParams { AccountId = id }, 60000)),
            a.Account.Connected
                ? Ui.Btn(Loc.T("Accounts.Disconnect"), async () =>
                {
                    if (Ui.Confirm(Loc.F("Accounts.DisconnectConfirm", a.Account.DisplayName)))
                        await Svc.Call(IpcMethods.Disconnect, new AccountIdParams { AccountId = id }, 60000);
                })
                : Ui.Btn(Loc.T("Accounts.Reconnect"), () => Svc.Call(IpcMethods.Reconnect, new AccountIdParams { AccountId = id }, 60000), true),
            Ui.Btn(Loc.T("Accounts.SignInAgain"), () =>
            {
                new AddAccountWindow(a.Account) { Owner = owner }.ShowDialog();
            }),
            Ui.Btn(Loc.T("Accounts.RefreshInfo"), () => Svc.Call(IpcMethods.RefreshAccountInfo, new AccountIdParams { AccountId = id }, 60000)),
            Ui.Btn(Loc.T("Accounts.Remove"), async () =>
            {
                var (ok, deleteCache, force) = Dialogs.ConfirmRemove(a.Account.DisplayName, owner);
                if (ok) await Svc.Call(IpcMethods.RemoveAccount, new RemoveAccountParams { AccountId = id, DeleteCache = deleteCache, Force = force }, 120000);
            }));
        return Ui.Card(Ui.Stack(info, actions));
    }
}
