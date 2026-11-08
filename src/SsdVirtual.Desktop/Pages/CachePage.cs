using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

public sealed class CachePage : PageBase
{
    private StackPanel _accounts = new();
    private StackPanel _pinned = new();
    private TextBlock _location = new();
    private string _signature = "";

    public override void Rebuild()
    {
        _accounts = new StackPanel();
        _pinned = new StackPanel();
        _location = Ui.Text("");
        _signature = "";
        var locationCard = Ui.Card(Ui.Stack(
            Ui.Heading(Loc.T("Cache.Location")),
            _location,
            Ui.Dim(Loc.T("Cache.LocationHelp")),
            Ui.Row(Ui.Btn(Loc.T("Cache.ChangeLocation"), ChangeLocationAsync),
                Ui.Btn(Loc.T("Cache.ClearAll"), async () =>
                {
                    if (!Ui.Confirm(Loc.T("Cache.ClearConfirm"))) return;
                    var n = await Svc.Call<int>(IpcMethods.ClearCache, new ClearCacheParams(), 120000);
                    Ui.Info(Loc.F("Cache.Cleared", n));
                }),
                Ui.Btn(Loc.T("Cache.Verify"), async () =>
                {
                    var r = await Svc.Call<VerifyResultDto>(IpcMethods.VerifyCache, new ClearCacheParams(), 3600_000);
                    Ui.Info(Loc.F("Cache.VerifyResult", r.FilesChecked, r.FilesSkipped, r.Corrupted) + (r.Problems.Count > 0 ? "\n\n" + string.Join("\n", r.Problems.Take(20)) : ""));
                }))));
        Content = Ui.Scroll(Ui.Stack(
            Ui.Title(Loc.T("Nav.Cache")),
            Ui.Dim(Loc.T("Cache.Subtitle")),
            new Border { Height = 14 },
            locationCard,
            _accounts,
            Ui.Heading(Loc.T("Cache.PinnedItems")),
            new Border { Height = 8 },
            _pinned));
        _ = RenderAsync(true);
    }

    public override Task RefreshAsync() => RenderAsync(false);

    private async Task RenderAsync(bool force)
    {
        List<CacheStatsDto> stats;
        List<PathInfoDto> pinned;
        try
        {
            stats = await Svc.Call<List<CacheStatsDto>>(IpcMethods.GetCacheStats, null, 10000) ?? new();
            pinned = await Svc.Call<List<PathInfoDto>>(IpcMethods.ListPinned, null, 30000) ?? new();
        }
        catch (Exception) { return; }
        _location.Text = Svc.LastStatus?.Settings.CacheRoot ?? "";
        var accounts = Svc.LastStatus?.Accounts ?? new();
        var sig = string.Join("|", stats.Select(s => $"{s.AccountId}{s.UsedBytes / Bytes.MiB}{s.QuotaBytes}{s.DirtyBytes / Bytes.MiB}{s.CachedFiles}")) + pinned.Count;
        if (!force && sig == _signature) return;
        _signature = sig;

        _accounts.Children.Clear();
        foreach (var s in stats)
        {
            var a = accounts.FirstOrDefault(x => x.Account.Id == s.AccountId);
            if (a == null) continue;
            var id = s.AccountId;
            var size = Ui.Combo(CacheSizes.Presets.Select(p => ((object)p.Bytes, p.Label)).Append(((object)-1L, Loc.T("Cache.Custom"))).ToArray());
            size.Select(s.QuotaBytes);
            if (size.Selected() is not long) size.Items.Insert(0, new ComboBoxItem { Content = Bytes.Format(s.QuotaBytes), Tag = s.QuotaBytes });
            size.Select(s.QuotaBytes);
            size.SelectionChanged += async (_, _) =>
            {
                if (size.Selected() is not long v) return;
                if (v == -1)
                {
                    var input = Dialogs.Prompt(Loc.T("Cache.Custom"), Loc.T("Cache.CustomGb"), (s.QuotaBytes / Bytes.GiB).ToString(), Window.GetWindow(this));
                    if (!double.TryParse(input, out var gb) || gb < 1) return;
                    v = (long)(gb * Bytes.GiB);
                }
                if (v == s.QuotaBytes) return;
                try { await Svc.Call(IpcMethods.SetAccountOptions, new SetAccountOptionsParams { AccountId = id, CacheQuotaBytes = v }); }
                catch (Exception ex) { Ui.ShowError(ex); }
                _signature = "";
            };
            var policy = Ui.Combo((CacheWritePolicy.WriteBack, Loc.T("Cache.WriteBack")), (CacheWritePolicy.WriteThrough, Loc.T("Cache.WriteThrough")));
            policy.Select(a.Account.WritePolicy);
            policy.SelectionChanged += async (_, _) =>
            {
                if (policy.Selected() is CacheWritePolicy wp && wp != a.Account.WritePolicy)
                    try { await Svc.Call(IpcMethods.SetAccountOptions, new SetAccountOptionsParams { AccountId = id, WritePolicy = wp }); }
                    catch (Exception ex) { Ui.ShowError(ex); }
            };
            var ratio = s.Hits + s.Misses > 0 ? (double)s.Hits / (s.Hits + s.Misses) : 0;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = Ui.Stack(
                Ui.Heading($"{a.Account.DisplayName} ({a.Account.DriveLetter}:)"),
                Ui.UsageBar(s.QuotaBytes > 0 ? (double)s.UsedBytes / s.QuotaBytes : 0, s.UsedBytes > s.QuotaBytes ? "Warn" : "Accent"),
                Ui.Dim(Loc.F("Cache.UsedOf", Bytes.Format(s.UsedBytes), Bytes.Format(s.QuotaBytes))),
                Ui.Dim(Loc.F("Cache.Breakdown", Bytes.Format(s.DirtyBytes), Bytes.Format(s.PinnedBytes), s.CachedFiles)),
                Ui.Dim(Loc.F("Cache.HitRatio", $"{ratio * 100:0.0}", s.Hits, s.Misses)),
                Ui.Dim(Loc.F("Cache.DiskFree", Bytes.Format(s.FreeSpaceOnCacheDisk))),
                Ui.Dim(s.CacheDirectory, 11));
            var right = Ui.Stack(Ui.Field(Loc.T("Cache.Size"), size), Ui.Field(Loc.T("Cache.WritePolicy"), policy),
                Ui.Btn(Loc.T("Cache.ClearAccount"), async () =>
                {
                    if (!Ui.Confirm(Loc.T("Cache.ClearConfirm"))) return;
                    var n = await Svc.Call<int>(IpcMethods.ClearCache, new ClearCacheParams { AccountId = id }, 120000);
                    Ui.Info(Loc.F("Cache.Cleared", n));
                }));
            right.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(right);
            _accounts.Children.Add(Ui.Card(grid));
        }

        _pinned.Children.Clear();
        if (pinned.Count == 0) _pinned.Children.Add(Ui.Card(Ui.Dim(Loc.T("Cache.NoPinned"))));
        foreach (var p in pinned)
        {
            var path = p.Path;
            var frac = p.Size > 0 ? (double)p.CachedBytes / p.Size : 1;
            _pinned.Children.Add(Ui.Card(Ui.Stack(
                Ui.Text(path),
                Ui.UsageBar(frac, frac >= 1 ? "Good" : "Accent"),
                Ui.Dim(Loc.F("Games.Cached", Bytes.Format(p.CachedBytes), Bytes.Format(p.Size), $"{frac * 100:0}")),
                Ui.Btn(Loc.T("Files.Unpin"), async () => { await Svc.Call(IpcMethods.SetPinned, new SetPinnedParams { Path = path, Pinned = false }, 60000); _signature = ""; })),
                new Thickness(0, 0, 0, 8), 12));
        }
    }

    private async Task ChangeLocationAsync()
    {
        var folder = Dialogs.PickFolder(Loc.T("Cache.ChangeLocation"));
        if (folder == null) return;
        var target = folder.TrimEnd('\\').EndsWith("SsdVirtualCache", StringComparison.OrdinalIgnoreCase) ? folder : System.IO.Path.Combine(folder, "SsdVirtualCache");
        var root = System.IO.Path.GetPathRoot(target) ?? "";
        if ((Svc.LastStatus?.Accounts ?? new()).Any(a => root.StartsWith(a.Account.DriveLetter + ":", StringComparison.OrdinalIgnoreCase)))
        {
            Ui.ShowError(new InvalidOperationException(Loc.T("Cache.NotOnVirtual")));
            return;
        }
        if (!Ui.Confirm(Loc.F("Cache.MoveConfirm", target))) return;
        var settings = await Svc.Call<AppSettings>(IpcMethods.GetSettings);
        settings.CacheRoot = target;
        await Svc.Call<AppSettings>(IpcMethods.SaveSettings, settings, 3600_000);
        await Svc.RefreshStatusAsync();
        _location.Text = target;
    }
}
