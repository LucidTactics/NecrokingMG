using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Necroking.Core;

/// <summary>
/// Win32 helpers for this process's own top-level window. Used in headless /
/// dev-server runs to drop the (off-screen) game window's taskbar button so the
/// supervisor-owned game doesn't clutter the taskbar. The window stays a real,
/// renderable window — only its taskbar presence changes — so screenshots and
/// the GL context are unaffected. No-op on non-Windows.
/// </summary>
public static class WindowChrome
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080; // tool windows are never in the taskbar
    const int WS_EX_APPWINDOW = 0x00040000;   // forces a taskbar button — clear it
    const int SW_HIDE = 0;
    const int SW_SHOW = 5;                      // show + activate
    const int SW_SHOWNA = 8;                   // show without activating/stealing focus

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Remove this process's main window from the Windows taskbar by turning it
    /// into a tool window. Returns true once applied (or already applied); false
    /// if the window handle isn't available yet — call again on a later frame.
    /// Safe to call repeatedly. No-op (returns true) off Windows.
    /// </summary>
    public static bool HideFromTaskbar()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        IntPtr hwnd;
        using (var p = Process.GetCurrentProcess()) hwnd = p.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return false; // window not realised yet — retry later

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        int want = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        if (want != ex)
        {
            // The taskbar button only refreshes across a hide/show cycle. Re-show
            // without activating (SW_SHOWNA) so the off-screen window keeps
            // rendering and we don't steal focus from the user's foreground app.
            ShowWindow(hwnd, SW_HIDE);
            SetWindowLong(hwnd, GWL_EXSTYLE, want);
            ShowWindow(hwnd, SW_SHOWNA);
        }
        return true;
    }

    /// <summary>
    /// Reverse of <see cref="HideFromTaskbar"/>: give this process's window a real
    /// taskbar button again and bring it to the foreground. Used by the dev
    /// `window show` command to surface an otherwise-headless game so the user can
    /// see and click it, without restarting the process. Returns false if the
    /// window handle isn't ready yet. No-op (returns true) off Windows.
    /// </summary>
    public static bool RestoreToTaskbarAndFocus()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        IntPtr hwnd;
        using (var p = Process.GetCurrentProcess()) hwnd = p.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return false;

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        int want = (ex | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW;
        // The taskbar button only refreshes across a hide/show cycle.
        ShowWindow(hwnd, SW_HIDE);
        if (want != ex) SetWindowLong(hwnd, GWL_EXSTYLE, want);
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        return true;
    }
}
