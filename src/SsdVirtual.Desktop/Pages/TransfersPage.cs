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

public sealed class TransfersPage : PageBase
{
    public sealed class Row
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public string Progress { get; set; } = "";
        public double Percent { get; set; }
        public string Speed { get; set; } = "";
        public string State { get; set; } = "";
        public string Error { get; set; } = "";
    }

    private ListView _list = new();
    private TextBlock _summary = new();
    private readonly ObservableCollection<Row> _rows = new();

    public override void Rebuild()
    {
        _summary = Ui.Dim("");
        _list = new ListView { ItemsSource = _rows, MinHeight = 420, SelectionMode = SelectionMode.Extended };
        var view = new GridView();
        void Col(string h, string p, double w) => view.Columns.Add(new GridViewColumn { Header = h, DisplayMemberBinding = new Binding(p), Width = w });
        Col(Loc.T("Sync.Kind"), nameof(Row.Kind), 90);
        Col(Loc.T("Files.Name"), nameof(Row.Path), 380);
        var progress = new FrameworkElementFactory(typeof(ProgressBar));
        progress.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(Row.Percent)));
        progress.SetValue(FrameworkElement.WidthProperty, 110.0);
        view.Columns.Add(new GridViewColumn { Header = "%", Width = 130, CellTemplate = new DataTemplate { VisualTree = progress } });
        Col(Loc.T("Transfers.Progress"), nameof(Row.Progress), 170);
        Col(Loc.T("Transfers.Speed"), nameof(Row.Speed), 100);
        Col(Loc.T("Disks.Status"), nameof(Row.State), 100);
        Col(Loc.T("Sync.Error"), nameof(Row.Error), 220);
        _list.View = view;
        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Transfers")),
            Ui.Dim(Loc.T("Transfers.Subtitle")),
            Ui.Row(Ui.Btn(Loc.T("Transfers.CancelSelected"), CancelSelectedAsync)),
            new Border { Height = 10 },
            _summary,
            new Border { Height = 8 },
            _list));
        _ = RefreshAsync();
    }

    private async Task CancelSelectedAsync()
    {
        foreach (var r in _list.SelectedItems.Cast<Row>().ToList())
            await Svc.Call<bool>(IpcMethods.CancelTransfer, new CancelTransferParams { TransferId = r.Id });
    }

    public override async Task RefreshAsync()
    {
        List<TransferDto> list;
        try { list = await Svc.Call<List<TransferDto>>(IpcMethods.ListTransfers, null, 10000) ?? new(); }
        catch (Exception) { return; }
        var selected = _list.SelectedItems.Cast<Row>().Select(r => r.Id).ToHashSet();
        _rows.Clear();
        foreach (var t in list)
        {
            var row = new Row
            {
                Id = t.Id,
                Kind = Loc.T("Transfer." + t.Kind),
                Path = t.Path,
                Percent = t.BytesTotal > 0 ? Math.Min(100, t.BytesDone * 100.0 / t.BytesTotal) : (t.State == "Completed" ? 100 : 0),
                Progress = $"{Bytes.Format(t.BytesDone)} / {Bytes.Format(t.BytesTotal)}",
                Speed = t.BytesPerSecond > 0 ? Bytes.Format((long)t.BytesPerSecond) + "/s" : "",
                State = Loc.T("TransferState." + t.State),
                Error = t.Error,
            };
            _rows.Add(row);
            if (selected.Contains(row.Id)) _list.SelectedItems.Add(row);
        }
        var running = list.Count(t => t.State is "Running" or "Preparing");
        _summary.Text = Loc.F("Transfers.Summary", running, list.Count(t => t.State == "Completed"), list.Count(t => t.State == "Failed"));
    }
}
