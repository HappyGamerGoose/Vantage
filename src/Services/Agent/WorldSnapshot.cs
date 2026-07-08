// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/WorldSnapshot.cs
//
// Deterministic state capture used by the verification loop. Unlike a
// screenshot, which is opaque to the verifier and stretches vision-tokens,
// a WorldSnapshot is named + addressable: foreground window + process,
// cursor position, visible window count, displays. Lets the system verify
// "the action landed" with ground-truth state, not by asking the model to
// look at a delta image.

using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Vantage.Services;

namespace Vantage.Services.Agent;

public sealed record WorldSnapshot(
    DateTimeOffset CapturedAt,
    long ElapsedMs,
    string ForegroundTitle,
    string ForegroundProcess,
    int ForegroundPid,
    long ForegroundHwnd,
    int CursorX,
    int CursorY,
    int DisplayCount,
    string DisplaySummary,
    int VisibleWindowCount,
    int RunningAppCount,
    string? ClipboardText,
    string Fingerprint)
{
    /// <summary>
    /// Capture a snapshot of the current desktop. The six Win32 queries
    /// (foreground / cursor / displays / visible-windows / running-apps /
    /// clipboard) are I/O-bound and independent — fire them in parallel
    /// via Task.Run, then join. On a typical desktop this collapses
    /// ~30-50 ms of wall clock into the cost of the slowest call
    /// (~5-12 ms).
    /// </summary>
    public static WorldSnapshot Capture()
    {
        var sw = Stopwatch.StartNew();

        // Kick all the I/O queries concurrently. Each lambda runs on a
        // thread-pool worker (Task.Run), so the marshalling cost is
        // bounded by the slowest call rather than the sum.
        var tFrontmost   = Task.Run(() => WindowsAppManager.GetFrontmostApp());
        var tCursor      = Task.Run(() => WindowsAppManager.GetCursorPosition());
        var tDisplays    = Task.Run(() => WindowsAppManager.GetDisplays());
        var tWindows     = Task.Run(() => WindowsAppManager.ListVisibleWindows());
        var tApps        = Task.Run(() => WindowsAppManager.ListRunningApps());
        var tClipboard   = Task.Run(SafeReadClipboard);

        // Task.WaitAll is the cheapest synchronisation primitive when you
        // don't need the awaitable pattern — it blocks the calling thread
        // until every task finishes, then inlines without allocating a
        // continuation. Task.WhenAll(...).GetAwaiter().GetResult() does the
        // same thing but routes through an extra Task allocation + an
        // AsyncStateMachineBox that we don't need here.
        Task.WaitAll(tFrontmost, tCursor, tDisplays, tWindows, tApps, tClipboard);
        sw.Stop();

        var frontmost = tFrontmost.Result;
        var cursor = tCursor.Result;
        var displays = tDisplays.Result;
        var windows = tWindows.Result;
        var apps = tApps.Result;
        var clip = tClipboard.Result;

        var displaySummary = displays.Count == 1
            ? $"{displays[0].Width}x{displays[0].Height}"
            : string.Join("+", displays.Select(d => $"{d.Width}x{d.Height}"));

        return new WorldSnapshot(
            DateTimeOffset.Now,
            sw.ElapsedMilliseconds,
            frontmost?.Window.Title ?? "",
            frontmost?.ProcessName ?? "",
            frontmost is { } f ? (int)f.Window.Pid : 0,
            frontmost?.Window.Handle.ToInt64() ?? 0,
            cursor.X,
            cursor.Y,
            displays.Count,
            displaySummary,
            windows.Count,
            apps.Count,
            clip,
            ComputeFingerprint(frontmost, cursor, windows));
    }

    public string Compact() =>
        $"fg='{ForegroundTitle}' proc={ForegroundProcess} cursor=({CursorX},{CursorY}) " +
        $"wins={VisibleWindowCount} apps={RunningAppCount} clip_len={ClipboardText?.Length ?? 0}";

    private static string ComputeFingerprint(
        (WindowsAppManager.WindowInfo Window, string ProcessName)? frontmost,
        (int X, int Y) cursor,
        List<WindowsAppManager.WindowInfo> windows)
    {
        // Compose a 64-bit-ish fingerprint of (foreground hwnd, cursor grid, window titles).
        // Used to detect "no observable change happened between two snapshots"
        // without doing full pixel work.
        var sb = new System.Text.StringBuilder(64);
        sb.Append(frontmost?.Window.Handle.ToInt64() ?? 0).Append('|');
        sb.Append(cursor.X / 8).Append(',').Append(cursor.Y / 8).Append('|');
        // Hash first 6 window titles (sorted by PID stability)
        var titles = windows
            .Where(w => !string.IsNullOrEmpty(w.Title))
            .OrderBy(w => w.Pid)
            .ThenBy(w => w.Title, StringComparer.Ordinal)
            .Take(6)
            .Select(w => w.Title);
        foreach (var t in titles) sb.Append(t).Append(';');
        return sb.ToString();
    }

    private static string? SafeReadClipboard()
    {
        try
        {
            return WindowsAppManager.ReadClipboard();
        }
        catch
        {
            return null; // clipboard may be locked by another app at any moment
        }
    }
}
