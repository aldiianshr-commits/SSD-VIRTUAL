using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

public sealed class SyncPage : PageBase
{
    public sealed class Row
    {
        public string Drive { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public string State { get; set; } = "";
        public string Attempts { get; set; } = "";
        public string Error { get; set; } = "";
        public string Updated { get; set; } = "";
    }

    private StackPanel _summary = new();
    private readonly ObservableCollection<Row> _rows = new();

    public override void Rebuild()
    {
        _summary = new StackPanel();
        var list = new ListView { ItemsSource = _rows, MinHeight = 320 };
        var view = new GridView();
        void Col(string h, string p, double w) => view.Columns.Add(new GridViewColumn { Header = h, DisplayMemberBinding = new Binding(p), Width = w });
        Col(Loc.T("Sync.Drive"), nameof(Row.Drive), 120);
        Col(Loc.T("Sync.Kind"), nameof(Row.Kind), 90);
        Col(Loc.T("Files.Name"), nameof(Row.Path), 360);
        Col(Loc.T("Disks.Status"), nameof(Row.State), 130);
        Col(Loc.T("Sync.Attempts"), nameof(Row.Attempts), 70);
        Col(Loc.T("Sync.Error"), nameof(Row.Error), 260);
        Col(Loc.T("Sync.Updated"), nameof(Row.Updated), 140);
        list.View = view;

        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Sync")),
            Ui.Dim(Loc.T("Sync.Subtitle")),
            Ui.Row(
                Ui.Btn(Loc.T("Sync.Now"), () => Svc.Call(IpcMethods.SyncNow, new AccountIdParams()), true),
                Ui.Btn(Loc.T("Sync.RetryFailed"), () => Svc.Call(IpcMethods.RetryFailed))),
            new Border { Height = 14 },
            _summary,
            Ui.Heading(Loc.T("Sync.Queue")),
            new Border { Height = 8 },
            list,
            Ui.Dim(Loc.T("Sync.ConflictHelp"), 11.5)));
        _ = RefreshAsync();
    }

    public override async Task RefreshAsync()
    {
        var accounts = Svc.LastStatus?.Accounts ?? new();
        _summary.Children.Clear();
        var wrap = new WrapPanel();
        foreach (var a in accounts.OrderBy(a => a.Account.DriveLetter))
        {
            wrap.Children.Add(Ui.Stat($"{a.Account.DriveLetter}:  {a.Account.DisplayName}", Ui.SyncText(a.Sync),
                Loc.F("Sync.Summary", a.PendingUploads, a.PendingOperations, a.Conflicts) +
                (a.LastSyncUtc is { } t ? "\n" + Loc.F("Sync.LastSync", t.ToLocalTime().ToString("T")) : ""),
                Ui.StatusBrush(a.Sync)));
        }
        _summary.Children.Add(wrap);

        List<SyncItemDto> items;
        try { items = await Svc.Call<List<SyncItemDto>>(IpcMethods.ListSyncItems, null, 10000) ?? new(); }
        catch (Exception) { return; }
        var letters = accounts.ToDictionary(a => a.Account.Id, a => $"{a.Account.DriveLetter}: {a.Account.DisplayName}");
        _rows.Clear();
        foreach (var i in items.OrderByDescending(i => i.UpdatedUtc).Take(500))
            _rows.Add(new Row
            {
                Drive = letters.TryGetValue(i.AccountId, out var l) ? l : "",
                Kind = Loc.T("SyncKind." + i.Kind),
                Path = i.Path,
                State = i.State,
                Attempts = i.Attempts > 0 ? i.Attempts.ToString() : "",
                Error = i.LastError,
                Updated = i.UpdatedUtc.Year > 2000 ? i.UpdatedUtc.ToLocalTime().ToString("g") : "",
            });
    }
}
