// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/WorldStateProbe.cs
//
// Rich per-step OS context for the agent. Distinct from WorldSnapshot
// (which the verifier uses) — this is a SHAPED TEXT BLOCK that gets
// appended to the generator's prompt so the model has a structured
// "where am I and what's around me" view WITHOUT it having to spend
// an LLM turn enumerating apps or asking the user about their machine.
//
// All probes here are local — no LLM, no API calls, no subprocess
// outside the process tree of standard system tools. Total wall-clock
// budget on a typical desktop: 5-25 ms.

using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using Vantage.Services;

namespace Vantage.Services.Agent;

public sealed record WorldStateProbe(
    DateTimeOffset CapturedAt,
    long ElapsedMs,
    string ForegroundTitle,
    string ForegroundProcess,
    long ForegroundPid,
    long ForegroundHwnd,
    int CursorX,
    int CursorY,
    int DisplayCount,
    string DisplaySummary,
    IReadOnlyList<string> TopVisibleWindows,
    string? ClipboardText,
    string? BrowserActiveTabHint,
    string? BatteryState,
    IReadOnlyList<string> RecentFiles,
    int InstalledAppCount,
    string InstalledAppSummary,
    string? OsVersion)
{
    // ── Cached session fields ──────────────────────────────────
    private static string? _cachedOsVersion;
    private static int    _cachedInstalledAppCount;
    private static string _cachedInstalledAppSummary = "";

    public static void PrimeSessionCache()
    {
        if (_cachedOsVersion is not null) return;
        try { _cachedOsVersion = ProbeOsVersion(); } catch { _cachedOsVersion = "Windows (unknown version)"; }
        try
        {
            var (count, summary) = ProbeInstalledApps();
            _cachedInstalledAppCount  = count;
            _cachedInstalledAppSummary = summary;
        }
        catch
        {
            _cachedInstalledAppCount  = 0;
            _cachedInstalledAppSummary = "(installed-apps probe unavailable)";
        }
    }

    /// <summary>
    /// Capture the volatile half of the world state. Cheap: 5-25 ms wall clock.
    /// Failures in any single probe degrade gracefully (field becomes null / empty)
    /// so a flaky WMI call can't tank the agent loop.
    /// </summary>
    public static WorldStateProbe Capture()
    {
        var sw = Stopwatch.StartNew();

        // Kick every probe as a Task.Run so the marshalling cost is bounded
        // by the slowest call rather than the sum. Each lambda tolerates
        // throwing internally — Capture returns defaults for any failed probe.
        var tFg      = Task.Run(SafeFrontmost);
        var tCursor  = Task.Run(SafeCursor);
        var tDisp    = Task.Run(SafeDisplays);
        var tWins    = Task.Run(SafeWindows);
        var tClip    = Task.Run(SafeReadClipboard);
        var tBattery = Task.Run(SafeProbeBattery);
        var tRecent  = Task.Run(SafeProbeRecentFiles);

        // We don't Task.WhenAll + GetAwaiter().GetResult() because that
        // would deadlock; instead we sleep until the slowest task finishes
        // via WaitAll on the thread pool. .NET's thread pool does not run
        // continuations on already-blocking threads.
        Task.WaitAll(new Task[] { tFg, tCursor, tDisp, tWins, tClip, tBattery, tRecent });
        sw.Stop();

        var fg       = tFg.IsCompletedSuccessfully   ? tFg.Result   : default;
        var cursor   = tCursor.IsCompletedSuccessfully ? tCursor.Result : ((int X, int Y))(0, 0);
        var displays = tDisp.IsCompletedSuccessfully   ? tDisp.Result  : new List<WindowsAppManager.DisplayInfo>();
        var windows  = tWins.IsCompletedSuccessfully   ? tWins.Result  : new List<WindowsAppManager.WindowInfo>();
        var clip     = tClip.IsCompletedSuccessfully   ? tClip.Result  : null;
        var battery  = tBattery.IsCompletedSuccessfully ? tBattery.Result : null;
        var recent   = tRecent.IsCompletedSuccessfully ? tRecent.Result : new List<string>();

        var dispSummary = displays.Count == 0
            ? "?"
            : displays.Count == 1
                ? $"{displays[0].Width}x{displays[0].Height}"
                : string.Join("+", displays.Select(d => $"{d.Width}x{d.Height}")) +
                  $" ({displays.Count} monitors)";

        var topWindows = windows
            .OrderBy(w => fg is { } f2 && f2.Window.Handle == w.Handle ? 0 :
                          fg is { } f3 && f3.Window.Pid   == w.Pid    ? 1 : 2)
            .ThenBy(w => w.Handle.ToInt64())
            .Take(10)
            .Select(w => $"{w.ClassName} pid={w.Pid} \"{Truncate(w.Title, 64)}\"")
            .ToList();

        // Browser active-tab heuristic: if foreground process is a known
        // browser, browsers put the active tab title into the window title.
        // Surface the suffix when present ("… — Site Name" / "… - Site Name").
        string? browserHint = null;
        if (fg is { } fgv && !string.IsNullOrWhiteSpace(fgv.Window.Title))
        {
            var proc = (fgv.ProcessName ?? "").ToLowerInvariant();
            bool isBrowser = proc is "msedge" or "chrome" or "firefox" or "brave" or "opera" or "vivaldi";
            if (isBrowser)
            {
                var title = fgv.Window.Title;
                var sepIdx = title.LastIndexOf(" — ");
                if (sepIdx < 0) sepIdx = title.LastIndexOf(" - ");
                browserHint = sepIdx > 0 && sepIdx < title.Length - 2
                    ? title.Substring(sepIdx + 3).Trim()
                    : title;
            }
        }

        return new WorldStateProbe(
            CapturedAt: DateTimeOffset.Now,
            ElapsedMs: sw.ElapsedMilliseconds,
            ForegroundTitle: fg?.Window.Title ?? "",
            ForegroundProcess: fg?.ProcessName ?? "",
            ForegroundPid: fg is { } f4 ? f4.Window.Pid : 0,
            ForegroundHwnd: fg?.Window.Handle.ToInt64() ?? 0,
            CursorX: cursor.X,
            CursorY: cursor.Y,
            DisplayCount: displays.Count,
            DisplaySummary: dispSummary,
            TopVisibleWindows: topWindows,
            ClipboardText: clip is { Length: > 200 } ? clip.Substring(0, 200) + "…" : clip,
            BrowserActiveTabHint: browserHint,
            BatteryState: battery,
            RecentFiles: recent,
            InstalledAppCount: _cachedInstalledAppCount,
            InstalledAppSummary: _cachedInstalledAppSummary,
            OsVersion: _cachedOsVersion);
    }

    /// <summary>
    /// Compact single-line representation for prompt injection. Designed to
    /// add ~600-1400 bytes to the generator prompt — heavy enough to be
    /// useful, light enough that the per-token reasoning cost is negligible.
    /// </summary>
    public string ToPromptBlock()
    {
        var sb = new StringBuilder(1024);
        sb.Append("WORLD_STATE = { ");
        sb.Append("os=\"").Append(Escape(OsVersion ?? "Windows")).Append("\"");
        sb.Append(", monitors=").Append(DisplayCount).Append("(").Append(DisplaySummary).Append(")");
        if (!string.IsNullOrEmpty(ForegroundProcess))
        {
            sb.Append(", fg=\"").Append(Escape(ForegroundProcess)).Append("/pid=").Append(ForegroundPid)
              .Append("/hwnd=0x").Append(ForegroundHwnd.ToString("X")).Append("\"");
            if (!string.IsNullOrEmpty(ForegroundTitle))
                sb.Append(" title=\"").Append(Escape(Truncate(ForegroundTitle, 80))).Append('"');
        }
        sb.Append(", cursor=(").Append(CursorX).Append(',').Append(CursorY).Append(')');
        if (BrowserActiveTabHint is { } tab)
            sb.Append(", active_tab=\"").Append(Escape(Truncate(tab, 80))).Append('"');
        if (BatteryState is { } bat)
            sb.Append(", battery=\"").Append(Escape(Truncate(bat, 40))).Append('"');
        sb.Append(", windows=[").Append(TopVisibleWindows.Count).Append(']');
        if (TopVisibleWindows.Count > 0)
            sb.Append(" top=\"").Append(Escape(TopVisibleWindows[0])).Append('"');
        if (ClipboardText is { Length: > 0 } c)
            sb.Append(", clipboard=\"").Append(Escape(Truncate(c, 60))).Append('"');
        if (RecentFiles.Count > 0)
            sb.Append(", recent_files=[").Append(Escape(string.Join(", ", RecentFiles.Take(5).Select(Path.GetFileName)))).Append(']');
        sb.Append(", installed_apps=").Append(InstalledAppCount)
          .Append(" (").Append(Escape(Truncate(InstalledAppSummary, 220))).Append(")");
        sb.Append(" }");
        return sb.ToString();
    }

    // ── Safe wrappers — never throw ────────────────────────────

    private static (WindowsAppManager.WindowInfo Window, string ProcessName)? SafeFrontmost()
    {
        try { return WindowsAppManager.GetFrontmostApp(); } catch { return null; }
    }
    private static (int X, int Y) SafeCursor()
    {
        try { return WindowsAppManager.GetCursorPosition(); } catch { return (0, 0); }
    }
    private static List<WindowsAppManager.DisplayInfo> SafeDisplays()
    {
        try { return WindowsAppManager.GetDisplays(); } catch { return new(); }
    }
    private static List<WindowsAppManager.WindowInfo> SafeWindows()
    {
        try { return WindowsAppManager.ListVisibleWindows(); } catch { return new(); }
    }
    private static string? SafeReadClipboard()
    {
        try { return WindowsAppManager.ReadClipboard(); } catch { return null; }
    }
    private static List<string> SafeProbeRecentFiles()
    {
        try { return ProbeRecentFiles(); } catch { return new(); }
    }

    [SupportedOSPlatform("windows")]
    private static string ProbeOsVersion()
    {
        // Read ProductName + DisplayVersion via registry (cheap, 1 ms).
        using var name = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var pn   = name?.GetValue("ProductName") as string ?? "Windows";
        var ver  = name?.GetValue("DisplayVersion") as string ?? name?.GetValue("ReleaseId") as string ?? "?";
        var ed   = name?.GetValue("EditionID") as string;
        return string.IsNullOrEmpty(ed) ? $"{pn} {ver}" : $"{pn} {ver} {ed}";
    }

    [SupportedOSPlatform("windows")]
    private static (int Count, string Summary) ProbeInstalledApps()
    {
        // Enumerate Start Menu shortcuts — stable across desktop installs,
        // doesn't require UAC, gives the agent a "what apps are here" view
        // without enumerating Program Files.
        var names = new List<string>();
        string[] startMenuRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        foreach (var root in startMenuRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var n = Path.GetFileNameWithoutExtension(lnk);
                    if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase))
                        names.Add(n);
                }
            }
            catch { /* individual subfolder access failures are fine */ }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var top = names.Take(40).ToList();
        var summary = top.Count == 0
            ? "(no shortcuts enumerated)"
            : string.Join(", ", top) + (names.Count > 40 ? $" …+{names.Count - 40} more" : "");
        return (names.Count, summary);
    }

    [SupportedOSPlatform("windows")]
    private static string? SafeProbeBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            using var coll = searcher.Get();
            foreach (ManagementObject o in coll)
            {
                var pct = o["EstimatedChargeRemaining"]?.ToString();
                var ac  = o["BatteryStatus"]?.ToString();
                var s = $"AC={ac} pct={pct ?? "?"}";
                o.Dispose();
                return s;
            }
        }
        catch { }
        return null;
    }

    private static List<string> ProbeRecentFiles()
    {
        var hits = new List<string>();
        // Last files modified across user dirs. Top-level enumeration is
        // fast on every modern filesystem; recursion would be slow + risky
        // for huge user libraries.
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Documents",
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try { hits.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)); }
            catch { }
        }
        return hits
            .Where(p => !string.IsNullOrEmpty(p))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(5)
            .ToList();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
}
