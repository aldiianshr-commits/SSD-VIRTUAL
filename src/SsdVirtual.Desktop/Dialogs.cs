using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SsdVirtual.Desktop;

/// <summary>Small modal dialogs built in code (themed like the main window).</summary>
public static class Dialogs
{
    private static Window Shell(string title, UIElement body, Window? owner)
    {
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            MinWidth = 380,
            Content = new Border { Padding = new Thickness(22), Child = body },
            ShowInTaskbar = false,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        WindowChrome.ApplyDarkTitleBar(w, App.Prefs.Theme == "Dark");
        return w;
    }

    public static string? Prompt(string title, string label, string initial, Window? owner = null)
    {
        var box = Ui.Input(initial, 320);
        string? result = null;
        Window? w = null;
        var ok = new Button { Content = Loc.T("Common.OK"), IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = Loc.T("Common.Cancel"), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; w!.DialogResult = true; };
        var buttons = Ui.Row(ok, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        w = Shell(title, Ui.Stack(Ui.Dim(label), box, buttons), owner);
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>Drive letters that are free right now (not used by Windows or another SsdVirtual drive).</summary>
    public static List<char> FreeLetters(char? keep = null)
    {
        var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        foreach (var a in Svc.LastStatus?.Accounts ?? new()) used.Add(char.ToUpperInvariant(a.Account.DriveLetter));
        if (keep is { } k) used.Remove(char.ToUpperInvariant(k));
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Where(c => !used.Contains(c)).ToList();
    }

    public static char? PickLetter(string title, char? current, Window? owner = null)
    {
        var letters = FreeLetters(current);
        var combo = Ui.Combo(letters.Select(c => ((object)c, c + ":")).ToArray());
        if (current is { } cur) combo.Select(char.ToUpperInvariant(cur));
        char? result = null;
        Window? w = null;
        var ok = new Button { Content = Loc.T("Common.OK"), IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = Loc.T("Common.Cancel"), IsCancel = true };
        ok.Click += (_, _) => { result = combo.Selected() as char?; w!.DialogResult = true; };
        var buttons = Ui.Row(ok, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        w = Shell(title, Ui.Stack(Ui.Dim(Loc.T("Disks.ChooseLetter")), combo, buttons), owner);
        return w.ShowDialog() == true ? result : null;
    }

    public static (bool Ok, bool DeleteCache, bool Force) ConfirmRemove(string name, Window? owner = null)
    {
        var del = new CheckBox { Content = Loc.T("Accounts.DeleteCache"), IsChecked = true };
        var force = new CheckBox { Content = Loc.T("Accounts.ForceRemove"), IsChecked = false };
        bool ok = false;
        Window? w = null;
        var yes = new Button { Content = Loc.T("Accounts.Remove"), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = Loc.T("Common.Cancel"), IsCancel = true };
        yes.Click += (_, _) => { ok = true; w!.DialogResult = true; };
        var buttons = Ui.Row(yes, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 12, 0, 0);
        w = Shell(Loc.T("Accounts.Remove"), Ui.Stack(Ui.Text(Loc.F("Accounts.RemoveConfirm", name)), Ui.Dim(Loc.T("Accounts.RemoveNote")), del, force, buttons), owner);
        w.ShowDialog();
        return (ok, del.IsChecked == true, force.IsChecked == true);
    }

    public static string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static string[]? PickFolders(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = true };
        return dlg.ShowDialog() == true ? dlg.FolderNames : null;
    }
}
