using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SsdVirtual.Desktop;

/// <summary>Windows 11 look: dark title bar and Mica backdrop where supported (no-ops elsewhere).</summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    public static void ApplyDarkTitleBar(Window w, bool dark)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref v, sizeof(int)); } catch { }
            int round = 2;
            try { DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int)); } catch { }
        }
        if (new WindowInteropHelper(w).Handle == IntPtr.Zero) w.SourceInitialized += (_, _) => Apply();
        else Apply();
    }
}
