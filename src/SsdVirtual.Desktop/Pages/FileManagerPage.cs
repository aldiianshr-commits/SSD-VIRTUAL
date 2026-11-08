using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop.Pages;

public sealed class FileItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string Type => IsDirectory ? Loc.T("Files.Folder") : (Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() + " " + Loc.T("Files.File")).Trim();
    public string SizeText => IsDirectory ? "" : Bytes.Format(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToLocalTime().ToString("g");
    public string Icon => IsDirectory ? "📁" : "📄";
}

/// <summary>File manager for the virtual drives. Every operation goes through the real mounted file system.</summary>
public sealed class FileManagerPage : PageBase
{
    private ComboBox _drive = new();
    private TextBox _path = new();
    private TextBox _search = new();
    private ListView _list = new();
    private StackPanel _preview = new();
    private TextBlock _status = new();
    private readonly ObservableCollection<FileItem> _items = new();
    private readonly Stack<string> _history = new();
    private string _current = "";
    private List<string> _clipboard = new();
    private bool _clipboardCut;
    private CancellationTokenSource? _opCts;
    private string _sortBy = "Name";
    private ListSortDirection _sortDir = ListSortDirection.Ascending;
    private Point _dragStart;

    public override void Rebuild()
    {
        _drive = new ComboBox { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
        _drive.SelectionChanged += (_, _) => { if (_drive.Selected() is string root) Navigate(root); };
        _path = Ui.Input("", 420);
        _path.Margin = new Thickness(0, 0, 8, 0);
        _path.KeyDown += (_, e) => { if (e.Key == Key.Enter) Navigate(_path.Text); };
        _search = Ui.Input("", 180);
        _search.Margin = new Thickness(0, 0, 8, 0);
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SearchAsync(); };
        _status = Ui.Dim("");
        _preview = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };

        _list = new ListView { ItemsSource = _items, SelectionMode = SelectionMode.Extended, AllowDrop = true };
        var view = new GridView();
        view.Columns.Add(Column("", nameof(FileItem.Icon), 34));
        view.Columns.Add(Column(Loc.T("Files.Name"), nameof(FileItem.Name), 340, "Name"));
        view.Columns.Add(Column(Loc.T("Files.Size"), nameof(FileItem.SizeText), 100, "Size"));
        view.Columns.Add(Column(Loc.T("Files.Type"), nameof(FileItem.Type), 120, "Type"));
        view.Columns.Add(Column(Loc.T("Files.Modified"), nameof(FileItem.ModifiedText), 160, "Modified"));
        _list.View = view;
        _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += OnListKey;
        _list.SelectionChanged += async (_, _) => await UpdatePreviewAsync();
        _list.Drop += async (_, e) => await OnDropAsync(e);
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        _list.PreviewMouseMove += OnDragMove;
        _list.ContextMenu = BuildContextMenu();

        var nav = Ui.Row(_drive,
            Ui.Btn("←", () => { if (_history.Count > 0) Navigate(_history.Pop(), false); }),
            Ui.Btn("↑", () => { var parent = Directory.GetParent(_current.TrimEnd('\\')); if (parent != null) Navigate(parent.FullName); }),
            Ui.Btn("⟳", () => Navigate(_current, false)),
            _path, _search, Ui.Btn(Loc.T("Files.Search"), SearchAsync));
        foreach (UIElement c in nav.Children) if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;

        var toolbar = Ui.Wrap(
            Ui.Btn(Loc.T("Files.NewFolder"), NewFolder),
            Ui.Btn(Loc.T("Files.NewFile"), NewFile),
            Ui.Btn(Loc.T("Files.UploadFiles"), UploadFiles),
            Ui.Btn(Loc.T("Files.UploadFolder"), UploadFolder),
            Ui.Btn(Loc.T("Files.Download"), Download),
            Ui.Btn(Loc.T("Files.Copy"), () => SetClipboard(false)),
            Ui.Btn(Loc.T("Files.Cut"), () => SetClipboard(true)),
            Ui.Btn(Loc.T("Files.Paste"), PasteAsync),
            Ui.Btn(Loc.T("Files.Rename"), Rename),
            Ui.Btn(Loc.T("Files.Delete"), DeleteAsync),
            Ui.Btn(Loc.T("Files.ExtractZip"), ExtractAsync),
            Ui.Btn(Loc.T("Files.Properties"), PropertiesAsync),
            Ui.Btn(Loc.T("Files.Pin"), () => PinAsync(true)),
            Ui.Btn(Loc.T("Files.Unpin"), () => PinAsync(false)),
            Ui.Btn(Loc.T("Files.OpenExplorer"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_current}\"") { UseShellExecute = true })),
            Ui.Btn(Loc.T("Common.Cancel"), () => _opCts?.Cancel()));

        var center = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        center.Children.Add(_list);
        var previewCard = Ui.Card(Ui.Scroll(_preview), new Thickness(12, 0, 0, 0), 14);
        Grid.SetColumn(previewCard, 1);
        center.Children.Add(previewCard);

        var dock = new DockPanel();
        var top = Ui.Stack(Ui.Title(Loc.T("Nav.Files")), new Border { Height = 10 }, nav, toolbar);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(_status);
        dock.Children.Add(center);
        Content = dock;
        LoadDrives();
        if (_current.Length > 0) Navigate(_current, false);
    }

    private static GridViewColumn Column(string header, string path, double width, string? sortKey = null)
        => new() { Header = new GridViewColumnHeader { Content = header, Tag = sortKey }, DisplayMemberBinding = new Binding(path), Width = width };

    private void LoadDrives()
    {
        var mounted = Svc.LastStatus?.Accounts.Where(a => a.Mount == MountState.Mounted).OrderBy(a => a.Account.DriveLetter).ToList() ?? new();
        var current = _drive.Selected() as string;
        _drive.Items.Clear();
        foreach (var a in mounted)
            _drive.Items.Add(new ComboBoxItem { Content = $"{a.Account.DriveLetter}:  {a.Account.DisplayName}", Tag = a.Account.DriveLetter + ":\\" });
        if (current != null) _drive.Select(current);
        else if (_drive.Items.Count > 0 && _current.Length == 0) _drive.SelectedIndex = 0;
        if (_drive.Items.Count == 0) _status.Text = Loc.T("Files.NoDrives");
    }

    public override Task RefreshAsync()
    {
        if (_drive.Items.Count != (Svc.LastStatus?.Accounts.Count(a => a.Mount == MountState.Mounted) ?? 0)) LoadDrives();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ navigation

    private void Navigate(string path, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) { Ui.ShowError(new DirectoryNotFoundException(path)); return; }
            if (remember && _current.Length > 0 && !string.Equals(_current, path, StringComparison.OrdinalIgnoreCase)) _history.Push(_current);
            _current = path;
            _path.Text = path;
            var items = new List<FileItem>();
            var dir = new DirectoryInfo(path);
            foreach (var d in dir.EnumerateDirectories())
                items.Add(new FileItem { Name = d.Name, FullPath = d.FullName, IsDirectory = true, Modified = d.LastWriteTimeUtc });
            foreach (var f in dir.EnumerateFiles())
                items.Add(new FileItem { Name = f.Name, FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTimeUtc });
            Show(items);
            _status.Text = Loc.F("Files.Count", items.Count(i => i.IsDirectory), items.Count(i => !i.IsDirectory));
        }
        catch (Exception ex) { Ui.ShowError(ex); }
    }

    private void Show(IEnumerable<FileItem> items)
    {
        IEnumerable<FileItem> sorted = _sortBy switch
        {
            "Size" => items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Size),
            "Type" => items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Type, StringComparer.OrdinalIgnoreCase),
            "Modified" => items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Modified),
            _ => items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = sorted.ToList();
        if (_sortDir == ListSortDirection.Descending)
            list = list.Where(i => i.IsDirectory).Reverse().Concat(list.Where(i => !i.IsDirectory).Reverse()).ToList();
        _items.Clear();
        foreach (var i in list) _items.Add(i);
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h || h.Tag is not string key) return;
        _sortDir = _sortBy == key && _sortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortBy = key;
        Show(_items.ToList());
    }

    private List<FileItem> Selection() => _list.SelectedItems.Cast<FileItem>().ToList();

    private void OpenSelected()
    {
        var sel = Selection().FirstOrDefault();
        if (sel == null) return;
        if (sel.IsDirectory) Navigate(sel.FullPath);
        else
        {
            try { Process.Start(new ProcessStartInfo(sel.FullPath) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.ShowError(ex); }
        }
    }

    private async void OnListKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Enter) OpenSelected();
        else if (e.Key == Key.Delete) await DeleteAsync();
        else if (e.Key == Key.F2) Rename();
        else if (e.Key == Key.Back) { var p = Directory.GetParent(_current.TrimEnd('\\')); if (p != null) Navigate(p.FullName); }
        else if (ctrl && e.Key == Key.C) SetClipboard(false);
        else if (ctrl && e.Key == Key.X) SetClipboard(true);
        else if (ctrl && e.Key == Key.V) await PasteAsync();
        else if (ctrl && e.Key == Key.A) _list.SelectAll();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string key, Func<Task> action)
        {
            var mi = new MenuItem { Header = Loc.T(key) };
            mi.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Ui.ShowError(ex); } };
            menu.Items.Add(mi);
        }
        Add("Files.Open", () => { OpenSelected(); return Task.CompletedTask; });
        Add("Files.Copy", () => { SetClipboard(false); return Task.CompletedTask; });
        Add("Files.Cut", () => { SetClipboard(true); return Task.CompletedTask; });
        Add("Files.Paste", PasteAsync);
        Add("Files.Rename", () => { Rename(); return Task.CompletedTask; });
        Add("Files.Delete", DeleteAsync);
        Add("Files.ExtractZip", ExtractAsync);
        Add("Files.Pin", () => PinAsync(true));
        Add("Files.Unpin", () => PinAsync(false));
        Add("Files.Properties", PropertiesAsync);
        return menu;
    }

    // ------------------------------------------------------------------ operations

    private async Task RunAsync(string label, Func<FileOps.Progress, CancellationToken, Task> work, long total = 0)
    {
        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        var ct = _opCts.Token;
        var progress = new FileOps.Progress { BytesTotal = total };
        var task = Task.Run(() => work(progress, ct), ct);
        while (!task.IsCompleted)
        {
            var pct = progress.BytesTotal > 0 ? $" {progress.BytesDone * 100 / Math.Max(1, progress.BytesTotal)}%" : "";
            _status.Text = $"{label}{pct} — {Bytes.Format(progress.BytesDone)} · {Path.GetFileName(progress.Current)}";
            await Task.WhenAny(task, Task.Delay(300));
        }
        try
        {
            await task;
            _status.Text = $"{label} — {Loc.T("Files.Done")}";
        }
        catch (OperationCanceledException) { _status.Text = $"{label} — {Loc.T("Add.Cancelled")}"; }
        catch (Exception ex) { _status.Text = ""; Ui.ShowError(ex); }
        Navigate(_current, false);
    }

    private void NewFolder()
    {
        var name = Dialogs.Prompt(Loc.T("Files.NewFolder"), Loc.T("Files.Name"), Loc.T("Files.NewFolderName"), Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        Directory.CreateDirectory(FileOps.UniqueTarget(_current, name.Trim()));
        Navigate(_current, false);
    }

    private void NewFile()
    {
        var name = Dialogs.Prompt(Loc.T("Files.NewFile"), Loc.T("Files.Name"), Loc.T("Files.NewFileName"), Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        using (File.Create(FileOps.UniqueTarget(_current, name.Trim()))) { }
        Navigate(_current, false);
    }

    private async Task UploadFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Loc.T("Files.UploadFiles") };
        if (dlg.ShowDialog() != true) return;
        await CopyInto(dlg.FileNames, _current);
    }

    private async Task UploadFolder()
    {
        var folders = Dialogs.PickFolders(Loc.T("Files.UploadFolder"));
        if (folders == null) return;
        await CopyInto(folders, _current);
    }

    private async Task Download()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        var target = Dialogs.PickFolder(Loc.T("Files.DownloadTo"));
        if (target == null) return;
        await CopyInto(sel.Select(s => s.FullPath).ToArray(), target);
    }

    private async Task CopyInto(string[] sources, string target)
    {
        await RunAsync(Loc.T("Files.Copying"), async (p, ct) =>
        {
            p.BytesTotal = sources.Sum(s => FileOps.MeasureSize(s, ct));
            foreach (var s in sources) await FileOps.CopyAsync(s, target, p, ct);
        });
    }

    private void SetClipboard(bool cut)
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        _clipboard = sel.Select(s => s.FullPath).ToList();
        _clipboardCut = cut;
        try
        {
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange(_clipboard.ToArray());
            Clipboard.SetFileDropList(files);
        }
        catch { }
        _status.Text = Loc.F(cut ? "Files.CutCount" : "Files.CopiedCount", _clipboard.Count);
    }

    private async Task PasteAsync()
    {
        var sources = _clipboard.ToList();
        var cut = _clipboardCut;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var drop = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (!drop.SequenceEqual(sources)) { sources = drop; cut = false; }
            }
        }
        catch { }
        if (sources.Count == 0) return;
        var target = _current;
        await RunAsync(cut ? Loc.T("Files.Moving") : Loc.T("Files.Copying"), async (p, ct) =>
        {
            p.BytesTotal = sources.Sum(s => FileOps.MeasureSize(s, ct));
            foreach (var s in sources)
            {
                if (cut) await FileOps.MoveAsync(s, target, p, ct);
                else await FileOps.CopyAsync(s, target, p, ct);
            }
        });
        if (cut) _clipboard.Clear();
    }

    private void Rename()
    {
        var sel = Selection().FirstOrDefault();
        if (sel == null) return;
        var name = Dialogs.Prompt(Loc.T("Files.Rename"), Loc.T("Files.Name"), sel.Name, Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name) || name == sel.Name) return;
        var target = Path.Combine(_current, name.Trim());
        try
        {
            if (sel.IsDirectory) Directory.Move(sel.FullPath, target);
            else File.Move(sel.FullPath, target);
        }
        catch (Exception ex) { Ui.ShowError(ex); }
        Navigate(_current, false);
    }

    private async Task DeleteAsync()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        if (!Ui.Confirm(Loc.F("Files.DeleteConfirm", sel.Count))) return;
        await RunAsync(Loc.T("Files.Deleting"), (p, ct) =>
        {
            foreach (var s in sel) { ct.ThrowIfCancellationRequested(); p.Current = s.FullPath; FileOps.Delete(s.FullPath); }
            return Task.CompletedTask;
        });
    }

    private async Task ExtractAsync()
    {
        var zips = Selection().Where(s => !s.IsDirectory && s.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 0) { Ui.Info(Loc.T("Files.SelectZip")); return; }
        await RunAsync(Loc.T("Files.Extracting"), (p, ct) =>
        {
            foreach (var z in zips)
            {
                var target = FileOps.UniqueTarget(_current, Path.GetFileNameWithoutExtension(z.Name));
                p.Current = z.FullPath;
                using var archive = ZipFile.OpenRead(z.FullPath);
                p.BytesTotal += archive.Entries.Sum(e => e.Length);
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                    if (!dest.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: false);
                    Interlocked.Add(ref p.BytesDone, entry.Length);
                }
            }
            return Task.CompletedTask;
        });
    }

    private async Task PinAsync(bool pinned)
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        foreach (var s in sel) await Svc.Call(IpcMethods.SetPinned, new SetPinnedParams { Path = s.FullPath, Pinned = pinned }, 120000);
        _status.Text = pinned ? Loc.F("Files.Pinned", sel.Count) : Loc.F("Files.Unpinned", sel.Count);
    }

    private async Task PropertiesAsync()
    {
        var sel = Selection().FirstOrDefault();
        var path = sel?.FullPath ?? _current;
        var info = await Svc.Call<PathInfoDto>(IpcMethods.GetPathInfo, new PathParams { Path = path }, 120000);
        string attrs = "";
        DateTime created = default;
        try { attrs = File.GetAttributes(path).ToString(); created = File.GetCreationTime(path); } catch { }
        Ui.Info(string.Join("\n", new[]
        {
            path,
            "",
            $"{Loc.T("Files.Type")}: {(info.IsDirectory ? Loc.T("Files.Folder") : Loc.T("Files.File"))}",
            $"{Loc.T("Files.Size")}: {Bytes.Format(info.Size)} ({info.Size:N0} bytes)",
            $"{Loc.T("Props.Cached")}: {Bytes.Format(info.CachedBytes)} ({(info.Size > 0 ? info.CachedBytes * 100 / info.Size : 100)}%)",
            $"{Loc.T("Props.Pinned")}: {(info.Pinned ? Loc.T("Common.Yes") : Loc.T("Common.No"))}",
            $"{Loc.T("Props.SyncState")}: {info.SyncState}",
            $"{Loc.T("Props.Created")}: {created:g}",
            $"{Loc.T("Files.Modified")}: {info.ModifiedUtc.ToLocalTime():g}",
            $"{Loc.T("Props.Attributes")}: {attrs}",
            $"{Loc.T("Props.RemoteId")}: {info.RemoteId}",
        }));
    }

    private async Task SearchAsync()
    {
        var text = _search.Text.Trim();
        if (text.Length == 0) { Navigate(_current, false); return; }
        var root = _current;
        var results = new List<FileItem>();
        await RunAsync(Loc.T("Files.Searching"), (p, ct) =>
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + text + "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                p.Current = e.FullName;
                results.Add(new FileItem
                {
                    Name = Path.GetRelativePath(root, e.FullName), FullPath = e.FullName, IsDirectory = e is DirectoryInfo,
                    Size = e is System.IO.FileInfo f ? f.Length : 0, Modified = e.LastWriteTimeUtc,
                });
                if (results.Count >= 2000) break;
            }
            return Task.CompletedTask;
        });
        Show(results);
        _status.Text = Loc.F("Files.SearchResults", results.Count, text);
    }

    // ------------------------------------------------------------------ preview & drag-drop

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tif", ".tiff" };
    private static readonly string[] TextExt = { ".txt", ".log", ".json", ".xml", ".md", ".ini", ".cfg", ".csv", ".yml", ".yaml", ".vdf", ".acf", ".bat", ".ps1", ".cs", ".html", ".css", ".js" };

    private async Task UpdatePreviewAsync()
    {
        _preview.Children.Clear();
        var sel = Selection();
        if (sel.Count != 1)
        {
            if (sel.Count > 1) _preview.Children.Add(Ui.Text(Loc.F("Files.SelectedCount", sel.Count, Bytes.Format(sel.Sum(s => s.Size)))));
            return;
        }
        var item = sel[0];
        _preview.Children.Add(Ui.Heading(item.Name));
        _preview.Children.Add(Ui.Dim($"{item.Type} · {item.SizeText}"));
        _preview.Children.Add(Ui.Dim(item.ModifiedText));
        if (item.IsDirectory) return;
        var ext = Path.GetExtension(item.Name).ToLowerInvariant();
        try
        {
            if (ImageExt.Contains(ext) && item.Size < 64 * Bytes.MiB)
            {
                var bytes = await Task.Run(() => File.ReadAllBytes(item.FullPath));
                if (Selection().FirstOrDefault() != item) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 540;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                _preview.Children.Add(new Image { Source = bmp, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 270 });
            }
            else if (TextExt.Contains(ext))
            {
                var text = await Task.Run(() =>
                {
                    using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buf = new byte[Math.Min(64 * 1024, fs.Length)];
                    var n = fs.Read(buf, 0, buf.Length);
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n);
                });
                if (Selection().FirstOrDefault() != item) return;
                var tb = Ui.Text(text, 11.5);
                tb.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
                tb.Margin = new Thickness(0, 10, 0, 0);
                _preview.Children.Add(tb);
            }
            var info = await Svc.Call<PathInfoDto>(IpcMethods.GetPathInfo, new PathParams { Path = item.FullPath }, 10000);
            if (info.Exists && Selection().FirstOrDefault() == item)
                _preview.Children.Add(Ui.Dim($"{info.SyncState}{(info.Pinned ? " · " + Loc.T("Props.Pinned") : "")}"));
        }
        catch (Exception ex)
        {
            _preview.Children.Add(Ui.Text(ex.Message, 12, "Warn"));
        }
    }

    private async Task OnDropAsync(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), _current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        await CopyInto(files, _current);
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var sel = Selection();
        if (sel.Count == 0) return;
        var data = new DataObject(DataFormats.FileDrop, sel.Select(s => s.FullPath).ToArray());
        DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy);
    }
}
