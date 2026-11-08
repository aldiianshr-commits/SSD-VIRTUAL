using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

/// <summary>Game storage: Steam libraries on virtual drives, installed games, offline pinning and moving games.</summary>
public sealed class GamesPage : PageBase
{
    private StackPanel _games = new();
    private ComboBox _driveForLibrary = new();
    private TextBlock _status = new();
    private List<GameEntryDto> _last = new();
    private DateTime _lastScan = DateTime.MinValue;
    private CancellationTokenSource? _cts;

    public override void Rebuild()
    {
        _games = new StackPanel();
        _status = Ui.Dim("");
        _driveForLibrary = new ComboBox { Margin = new Thickness(0, 6, 8, 0) };
        FillDrives(_driveForLibrary);

        var libraryCard = Ui.Card(Ui.Stack(
            Ui.Heading(Loc.T("Games.SteamLibrary")),
            Ui.Dim(Loc.T("Games.SteamLibraryHelp")),
            Ui.Row(_driveForLibrary,
                Ui.Btn(Loc.T("Games.CreateLibrary"), CreateLibraryAsync, true),
                Ui.Btn(Loc.T("Games.OpenSteamStorage"), () => Process.Start(new ProcessStartInfo("steam://open/settings/storage") { UseShellExecute = true })))));

        var moveCard = Ui.Card(Ui.Stack(
            Ui.Heading(Loc.T("Games.MoveCopy")),
            Ui.Dim(Loc.T("Games.MoveCopyHelp")),
            Ui.Row(Ui.Btn(Loc.T("Games.CopyFolder"), () => TransferGameAsync(false)), Ui.Btn(Loc.T("Games.MoveFolder"), () => TransferGameAsync(true)),
                Ui.Btn(Loc.T("Common.Cancel"), () => _cts?.Cancel())),
            _status));

        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Games")),
            Ui.Dim(Loc.T("Games.Subtitle")),
            new Border { Height = 14 },
            libraryCard,
            moveCard,
            Ui.Row(Ui.Heading(Loc.T("Games.Installed")), Ui.Btn(Loc.T("Games.Rescan"), ScanAsync)),
            new Border { Height = 6 },
            Ui.Dim(Loc.T("Games.PinHelp"), 11.5),
            new Border { Height = 8 },
            _games));
        Render();
    }

    private static void FillDrives(ComboBox combo)
    {
        combo.Items.Clear();
        foreach (var a in Svc.LastStatus?.Accounts.Where(a => a.Mount == MountState.Mounted).OrderBy(a => a.Account.DriveLetter) ?? Enumerable.Empty<AccountStatusDto>())
            combo.Items.Add(new ComboBoxItem { Content = $"{a.Account.DriveLetter}:  {a.Account.DisplayName}", Tag = a.Account.Id });
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    public override async Task RefreshAsync()
    {
        if (_driveForLibrary.Items.Count == 0) FillDrives(_driveForLibrary);
        if (DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(20)) await ScanAsync();
    }

    private async Task ScanAsync()
    {
        _lastScan = DateTime.UtcNow;
        try { _last = await Svc.Call<List<GameEntryDto>>(IpcMethods.ListGames, null, 120000) ?? new(); }
        catch (ServiceUnavailableException) { _last = new(); }
        Render();
    }

    private void Render()
    {
        _games.Children.Clear();
        if (_last.Count == 0) { _games.Children.Add(Ui.Card(Ui.Dim(Loc.T("Games.None")))); return; }
        foreach (var g in _last.OrderBy(g => g.Name))
        {
            var frac = g.SizeOnDisk > 0 ? (double)g.CachedBytes / g.SizeOnDisk : 0;
            var path = g.InstallPath;
            var left = Ui.Stack(
                Ui.Text(g.Name, 14, "Text", FontWeights.SemiBold),
                Ui.Dim($"{g.Launcher}{(g.AppId.Length > 0 ? " · AppID " + g.AppId : "")} · {path}"),
                Ui.UsageBar(frac, g.Pinned ? "Good" : "Accent"),
                Ui.Dim(Loc.F("Games.Cached", Bytes.Format(g.CachedBytes), Bytes.Format(g.SizeOnDisk), $"{Math.Min(100, frac * 100):0}")
                       + (g.Pinned ? " · " + Loc.T("Props.Pinned") : "")));
            var actions = Ui.Wrap(
                g.Pinned
                    ? Ui.Btn(Loc.T("Files.Unpin"), async () => { await Svc.Call(IpcMethods.SetPinned, new SetPinnedParams { Path = path, Pinned = false }, 120000); await ScanAsync(); })
                    : Ui.Btn(Loc.T("Games.PinOffline"), async () => { await Svc.Call(IpcMethods.SetPinned, new SetPinnedParams { Path = path, Pinned = true }, 120000); await ScanAsync(); }, true),
                Ui.Btn(Loc.T("Games.Browse"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })));
            _games.Children.Add(Ui.Card(Ui.Stack(left, actions)));
        }
    }

    private async Task CreateLibraryAsync()
    {
        if (_driveForLibrary.Selected() is not string id) return;
        var r = await Svc.Call<CreateSteamLibraryResultDto>(IpcMethods.CreateSteamLibrary, new AccountIdParams { AccountId = id }, 60000);
        Ui.Info(Loc.F(r.AlreadyExisted ? "Games.LibraryExists" : "Games.LibraryCreated", r.LibraryPath));
    }

    /// <summary>Copies (or moves) a game folder from any disk to a virtual drive.</summary>
    private async Task TransferGameAsync(bool move)
    {
        var source = Dialogs.PickFolder(Loc.T("Games.PickSource"));
        if (source == null) return;
        var targetRoot = Dialogs.PickFolder(Loc.T("Games.PickTarget"));
        if (targetRoot == null) return;
        if (move && !Ui.Confirm(Loc.F("Games.MoveConfirm", source, targetRoot))) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var p = new FileOps.Progress();
        var task = Task.Run(async () =>
        {
            p.BytesTotal = FileOps.MeasureSize(source, ct);
            if (move) await FileOps.MoveAsync(source, targetRoot, p, ct);
            else await FileOps.CopyAsync(source, targetRoot, p, ct);
        }, ct);
        while (!task.IsCompleted)
        {
            _status.Text = $"{(move ? Loc.T("Files.Moving") : Loc.T("Files.Copying"))} {p.BytesDone * 100 / Math.Max(1, p.BytesTotal)}% — {Bytes.Format(p.BytesDone)} / {Bytes.Format(p.BytesTotal)} · {p.Files} {Loc.T("Files.File")}";
            await Task.WhenAny(task, Task.Delay(400));
        }
        try { await task; _status.Text = Loc.T("Files.Done"); }
        catch (OperationCanceledException) { _status.Text = Loc.T("Add.Cancelled"); }
        await ScanAsync();
    }
}
