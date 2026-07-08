// SPDX-License-Identifier: MIT
// Vantage — Common/NativeInterop.cs
//
// Win32 P/Invoke surface shared by every view / agent path that needs
// to talk directly to user32 / shell32. Centralised here so the
// declarations and constants aren't sprinkled across MainWindow
// partials and agent helpers — when a constant or signature changes,
// there's one place to fix it.

using System;
using System.Runtime.InteropServices;

namespace Vantage.Common;

internal static class NativeInterop
{
    // ── Win32 constants ────────────────────────────────────────────

    public const int SW_SHOWNORMAL       = 1;
    public const int SW_SHOWMINIMIZED    = 2;
    public const int SW_SHOWMAXIMIZED    = 3;
    public const int SW_SHOWNOACTIVATE   = 4;
    public const int SW_SHOW             = 5;
    public const int SW_MINIMIZE         = 6;
    public const int SW_SHOWMINNOACTIVE  = 7;
    public const int SW_SHOWNA           = 8;
    public const int SW_RESTORE          = 9;
    public const int SW_SHOWDEFAULT      = 10;
    public const int SW_FORCEMINIMIZE    = 11;

    // HWND placed at the TOP of the Z-order (often sent as hwndInsertAfter).
    public static readonly IntPtr HWND_TOP       = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM    = new IntPtr(1);
    public static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public const uint SWP_NOMOVE       = 0x0002;
    public const uint SWP_NOSIZE       = 0x0001;
    public const uint SWP_NOZORDER     = 0x0004;
    public const uint SWP_NOACTIVATE   = 0x0010;
    public const uint SWP_SHOWWINDOW   = 0x0040;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // ── Foreground / focus / window ordering ───────────────────────

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
