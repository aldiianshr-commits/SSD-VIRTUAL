using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

/// <summary>Live measurements from the service and an on-demand benchmark. No number here is estimated.</summary>
public sealed class PerformancePage : PageBase
{
    private WrapPanel _tiles = new();
    private Canvas _chart = new();
    private ComboBox _benchDrive = new();
    private TextBlock _benchResult = new();
    private readonly List<(double Down, double Up)> _samples = new();

    public override void Rebuild()
    {
        _tiles = new WrapPanel();
        _chart = new Canvas { Height = 160, ClipToBounds = true };
        _benchDrive = new ComboBox { Margin = new Thickness(0, 6, 8, 0) };
        foreach (var a in Svc.LastStatus?.Accounts.Where(a => a.Mount == MountState.Mounted) ?? Enumerable.Empty<AccountStatusDto>())
            _benchDrive.Items.Add(new ComboBoxItem { Content = $"{a.Account.DriveLetter}:  {a.Account.DisplayName}", Tag = a.Account.DriveLetter + ":\\" });
        if (_benchDrive.Items.Count > 0) _benchDrive.SelectedIndex = 0;
        _benchResult = Ui.Dim("");
        var size = Ui.Combo((64, "64 MB"), (256, "256 MB"), (1024, "1 GB"));
        size.SelectedIndex = 1;

        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Performance")),
            Ui.Dim(Loc.T("Perf.Subtitle")),
            new Border { Height = 14 },
            _tiles,
            Ui.Card(Ui.Stack(Ui.Heading(Loc.T("Perf.Throughput")), Ui.Row(Ui.Dot("Accent"), Ui.Dim(Loc.T("Perf.Download") + "   "), Ui.Dot("Good"), Ui.Dim(Loc.T("Perf.Upload"))), _chart)),
            Ui.Card(Ui.Stack(
                Ui.Heading(Loc.T("Perf.Benchmark")),
                Ui.Dim(Loc.T("Perf.BenchmarkHelp")),
                Ui.Row(_benchDrive, size, Ui.Btn(Loc.T("Perf.Run"), () => RunBenchmarkAsync((int)(size.Selected() ?? 256)), true)),
                _benchResult))));
        _ = RefreshAsync();
    }

    public override async Task RefreshAsync()
    {
        PerformanceDto p;
        try { p = await Svc.Call<PerformanceDto>(IpcMethods.GetPerformance, null, 5000); }
        catch (Exception) { return; }
        _samples.Add((p.DownloadBytesPerSec, p.UploadBytesPerSec));
        if (_samples.Count > 120) _samples.RemoveAt(0);
        _tiles.Children.Clear();
        var hitRatio = p.CacheHitBlocks + p.CacheMissBlocks > 0 ? p.CacheHitBlocks * 100.0 / (p.CacheHitBlocks + p.CacheMissBlocks) : 0;
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Cpu"), $"{p.ServiceCpuPercent:0.0}%"));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Memory"), Bytes.Format(p.WorkingSetBytes), Loc.F("Perf.Private", Bytes.Format(p.PrivateBytes))));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Threads"), p.ThreadCount.ToString()));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Download"), Bytes.Format((long)p.DownloadBytesPerSec) + "/s", Loc.F("Perf.Total", Bytes.Format(p.BytesDownloaded))));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Upload"), Bytes.Format((long)p.UploadBytesPerSec) + "/s", Loc.F("Perf.Total", Bytes.Format(p.BytesUploaded))));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.CacheHits"), $"{hitRatio:0.0}%", Loc.F("Perf.Blocks", p.CacheHitBlocks, p.CacheMissBlocks)));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.FetchLatency"), p.AvgFetchLatencyMs > 0 ? $"{p.AvgFetchLatencyMs:0} ms" : "—"));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Requests"), $"{p.ReadRequests:N0} / {p.WriteRequests:N0}", Loc.T("Perf.ReadsWrites")));
        _tiles.Children.Add(Ui.Stat(Loc.T("Perf.Uptime"), TimeSpan.FromSeconds(p.UptimeSeconds).ToString(@"d\.hh\:mm\:ss")));
        DrawChart();
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        var w = _chart.ActualWidth > 0 ? _chart.ActualWidth : 800;
        var h = _chart.Height;
        var max = Math.Max(1024 * 1024, _samples.Count == 0 ? 0 : _samples.Max(s => Math.Max(s.Down, s.Up)));
        Polyline Line(Func<(double Down, double Up), double> pick, string brush)
        {
            var pl = new Polyline { StrokeThickness = 2 };
            pl.SetResourceReference(Shape.StrokeProperty, brush);
            for (int i = 0; i < _samples.Count; i++)
                pl.Points.Add(new Point(w - (_samples.Count - 1 - i) * (w / 119.0), h - 4 - pick(_samples[i]) / max * (h - 12)));
            return pl;
        }
        var axis = Ui.Dim(Bytes.Format((long)max) + "/s", 11);
        _chart.Children.Add(axis);
        _chart.Children.Add(Line(s => s.Down, "Accent"));
        _chart.Children.Add(Line(s => s.Up, "Good"));
    }

    /// <summary>
    /// Writes a test file to the virtual drive, reads it back and deletes it. This measures the local
    /// cache path of SsdVirtual (WinFsp + cache); first-time reads of cloud data depend on the internet link.
    /// </summary>
    private async Task RunBenchmarkAsync(int megabytes)
    {
        if (_benchDrive.Selected() is not string root) return;
        var file = System.IO.Path.Combine(root, $".ssdvirtual-benchmark-{Guid.NewGuid():N}.tmp");
        _benchResult.Text = Loc.T("Perf.Running");
        try
        {
            var (write, read, small) = await Task.Run(() =>
            {
                var buffer = new byte[4 * 1024 * 1024];
                new Random(1).NextBytes(buffer);
                long total = megabytes * 1024L * 1024;
                var sw = Stopwatch.StartNew();
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
                {
                    for (long done = 0; done < total; done += buffer.Length) fs.Write(buffer, 0, buffer.Length);
                    fs.Flush(true);
                }
                var writeMBs = total / 1048576.0 / sw.Elapsed.TotalSeconds;
                sw.Restart();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    while (fs.Read(buffer, 0, buffer.Length) > 0) { }
                var readMBs = total / 1048576.0 / sw.Elapsed.TotalSeconds;
                // Random 4 KiB reads (typical for games loading many assets).
                sw.Restart();
                var rnd = new Random(2);
                var small4k = new byte[4096];
                int ops = 0;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
                    while (sw.ElapsedMilliseconds < 3000)
                    {
                        fs.Position = (long)(rnd.NextDouble() * (total - 4096)) & ~4095L;
                        fs.Read(small4k, 0, 4096);
                        ops++;
                    }
                var iops = ops / sw.Elapsed.TotalSeconds;
                File.Delete(file);
                return (writeMBs, readMBs, iops);
            });
            _benchResult.Text = Loc.F("Perf.Result", $"{write:0}", $"{read:0}", $"{small:0}", megabytes);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            _benchResult.Text = ex.Message;
        }
    }
}
