using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SsdVirtual.Core;
using SsdVirtual.Core.Ipc;

namespace SsdVirtual.Desktop;

/// <summary>Small builders for the code-constructed pages, all theme-aware (DynamicResource).</summary>
public static class Ui
{
    public static T Res<T>(this T el, DependencyProperty dp, string key) where T : FrameworkElement
    {
        el.SetResourceReference(dp, key);
        return el;
    }

    public static TextBlock Text(string text, double size = 13, string brush = "Text", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Title(string text) => Text(text, 22, "Text", FontWeights.SemiBold);
    public static TextBlock Heading(string text) => Text(text, 15, "Text", FontWeights.SemiBold);
    public static TextBlock Dim(string text, double size = 12) => Text(text, size, "TextDim");

    public static Border Card(UIElement content, Thickness? margin = null, double padding = 18)
    {
        var b = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(padding),
            Margin = margin ?? new Thickness(0, 0, 0, 14),
        };
        b.SetResourceReference(Border.BackgroundProperty, "Surface");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static WrapPanel Wrap(params UIElement[] children)
    {
        var s = new WrapPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static Button Btn(string text, Func<Task> onClick, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 8, 0) };
        if (primary) b.Style = (Style)Application.Current.FindResource("PrimaryButton");
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            catch (Exception ex) { ShowError(ex); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static Button Btn(string text, Action onClick, bool primary = false)
        => Btn(text, () => { onClick(); return Task.CompletedTask; }, primary);

    /// <summary>Stat tile: small label over a large value.</summary>
    public static Border Stat(string label, string value, string? sub = null, string valueBrush = "Text")
    {
        var s = Stack(Dim(label), Text(value, 24, valueBrush, FontWeights.SemiBold));
        if (sub != null) s.Children.Add(Dim(sub, 11.5));
        var card = Card(s, new Thickness(0, 0, 12, 12), 16);
        card.MinWidth = 190;
        return card;
    }

    /// <summary>Horizontal usage bar with label.</summary>
    public static FrameworkElement UsageBar(double fraction, string brush = "Accent", double height = 8)
    {
        fraction = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        var g = new Grid { Height = height, Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(height / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "Track");
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(height / 2) };
        fill.SetResourceReference(Border.BackgroundProperty, brush);
        g.Children.Add(track);
        g.Children.Add(fill);
        return g;
    }

    public static Ellipse Dot(string brush, double size = 9)
    {
        var e = new Ellipse { Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        e.SetResourceReference(Shape.FillProperty, brush);
        return e;
    }

    public static string StatusBrush(SyncStatus s) => s switch
    {
        SyncStatus.Synced => "Good",
        SyncStatus.Offline or SyncStatus.Conflict or SyncStatus.RetryRequired or SyncStatus.PendingUpload => "Warn",
        SyncStatus.Error => "Bad",
        _ => "Accent",
    };

    public static string ConnectionBrush(ConnectionState c) => c switch
    {
        ConnectionState.Online => "Good",
        ConnectionState.Offline or ConnectionState.Unknown => "Warn",
        ConnectionState.Disconnected => "TextDim",
        _ => "Bad",
    };

    public static string SyncText(SyncStatus s) => Loc.T("Sync." + s);
    public static string ConnectionText(ConnectionState c) => Loc.T("Conn." + c);
    public static string MountText(MountState m) => Loc.T("Mount." + m);

    public static string ProviderText(ProviderKind p) => p switch
    {
        ProviderKind.GoogleDrive => "Google Drive",
        ProviderKind.TeraBox => "TeraBox",
        _ => Loc.T("Provider.LocalFolder"),
    };

    public static void ShowError(Exception ex)
    {
        var msg = ex is ServiceUnavailableException ? Loc.T("Error.ServiceUnavailable") : ex.Message;
        MessageBox.Show(Application.Current?.MainWindow!, msg, "SsdVirtual", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void Info(string message)
        => MessageBox.Show(Application.Current?.MainWindow!, message, "SsdVirtual", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message)
        => MessageBox.Show(Application.Current?.MainWindow!, message, "SsdVirtual", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, 8, 0),
    };

    public static ComboBox Combo(params (object Value, string Label)[] items)
    {
        var c = new ComboBox { Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (v, l) in items) c.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    public static object? Selected(this ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag;

    public static void Select(this ComboBox c, object? value)
    {
        for (int i = 0; i < c.Items.Count; i++)
            if (c.Items[i] is ComboBoxItem it && Equals(it.Tag, value)) { c.SelectedIndex = i; return; }
    }

    public static TextBox Input(string text = "", double width = 260)
        => new() { Text = text, Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };

    public static UIElement Field(string label, UIElement control) => Stack(Dim(label), control);
}
