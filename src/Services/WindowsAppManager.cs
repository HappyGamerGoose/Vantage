// SPDX-License-Identifier: MIT
// Vantage — Services/WindowsAppManager.cs
//
// Direct ports of the most useful Windows-MCP tools into the Vantage host
// process. The agent communicates with these via the existing VantageACI
// dispatcher (no MCP stdio subprocess, no extra window); each method is a
// thin Win32 / System.Diagnostics wrapper that returns synchronously so the
// agent loop keeps its tight per-step budget.
//
// Tool surface covered (see VantageACI for the action-name mapping):
//   • launch_app / focus_app / resize_app / close_app / wait_for_app
//   • list_processes / kill_process / focus_process
//   • run_powershell
//   • move_mouse (no-click cursor movement)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Vantage.Services;

public static class WindowsAppManager
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public sealed record WindowInfo(IntPtr Handle, uint Pid, string Title, string ClassName);

    // ─── Window enumeration ─────────────────────────────────────────────

    /// <summary>
    /// Walks every top-level window owned by a visible process and returns
    /// (handle, pid, title, class) for each. Cheap, ~5 ms on a typical
    /// desktop — well under the agent's per-step budget.
    /// </summary>
    public static List<WindowInfo> ListVisibleWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLengthW(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);
            GetWindowThreadProcessId(hWnd, out var pid);
            result.Add(new WindowInfo(hWnd, pid, title, cls.ToString()));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ─── Single-window lookup ──────────────────────────────────────────

    /// <summary>
    /// Returns the first visible top-level window whose title contains
    /// <paramref name="titleContains"/>. Case-insensitive substring match.
    /// </summary>
    public static WindowInfo? FindWindowByTitle(string titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains)) return null;
        var needle = titleContains;
        foreach (var w in ListVisibleWindows())
        {
            if (w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return w;
        }
        return null;
    }

    // ─── Window actions ───────────────────────────────────────────────

    public static bool FocusWindow(WindowInfo w)
    {
        ShowWindow(w.Handle, SW_RESTORE);  // unminimize first
        return SetForegroundWindow(w.Handle);
    }

    public static bool FocusWindowByTitle(string titleContains) =>
        FindWindowByTitle(titleContains) is { } w && FocusWindow(w);

    public static bool ResizeWindow(WindowInfo w, int x, int y, int width, int height) =>
        MoveWindow(w.Handle, x, y, width, height, true);

    public static bool ResizeWindowByTitle(string titleContains, int x, int y, int width, int height) =>
        FindWindowByTitle(titleContains) is { } w && ResizeWindow(w, x, y, width, height);

    public static bool CloseWindow(WindowInfo w) =>
        PostMessageW(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static bool CloseWindowByTitle(string titleContains) =>
        FindWindowByTitle(titleContains) is { } w && CloseWindow(w);

    /// <summary>
    /// Polls up to <paramref name="timeoutSeconds"/> for a window whose
    /// title contains the given substring. Returns the match or null.
    /// </summary>
    public static WindowInfo? WaitForWindow(string titleContains, bool appear, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(titleContains)) return null;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var existing = FindWindowByTitle(titleContains);
            if (appear && existing is not null) return existing;
            if (!appear && existing is null) return null;
            Thread.Sleep(150);
        }
        return null;
    }

    // ─── Process actions ──────────────────────────────────────────────

    public static bool LaunchApp(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public sealed record ProcessInfo(int Pid, string Name, string MainWindowTitle);

    public static List<ProcessInfo> ListProcesses(string? nameFilter = null)
    {
        var list = new List<ProcessInfo>();
        var procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (nameFilter is { Length: > 0 } &&
                        p.ProcessName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) == false)
                        continue;
                    list.Add(new ProcessInfo(p.Id, p.ProcessName, p.MainWindowTitle));
                }
                catch
                {
                    // skip processes we can't read
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return list;
    }

    public static int KillProcess(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var procs = Process.GetProcessesByName(n);
        var killed = 0;
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
                catch { /* ignore ones we can't kill */ }
                finally { p.Dispose(); }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return killed;
    }

    // ─── PowerShell ───────────────────────────────────────────────────

    public sealed record PowerShellResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Runs a single PowerShell -Command under powershell.exe with
    /// NoProfile + NonInteractive + a hard timeout. Returns stdout/stderr
    /// for the agent to read. Uses powershell.exe (Windows PowerShell 5.x)
    /// because that's guaranteed to be on the path; pwsh is preferred if
    /// installed.
    /// </summary>
    public static PowerShellResult RunPowerShell(string command, int timeoutMs = 30_000)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new PowerShellResult(-1, "", "empty command");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return new PowerShellResult(-1, "", "failed to start powershell.exe");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new PowerShellResult(-1, stdout, stderr + $"[timeout after {timeoutMs}ms]");
            }
            return new PowerShellResult(proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new PowerShellResult(-1, "", ex.Message);
        }
    }

    // ─── Mouse move (no click) ─────────────────────────────────────────

    public static void MoveMouse(int x, int y)
    {
        // Re-use the existing primitive — uses SetCursorPos via SendInput.
        WindowsAutomationService.MoveMouse(x, y);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Desktop interaction layer — ports of computer-use-mcp input tools
    // ════════════════════════════════════════════════════════════════════
    //
    //  Provides direct SendInput-based mouse + keyboard, clipboard, and
    //  display queries. Implemented in-process (no subprocess) so the
    //  agent's per-step budget stays tight. Coordinates use the Win32
    //  *virtual-screen* pixel space (origin top-left of the primary
    //  monitor, extending to secondary monitors), which is what the agent
    //  sees in its screenshots and what GetCursorPos reports.
    //
    //  Tool surface covered (action names in VantageACI.ExecuteAsync):
    //    • click    — left/right/middle, single/double/triple
    //    • scroll   — mouse-wheel
    //    • drag     — left/right press, move, release
    //    • type_text — Unicode via KEYEVENTF_UNICODE
    //    • press_key — single keystroke or chord (ctrl+s, win+e, return)
    //    • hold_key  — modifier-down without release (for chords)
    //    • read_clipboard / write_clipboard
    //    • frontmost_app — top-level foreground window
    //    • list_apps     — distinct visible processes
    //    • displays      — list monitors with DPI

    public enum ClickButton { Left, Right, Middle }

    public sealed record DisplayInfo(
        int Index,
        string DeviceName,
        int Width,
        int Height,
        int Dpi,
        bool Primary,
        int OriginX,
        int OriginY);

    // ─── SendInput structs and constants ───────────────────────────────

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi; // union — sized as the largest variant
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint flags, StringBuilder lpExeName, ref uint lpdwSize);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const uint MDT_EFFECTIVE_DPI = 0;

    /// <summary>
    /// Reads the on-screen rectangle (in PHYSICAL pixels) of the given
    /// window. Coordinates are screen-space, not client-space, so a
    /// maximized window's RECT covers the entire visible work area
    /// minus the taskbar. False on invalid handles.
    /// </summary>
    public static bool TryGetWindowBounds(IntPtr hwnd, out (int Left, int Top, int Right, int Bottom) bounds)
    {
        bounds = default;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        bounds = (r.Left, r.Top, r.Right, r.Bottom);
        return true;
    }

    /// <summary>Same as <see cref="TryGetWindowBounds"/> but returns a
    /// logical-pixel center coordinate (the agent reasons in logical pixels;
    /// <see cref="WindowsAutomationService.LeftClick(int,int)"/> expects them
    /// too). Pass the result directly to LeftClick / MoveMouse.</summary>
    public static bool TryGetWindowCenterLogical(IntPtr hwnd, out int cx, out int cy)
    {
        cx = cy = 0;
        if (!TryGetWindowBounds(hwnd, out var b)) return false;
        var geo = WindowsAutomationService.GetPrimaryMonitor();
        if (geo.LogicalToPhysicalScale <= 0) return false;
        double physCx = (b.Left + b.Right)  * 0.5;
        double physCy = (b.Top  + b.Bottom) * 0.5;
        // Round-to-nearest and clamp to monitor bounds.
        cx = (int)Math.Round(physCx / geo.LogicalToPhysicalScale);
        cy = (int)Math.Round(physCy / geo.LogicalToPhysicalScale);
        cx = Math.Clamp(cx, 0, geo.LogicalWidth  - 1);
        cy = Math.Clamp(cy, 0, geo.LogicalHeight - 1);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    // ─── Virtual-screen helpers ────────────────────────────────────────

    private static (int Width, int Height, int OriginX, int OriginY) VirtualScreen()
    {
        // Use GetSystemMetrics for SM_CXVIRTUALSCREEN etc.
        // P/Invoke wrapped inline to keep this file self-contained.
        var w = (int)NativeGetSys(SM_CXVIRTUALSCREEN);
        var h = (int)NativeGetSys(SM_CYVIRTUALSCREEN);
        var x = (int)NativeGetSys(SM_XVIRTUALSCREEN);
        var y = (int)NativeGetSys(SM_YVIRTUALSCREEN);
        if (w <= 0 || h <= 0) { w = (int)NativeGetSys(SM_CXSCREEN); h = (int)NativeGetSys(SM_CYSCREEN); x = 0; y = 0; }
        return (w, h, x, y);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private static uint NativeGetSys(int idx) => (uint)GetSystemMetrics(idx);

    private static (int abs, int ord) ToAbsolute(int pixel, int axisSize)
    {
        if (axisSize <= 0) return (0, 0);
        // MOUSEEVENTF_ABSOLUTE maps the virtual screen to [0, 65535]
        var abs = (int)Math.Round(65535.0 * pixel / Math.Max(axisSize - 1, 1));
        if (abs < 0) abs = 0;
        if (abs > 65535) abs = 65535;
        return (abs, pixel);
    }

    // ─── Mouse — clicks / scroll / drag ────────────────────────────────

    /// <summary>
    /// Click at logical (x, y). Supports left / right / middle and
    /// single / double / triple. Coordinates are pixel coordinates in
    /// the virtual-screen space (what the agent sees on a screenshot).
    /// </summary>
    public static bool Click(int x, int y, ClickButton button = ClickButton.Left, int count = 1)
    {
        if (count < 1 || count > 3) count = 1;
        var (vw, vh, ox, oy) = VirtualScreen();
        var (ax, _) = ToAbsolute(x, vw);
        var (ay, _) = ToAbsolute(y, vh);
        uint downFlag, upFlag;
        switch (button)
        {
            case ClickButton.Right: downFlag = MOUSEEVENTF_RIGHTDOWN; upFlag = MOUSEEVENTF_RIGHTUP; break;
            case ClickButton.Middle: downFlag = MOUSEEVENTF_MIDDLEDOWN; upFlag = MOUSEEVENTF_MIDDLEUP; break;
            default: downFlag = MOUSEEVENTF_LEFTDOWN; upFlag = MOUSEEVENTF_LEFTUP; break;
        }
        var ok = true;
        for (int i = 0; i < count; i++)
        {
            // Move + first DOWN
            var inputs = new[]
            {
                MakeMouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE),
                MakeMouse(ax, ay, downFlag),
                MakeMouse(ax, ay, upFlag),
            };
            if (i < count - 1)
            {
                // brief pause between click halves
                Thread.Sleep(60);
                inputs = new[]
                {
                    MakeMouse(ax, ay, downFlag),
                    MakeMouse(ax, ay, upFlag),
                };
            }
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length) ok = false;
            // Gap between full click repetitions for double/triple
            if (i < count - 1) Thread.Sleep(110);
        }
        return ok;
    }

    private static INPUT MakeMouse(int absX, int absY, uint flags, uint wheel = 0)
    {
        return new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = absX,
                dy = absY,
                mouseData = wheel,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        };
    }

    /// <summary>
    /// Mouse-wheel scroll at (x, y). Positive delta scrolls up, negative
    /// scrolls down. One WHEEL_DELTA (120) = one notch.
    /// </summary>
    public static bool Scroll(int x, int y, int delta)
    {
        var (vw, vh, _, _) = VirtualScreen();
        var (ax, _) = ToAbsolute(x, vw);
        var (ay, _) = ToAbsolute(y, vh);
        var inputs = new[]
        {
            MakeMouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 0u),
            MakeMouse(ax, ay, MOUSEEVENTF_WHEEL, (uint)delta),
        };
        uint sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        return sent == 2;
    }

    /// <summary>
    /// Drag from (fromX, fromY) to (toX, toY) with the given button held.
    /// Uses SetCursorPos to walk the cursor (so we don't need a multi-step
    /// SendInput move), then a pair of down/up events.
    /// </summary>
    public static bool Drag(int fromX, int fromY, int toX, int toY, ClickButton button = ClickButton.Left)
    {
        SetCursorPos(fromX, fromY);
        Thread.Sleep(20);
        uint downFlag, upFlag;
        switch (button)
        {
            case ClickButton.Right: downFlag = MOUSEEVENTF_RIGHTDOWN; upFlag = MOUSEEVENTF_RIGHTUP; break;
            case ClickButton.Middle: downFlag = MOUSEEVENTF_MIDDLEDOWN; upFlag = MOUSEEVENTF_MIDDLEUP; break;
            default: downFlag = MOUSEEVENTF_LEFTDOWN; upFlag = MOUSEEVENTF_LEFTUP; break;
        }
        var inputs = new[]
        {
            MakeMouse(0, 0, downFlag),
            MakeMouse(0, 0, upFlag),
        };
        var sentDown = SendInput(1, new[] { MakeMouse(0, 0, downFlag) }, Marshal.SizeOf<INPUT>());
        SetCursorPos(toX, toY);
        Thread.Sleep(40);
        var sentUp = SendInput(1, new[] { MakeMouse(0, 0, upFlag) }, Marshal.SizeOf<INPUT>());
        return sentDown == 1 && sentUp == 1;
    }

    /// <summary>Read the current cursor position in virtual-screen pixels.</summary>
    public static (int X, int Y) GetCursorPosition()
    {
        if (GetCursorPos(out var pt)) return (pt.x, pt.y);
        return (0, 0);
    }

    // ─── Keyboard — type / press / hold ───────────────────────────────

    private static readonly Dictionary<string, ushort> VkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Modifiers
        ["shift"] = 0x10, ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12,
        ["lwin"] = 0x5B, ["rwin"] = 0x5C, ["win"] = 0x5B, ["meta"] = 0x5B,
        ["caps"] = 0x14, ["capslock"] = 0x14,
        // Whitespace / editing
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09, ["space"] = 0x20, ["spacebar"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        // Arrows
        ["left"] = 0x25, ["right"] = 0x27, ["up"] = 0x26, ["down"] = 0x28,
        // Locks + special
        ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["print"] = 0x2A, ["printscreen"] = 0x2C,
        ["pause"] = 0x13, ["break"] = 0x13, ["menu"] = 0x5D, ["apps"] = 0x5D,
        // Function keys
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["f13"] = 0x7C, ["f14"] = 0x7D, ["f15"] = 0x7E, ["f16"] = 0x7F,
        ["f17"] = 0x80, ["f18"] = 0x81, ["f19"] = 0x82, ["f20"] = 0x83,
        ["f21"] = 0x84, ["f22"] = 0x85, ["f23"] = 0x86, ["f24"] = 0x87,
        // OEM / top-row symbols on a US keyboard
        ["minus"] = 0xBD, ["equals"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["backslash"] = 0xDC, ["semicolon"] = 0xBA,
        ["quote"] = 0xDE, ["apostrophe"] = 0xDE,
        ["bracketleft"] = 0xDB, ["bracketright"] = 0xDD,
        ["grave"] = 0xC0, ["tilde"] = 0xC0,
    };

    /// <summary>
    /// Convert a token from the { name | + VK } form into a virtual-key
    /// code. Letters and digits come through unchanged (uppercase).
    /// </summary>
    private static ushort? TokenToVk(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim();
        if (VkMap.TryGetValue(token, out var vk)) return vk;
        if (token.Length == 1)
        {
            char c = token[0];
            if (char.IsLetterOrDigit(c)) return (ushort)char.ToUpperInvariant(c);
            return null;
        }
        if (token.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(token.AsSpan(3), System.Globalization.NumberStyles.HexNumber, null, out var vkHex))
            return vkHex;
        return null;
    }

    /// <summary>
    /// Type the given text one character at a time using
    /// KEYEVENTF_UNICODE — handles any Unicode codepoint, not just ASCII.
    /// </summary>
    public static int TypeText(string text, int perCharDelayMs = 0)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var inputs = new INPUT[text.Length * 2];
        int n = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            WriteKbd(inputs, n++, wVk: 0, wScan: (ushort)rune.Value, flags: KEYEVENTF_UNICODE);
            WriteKbd(inputs, n++, wVk: 0, wScan: (ushort)rune.Value, flags: KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
        uint sent = SendInput((uint)n, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)n) return 0;
        if (perCharDelayMs > 0) Thread.Sleep(perCharDelayMs * text.Length);
        return n / 2;
    }

    private static void WriteKbd(INPUT[] arr, int idx, ushort wVk, ushort wScan, uint flags)
    {
        arr[idx] = new INPUT
        {
            type = INPUT_KEYBOARD,
            mi = new MOUSEINPUT
            {
                dx = wVk,
                dy = wScan,
                mouseData = flags,
                dwFlags = 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        };
    }

    /// <summary>
    /// Press a chord like "ctrl+s" / "ctrl+shift+alt+escape" / "win+e" /
    /// "Return" / "F5". Modifiers go down in order, the main key goes
    /// down then up, then modifiers release in reverse order.
    /// </summary>
    public static bool PressKey(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return false;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        var vks = new ushort[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var v = TokenToVk(parts[i]);
            if (v is null) return false;
            vks[i] = v.Value;
        }
        var inputs = new List<INPUT>(vks.Length * 2 + 2);
        for (int i = 0; i < vks.Length - 1; i++)
            inputs.Add(MakeKey(vks[i], false));
        inputs.Add(MakeKey(vks[^1], false));
        inputs.Add(MakeKey(vks[^1], true));
        for (int i = vks.Length - 2; i >= 0; i--)
            inputs.Add(MakeKey(vks[i], true));
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == (uint)inputs.Count;
    }

    /// <summary>Hold a virtual key down (for multi-step chord assembly). Call KeyUp to release.</summary>
    public static bool KeyDown(string key) => SendOneKey(key, up: false);

    /// <summary>Release a previously held virtual key.</summary>
    public static bool KeyUp(string key) => SendOneKey(key, up: true);

    private static bool SendOneKey(string key, bool up)
    {
        var v = TokenToVk(key);
        if (v is null) return false;
        var input = MakeKey(v.Value, up);
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        return sent == 1;
    }

    private static INPUT MakeKey(ushort vk, bool up)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            mi = new MOUSEINPUT
            {
                dx = vk,
                dy = 0,
                mouseData = up ? KEYEVENTF_KEYUP : 0u,
                dwFlags = 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        };
    }

    public static bool IsKeyDown(ushort vk) => (GetKeyState(vk) & 0x8000) != 0;

    // ─── Clipboard ────────────────────────────────────────────────────

    /// <summary>
    /// Read the current clipboard contents as UTF-16 text. Returns null
    /// if the clipboard is empty or holds a non-text format. Owns all
    /// clipboard handles internally so callers don't need to dispose
    /// anything.
    /// </summary>
    public static string? ReadClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            var hData = GetClipboardData(CF_UNICODETEXT);
            if (hData == IntPtr.Zero) return null;
            var ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(hData); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// Replace the clipboard contents with the given UTF-16 text.
    /// </summary>
    public static bool WriteClipboard(string text)
    {
        if (text is null) return false;
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (uint)((text.Length + 1) * 2);
            var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hGlobal == IntPtr.Zero) return false;
            var dest = GlobalLock(hGlobal);
            if (dest == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            try { Marshal.Copy(System.Text.Encoding.Unicode.GetBytes(text + '\0'), 0, dest, (int)bytes); }
            finally { GlobalUnlock(hGlobal); }
            var hSet = SetClipboardData(CF_UNICODETEXT, hGlobal);
            // If SetClipboardData succeeded, the system owns the handle.
            if (hSet == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    // ─── Foreground / running apps ─────────────────────────────────────

    /// <summary>
    /// Returns the foreground window plus its owning process name and pid.
    /// Useful as a one-shot "which app am I on now" probe for the agent.
    /// </summary>
    public static (WindowInfo Window, string ProcessName)? GetFrontmostApp()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var len = GetWindowTextLengthW(hwnd);
        if (len == 0) return null;
        var title = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, title, title.Capacity);
        var cls = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        var procName = TryGetProcessName(pid) ?? "unknown";
        return (new WindowInfo(hwnd, pid, title.ToString(), cls.ToString()), procName);
    }

    private static string? TryGetProcessName(uint pid)
    {
        try
        {
            var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return null;
            try
            {
                var buf = new StringBuilder(1024);
                var size = (uint)buf.Capacity;
                if (QueryFullProcessImageNameW(hProc, 0, buf, ref size))
                {
                    var fullPath = buf.ToString();
                    return System.IO.Path.GetFileNameWithoutExtension(fullPath);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(hProc); }
        }
        catch { return null; }
    }

    private static readonly HashSet<uint> _seenFrontmostPids = new();
    /// <summary>
    /// Returns one entry per process that owns at least one visible
    /// top-level window. Sorted by process name for stable output.
    /// </summary>
    public static List<(int Pid, string Name, string Title, IntPtr Hwnd)> ListRunningApps()
    {
        var map = new Dictionary<int, (string Name, string Title, IntPtr Hwnd)>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            // de-dup to first-seen window per pid
            if (map.ContainsKey((int)pid)) return true;
            var len = GetWindowTextLengthW(hWnd);
            if (len == 0) return true;
            var title = new StringBuilder(len + 1);
            GetWindowTextW(hWnd, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return true;
            var name = TryGetProcessName(pid) ?? "<unknown>";
            map[(int)pid] = (name, title.ToString(), hWnd);
            return true;
        }, IntPtr.Zero);
        return map.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Title, kv.Value.Hwnd))
                 .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                 .ToList();
    }

    // ─── Displays ──────────────────────────────────────────────────────

    public static List<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        var primaryW = (int)NativeGetSys(SM_CXSCREEN);
        bool Callback(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(hMon, ref mi)) return true;
            GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out var dpi, out _);
            var rect = mi.rcMonitor;
            var isPrimary = mi.dwFlags != 0; // MONITORINFOF_PRIMARY
            list.Add(new DisplayInfo(
                Index: list.Count,
                DeviceName: mi.szDevice,
                Width: rect.Right - rect.Left,
                Height: rect.Bottom - rect.Top,
                Dpi: (int)dpi,
                Primary: isPrimary,
                OriginX: rect.Left,
                OriginY: rect.Top));
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return list;
    }
}
