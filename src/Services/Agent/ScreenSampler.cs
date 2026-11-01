// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ScreenSampler.cs
//
// Real pixel comparison for "did the action land?" and "has the screen
// finished changing?". Comparing encoded JPEG bytes cannot answer either: any
// small redraw changes the file size, so a hover highlight read as a total
// change, ended the wait early and left the next screenshot mid-transition.
// These frames are small grayscale copies of the screen, cheap enough to poll.

using System.Runtime.InteropServices;

namespace Vantage.Services.Agent;

internal sealed record ScreenFrame(byte[] Gray, int Width, int Height, int LogicalWidth, int LogicalHeight);

/// <summary>Share of pixels that differ across the screen and around a point.</summary>
internal readonly record struct FrameDelta(double Global, double Local)
{
    // About 0.08% of the screen is a tooltip or a small repaint; a clock tick
    // and a blinking caret stay below it. Around the pointer a toggle or a
    // focus ring is enough, however small it is against the whole screen.
    public bool IsChange => Global >= 0.0008 || Local >= 0.015;

    public bool IsMotion => Global >= 0.0004;
}

internal static class ScreenSampler
{
    private const int MaxFrameWidth = 960;
    private const int PixelTolerance = 14;
    private const int LocalRadiusLogical = 70;

    public static ScreenFrame? Capture()
    {
        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            var geo = WindowsAutomationService.GetPrimaryMonitor();
            if (geo.PhysicalWidth <= 0 || geo.PhysicalHeight <= 0) return null;
            var width = Math.Min(MaxFrameWidth, geo.PhysicalWidth);
            var height = Math.Max(1, (int)Math.Round(width * (double)geo.PhysicalHeight / geo.PhysicalWidth));

            screen = GetDC(IntPtr.Zero);
            memory = CreateCompatibleDC(screen);
            var info = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
            };
            bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, Halftone);
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memory, 0, 0, width, height, screen, 0, 0, geo.PhysicalWidth, geo.PhysicalHeight, SrcCopy))
                return null;

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var gray = new byte[width * height];
            for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
                gray[i] = (byte)((pixels[p] + (pixels[p + 1] * 5) + (pixels[p + 2] * 2)) >> 3);
            return new ScreenFrame(gray, width, height, geo.LogicalWidth, geo.LogicalHeight);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static FrameDelta Compare(ScreenFrame before, ScreenFrame after, int? pointerX = null, int? pointerY = null)
    {
        if (before.Width != after.Width || before.Height != after.Height) return new FrameDelta(1, 1);

        var width = before.Width;
        int left = -1, right = -1, top = -1, bottom = -1;
        if (pointerX is { } px && pointerY is { } py && before.LogicalWidth > 0 && before.LogicalHeight > 0)
        {
            var scale = (double)width / before.LogicalWidth;
            var radius = Math.Max(4, (int)(LocalRadiusLogical * scale));
            var cx = (int)(px * scale);
            var cy = (int)(py * (double)before.Height / before.LogicalHeight);
            left = Math.Max(0, cx - radius);
            right = Math.Min(width - 1, cx + radius);
            top = Math.Max(0, cy - radius);
            bottom = Math.Min(before.Height - 1, cy + radius);
        }

        int changed = 0, localChanged = 0, localTotal = 0;
        var a = before.Gray;
        var b = after.Gray;
        for (var y = 0; y < before.Height; y++)
        {
            var row = y * width;
            var inLocalRow = top >= 0 && y >= top && y <= bottom;
            for (var x = 0; x < width; x++)
            {
                var different = Math.Abs(a[row + x] - b[row + x]) > PixelTolerance;
                if (different) changed++;
                if (inLocalRow && x >= left && x <= right)
                {
                    localTotal++;
                    if (different) localChanged++;
                }
            }
        }

        var total = a.Length;
        return new FrameDelta(
            total > 0 ? (double)changed / total : 0,
            localTotal > 0 ? (double)localChanged / localTotal : 0);
    }

    /// <summary>
    /// Bridges to the verifier, whose thresholds were tuned against the old
    /// all-or-nothing byte comparison: a real change clears every one of them.
    /// </summary>
    public static ScreenshotDiff ToScreenshotDiff(FrameDelta delta)
    {
        var ratio = delta.IsChange ? Math.Max(0.02, delta.Global) : delta.Global;
        return new ScreenshotDiff(
            ratio, 0, 0, ratio,
            $"pixels changed: {delta.Global:P2} of screen, {delta.Local:P1} around the pointer",
            TimeSpan.Zero);
    }

    /// <summary>
    /// Polls until the action's effect has finished drawing, or until it is
    /// clear nothing is going to change. Returns the last frame seen.
    /// </summary>
    public static async Task<ScreenFrame?> SettleAsync(
        ScreenFrame? baseline,
        int noChangeBudgetMs,
        Func<(int X, int Y)?> pointer,
        CancellationToken ct) =>
        await SettleAsync(baseline, new SettlePlan(noChangeBudgetMs), pointer, null, ct);

    public static async Task<ScreenFrame?> SettleAsync(
        ScreenFrame? baseline,
        SettlePlan plan,
        Func<(int X, int Y)?> pointer,
        string? titleBefore,
        CancellationToken ct)
    {
        if (baseline is null) return null;

        var tracker = new SettleTracker(plan);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var previous = baseline;
        var latest = baseline;
        while (true)
        {
            await Task.Delay(SettleTracker.PollIntervalMs, ct);
            var frame = await Task.Run(Capture, ct);
            if (frame is null) return latest;

            var target = pointer();
            var fromBase = Compare(baseline, frame, target?.X, target?.Y);
            var fromPrevious = Compare(previous, frame);
            latest = frame;
            previous = frame;
            var titleChanged = plan.AwaitTitleMs > 0 && titleBefore is not null
                && !string.Equals(ForegroundWindowProbe.Read().Title, titleBefore, StringComparison.Ordinal);
            if (tracker.IsDone(fromBase, fromPrevious, watch.ElapsedMilliseconds, titleChanged)) return latest;
        }
    }

    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
}

internal readonly record struct ForegroundWindowInfo(string Title, string ProcessName)
{
    public bool IsBrowser => BrowserPolicy.IsBrowserProcess(ProcessName);
}

internal static class ForegroundWindowProbe
{
    public static ForegroundWindowInfo Read()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return default;
            var buffer = new System.Text.StringBuilder(512);
            GetWindowText(handle, buffer, buffer.Capacity);
            GetWindowThreadProcessId(handle, out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return new ForegroundWindowInfo(buffer.ToString(), process.ProcessName);
        }
        catch
        {
            return default;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

internal static class BrowserPolicy
{
    private static readonly string[] BrowserProcesses = { "msedge", "chrome", "firefox", "brave", "opera", "vivaldi" };
    private static readonly string[] CurrentTabPhrases = { "this tab", "current tab", "same tab", "existing tab", "open tab" };

    public static bool IsBrowserProcess(string? process) =>
        process is not null && BrowserProcesses.Contains(process, StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's open tabs are their work, unless the task is about one of them.</summary>
    public static bool GoalAllowsCurrentTab(string? goal) =>
        goal is not null && CurrentTabPhrases.Any(phrase => goal.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    public static bool IsBlankTab(string? windowTitle) =>
        windowTitle is not null
        && (windowTitle.StartsWith("new tab", StringComparison.OrdinalIgnoreCase)
            || windowTitle.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase));

    public static bool IsAddressBar(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var text = UiControlGrounding.Normalize(target);
        return text.Contains("address and search", StringComparison.Ordinal)
            || text.Contains("address bar", StringComparison.Ordinal)
            || text.Contains("url bar", StringComparison.Ordinal)
            || text.Contains("location bar", StringComparison.Ordinal)
            || text.Contains("omnibox", StringComparison.Ordinal);
    }

    public static bool IsAddressBarShortcut(string? chord)
    {
        var parts = (chord ?? string.Empty).ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("control", "ctrl")
            .Split('+', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => parts[0] == "f6",
            2 => (parts.Contains("ctrl") && parts.Contains("l")) || (parts.Contains("alt") && parts.Contains("d")),
            _ => false,
        };
    }

    public static bool IsNewTabShortcut(string? chord)
    {
        var parts = (chord ?? string.Empty).ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("control", "ctrl")
            .Split('+', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts.Contains("ctrl") && parts.Contains("t");
    }
}

internal static class DonePolicy
{
    /// <summary>
    /// The model answers from a screenshot taken before its call, which can be
    /// many seconds old. If the screen has moved on since then, the `done` is
    /// about a screen that no longer exists.
    /// </summary>
    public static bool ScreenMovedSinceLook(FrameDelta sinceScreenshot) => sinceScreenshot.Global >= 0.01;
}

/// <summary>
/// Decides when an action's effect has finished. Nothing changed: give the UI
/// a budget to react, which only a true no-op pays in full. Something changed:
/// wait for the screen to go still, so the next screenshot is never taken
/// mid-transition, with a cap for animations that never stop.
/// </summary>
internal sealed class SettleTracker
{
    public const int PollIntervalMs = 40;

    private readonly SettlePlan _plan;
    private long _lastMotionMs;
    private bool _titleChanged;

    public SettleTracker(int noChangeBudgetMs) : this(new SettlePlan(noChangeBudgetMs))
    {
    }

    public SettleTracker(SettlePlan plan) => _plan = plan;

    public bool Changed { get; private set; }

    public bool IsDone(FrameDelta fromBaseline, FrameDelta fromPrevious, long elapsedMs, bool titleChanged = false)
    {
        if (!Changed && fromBaseline.IsChange)
        {
            Changed = true;
            _lastMotionMs = elapsedMs;
        }
        if (titleChanged && !_titleChanged)
        {
            _titleChanged = true;
            Changed = true;
            _lastMotionMs = elapsedMs;
        }
        if (fromPrevious.IsMotion) _lastMotionMs = elapsedMs;

        // A page that is still fetching looks perfectly still: the typed text
        // has been drawn and nothing else moves yet. Until the title changes,
        // stillness proves nothing.
        if (_plan.AwaitTitleMs > 0 && !_titleChanged && elapsedMs < _plan.AwaitTitleMs) return false;

        if (!Changed) return elapsedMs >= _plan.NoChangeBudgetMs;
        return elapsedMs - _lastMotionMs >= _plan.QuietMs || elapsedMs >= _plan.MaxMs;
    }
}

/// <summary>How to decide that an action's effect has finished.</summary>
internal sealed record SettlePlan(int NoChangeBudgetMs, int QuietMs = 220, int MaxMs = 2500, int AwaitTitleMs = 0);

internal static class SettlePolicy
{
    private const int PageQuietMs = 600;
    private const int NavigationMaxMs = 4000;
    private const int AwaitTitleMs = 2500;

    public static SettlePlan Plan(AgentAction action, bool browserInFront)
    {
        var budget = NoChangeBudgetMs(action);
        if (budget == 0) return new SettlePlan(0);
        if (browserInFront && SubmitsSomething(action))
            return new SettlePlan(Math.Max(budget, AwaitTitleMs), PageQuietMs, NavigationMaxMs, AwaitTitleMs);
        if (action.Action.Trim().ToLowerInvariant() == "launch_app" && OpensUrl(action))
            return new SettlePlan(Math.Max(budget, AwaitTitleMs), PageQuietMs, NavigationMaxMs, AwaitTitleMs);
        // A link click draws its hover at once and only then starts loading.
        if (browserInFront && action.Action.Trim().ToLowerInvariant() is "click" or "click_xy" or "click_element")
            return new SettlePlan(budget, PageQuietMs);
        return new SettlePlan(budget);
    }

    internal static bool OpensUrl(AgentAction action)
    {
        var target = action.GetString("executable")?.TrimStart() ?? string.Empty;
        return target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the action ends by pressing Enter, which is what starts a page load.</summary>
    internal static bool SubmitsSomething(AgentAction action)
    {
        switch (action.Action.Trim().ToLowerInvariant())
        {
            case "batch":
                return action.GetActionList("steps").Any(SubmitsSomething);
            case "type":
                return action.GetBool("enter", false);
            case "press_key" or "press_window_key":
                return IsEnter(action.GetString("combo"));
            case "key":
                return action.GetStringList("keys") is { Count: 1 } keys && IsEnter(keys[0]);
            default:
                return false;
        }
    }

    private static bool IsEnter(string? key) =>
        key?.Trim().ToLowerInvariant() is "enter" or "return";

    /// <summary>
    /// How long to wait for a first visible reaction, by action. Zero means
    /// the action cannot change the screen, so no frames are taken at all.
    /// </summary>
    public static int NoChangeBudgetMs(AgentAction action) => action.Action.Trim().ToLowerInvariant() switch
    {
        "done" or "fail" or "wait" or "screenshot" or "move_mouse" or "list_windows" or "get_window_state" or "read_text"
            or "list_apps" or "list_processes" or "frontmost_app" or "displays" or "cursor_position"
            or "vantage_get_clipboard" or "vantage_set_clipboard" or "save_to_knowledge" or "wait_for_app" => 0,
        "type" or "type_text" or "type_window_text" or "set_value" => 350,
        "run_powershell" => 350,
        "launch_app" or "focus_app" or "activate_window" or "close_app" or "resize_app" or "kill_process" => 1500,
        "key" or "press_key" or "press_window_key" => 1200,
        "batch" => 1200,
        _ => 900,
    };
}
