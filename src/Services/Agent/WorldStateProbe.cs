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
// budget on a typical desktop: 8-35 ms.

using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using Vantage.Services;

namespace Vantage.Services.Agent;

public sealed record InstalledAppInfo(string Name, string? ExecutablePath);

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
    IReadOnlyList<InstalledAppInfo> InstalledApps,
    IReadOnlyList<WindowsAppManager.RunningAppInfo> RunningApps,
    IReadOnlyList<string> RecentApps,
    string? KeyboardLayout,
    bool CapsLockOn,
    bool NumLockOn,
    bool ScrollLockOn,
    string LocalTimeIso,
    string TimeZoneId,
    string? OsVersion)
{
    // ── Cached session fields ──────────────────────────────────
    private static string? _cachedOsVersion;
    private static int    _cachedInstalledAppCount;
    private static List<InstalledAppInfo> _cachedInstalledApps = new();
    private const int InstalledAppsCachedLimit = 80;

    // Recent-foreground-app ring: when the foreground process changes
    // (different PID from the previous capture), push the previous app
    // name onto this list. The agent uses it as a lightweight "where
    // did the user just come from" hint so it can pick up context
    // (e.g. "they were in Slack a moment ago, now they need me to
    // open Settings — keep the Slack window untouched").
    private static readonly LinkedList<string> _recentAppRing = new();
    private static int _recentAppRingLimit = 6;
    private static string? _lastForegroundName;

    public static void PrimeSessionCache()
    {
        if (_cachedOsVersion is not null) return;
        try { _cachedOsVersion = ProbeOsVersion(); } catch { _cachedOsVersion = "Windows (unknown version)"; }
        try
        {
            var (count, apps) = ProbeInstalledApps();
            _cachedInstalledAppCount  = count;
            _cachedInstalledApps       = apps;
        }
        catch
        {
            _cachedInstalledAppCount  = 0;
            _cachedInstalledApps       = new();
        }
    }

    /// <summary>
    /// Capture the volatile half of the world state. Cheap: 8-35 ms wall clock.
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
        var tApps    = Task.Run(SafeListRunningAppsRich);
        var tKb      = Task.Run(SafeKeyboard);

        // We don't Task.WhenAll + GetAwaiter().GetResult() because that
        // would deadlock; instead we sleep until the slowest task finishes
        // via WaitAll on the thread pool. .NET's thread pool does not run
        // continuations on already-blocking threads.
        Task.WaitAll(new Task[] { tFg, tCursor, tDisp, tWins, tClip, tBattery, tRecent, tApps, tKb });
        sw.Stop();

        var fg       = tFg.IsCompletedSuccessfully   ? tFg.Result   : default;
        var cursor   = tCursor.IsCompletedSuccessfully ? tCursor.Result : ((int X, int Y))(0, 0);
        var displays = tDisp.IsCompletedSuccessfully   ? tDisp.Result  : new List<WindowsAppManager.DisplayInfo>();
        var windows  = tWins.IsCompletedSuccessfully   ? tWins.Result  : new List<WindowsAppManager.WindowInfo>();
        var clip     = tClip.IsCompletedSuccessfully   ? tClip.Result  : null;
        var battery  = tBattery.IsCompletedSuccessfully ? tBattery.Result : null;
        var recent   = tRecent.IsCompletedSuccessfully ? tRecent.Result : new List<string>();
        var apps     = tApps.IsCompletedSuccessfully ? tApps.Result : new List<WindowsAppManager.RunningAppInfo>();
        var kb       = tKb.IsCompletedSuccessfully ? tKb.Result : ((string? Layout, bool Caps, bool Num, bool Scroll))(null, false, false, false);

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

        // Track recent foreground transitions for the prompt. We key on
        // process name (not PID — PIDs are unstable across launches) and
        // only push when the name actually changes, so a user clicking
        // between two windows of the same app doesn't spam the ring.
        var recentAppsForPrompt = new List<string>(_recentAppRing);
        if (fg is { } fgNow && !string.IsNullOrEmpty(fgNow.ProcessName))
        {
            var name = fgNow.ProcessName;
            if (name != _lastForegroundName)
            {
                if (_lastForegroundName is not null)
                {
                    _recentAppRing.AddFirst(_lastForegroundName);
                    while (_recentAppRing.Count > _recentAppRingLimit)
                        _recentAppRing.RemoveLast();
                }
                _lastForegroundName = name;
                recentAppsForPrompt = new List<string>(_recentAppRing);
            }
        }

        var localNow     = DateTimeOffset.Now;
        var localTimeIso = localNow.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var tzId         = TimeZoneInfo.Local.Id;

        return new WorldStateProbe(
            CapturedAt: localNow,
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
            InstalledAppSummary: SummarizeInstalledApps(_cachedInstalledApps),
            InstalledApps: _cachedInstalledApps,
            RunningApps: apps,
            RecentApps: recentAppsForPrompt,
            KeyboardLayout: kb.Layout,
            CapsLockOn: kb.Caps,
            NumLockOn: kb.Num,
            ScrollLockOn: kb.Scroll,
            LocalTimeIso: localTimeIso,
            TimeZoneId: tzId,
            OsVersion: _cachedOsVersion);
    }

    /// <summary>
    /// Compact single-line representation for prompt injection. Designed to
    /// add ~800-1800 bytes to the generator prompt — heavy enough to be
    /// useful, light enough that the per-token reasoning cost is negligible.
    ///
    /// The fields the model reaches for most are surfaced first; deep
    /// metadata (running apps + installed apps with exe paths) is
    /// formatted so the model can quickly scan the names AND copy a
    /// concrete exe path into a `launch_app` action.
    /// </summary>
    public string ToPromptBlock()
    {
        var sb = new StringBuilder(1536);
        sb.Append("WORLD_STATE = { ");
        sb.Append("os=\"").Append(Escape(OsVersion ?? "Windows")).Append("\"");
        sb.Append(", tz=\"").Append(Escape(TimeZoneId)).Append("\"");
        sb.Append(", time=\"").Append(LocalTimeIso).Append("\"");
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

        // Keyboard / lock state — useful for accurate typing. CapsLock
        // surprises are a real source of "agent typed everything in
        // upper case" failures.
        sb.Append(", kb=\"").Append(KeyboardLayout ?? "?")
          .Append(" caps=").Append(CapsLockOn ? 1 : 0)
          .Append(" num=").Append(NumLockOn ? 1 : 0)
          .Append(" scroll=").Append(ScrollLockOn ? 1 : 0)
          .Append('"');

        sb.Append(", windows=[").Append(TopVisibleWindows.Count).Append(']');
        if (TopVisibleWindows.Count > 0)
            sb.Append(" top=\"").Append(Escape(TopVisibleWindows[0])).Append('"');
        if (ClipboardText is { Length: > 0 } c)
            sb.Append(", clipboard_chars=").Append(c.Length);
        if (RecentFiles.Count > 0)
            sb.Append(", recent_files=[").Append(Escape(string.Join(", ", RecentFiles.Take(5).Select(Path.GetFileName)))).Append(']');

        // Running apps with rich metadata. Formatted as
        // `name@pid=N haswin=1` so the model can identify a specific
        // process by its number; the IsForeground one is marked with
        // `/fg` so the model doesn't have to cross-reference.
        if (RunningApps.Count > 0)
        {
            sb.Append(", running_apps=[");
            for (int i = 0; i < RunningApps.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var a = RunningApps[i];
                sb.Append(Escape(a.ProcessName)).Append("@pid=").Append(a.Pid);
                if (a.IsForeground) sb.Append("/fg");
                if (a.HasVisibleWindow) sb.Append("/win");
            }
            sb.Append(']');
        }
        if (RecentApps.Count > 0)
        {
            sb.Append(", recent_apps=[").Append(Escape(string.Join(", ", RecentApps))).Append(']');
        }

        // Installed apps with executable paths. The model can call
        // `launch_app` directly with the path; if the path is
        // missing (rare — system shortcuts that don't carry targets)
        // we still surface the name.
        if (InstalledApps.Count > 0)
        {
            sb.Append(", installed_apps=").Append(InstalledAppCount)
              .Append(" (");
            var shown = InstalledApps.Take(12).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var app = shown[i];
                sb.Append(Escape(app.Name));
                if (!string.IsNullOrEmpty(app.ExecutablePath))
                {
                    sb.Append('=').Append(Escape(Truncate(app.ExecutablePath!, 80)));
                }
            }
            if (InstalledAppCount > shown.Count)
                sb.Append(", …+").Append(InstalledAppCount - shown.Count).Append(" more");
            sb.Append(')');
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static string SummarizeInstalledApps(List<InstalledAppInfo> apps)
    {
        if (apps.Count == 0) return "(no shortcuts enumerated)";
        var top = apps.Take(40).ToList();
        var summary = string.Join(", ", top.Select(a => a.Name)) +
                      (apps.Count > 40 ? $" …+{apps.Count - 40} more" : "");
        return summary;
    }

    // ── Safe wrappers — never throw ────────────────────────────

    private static (WindowsAppManager.WindowInfo Window, string ProcessName)? SafeFrontmost()
    {
        try { return WindowsAppManager.GetFrontmostApp(); } catch { return null; }
    }
    private static (int X, int Y) SafeCursor()
    {
        try { return WindowsAutomationService.GetCursorPositionLogical(); } catch { return (0, 0); }
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
    private static List<WindowsAppManager.RunningAppInfo> SafeListRunningAppsRich()
    {
        try { return WindowsAppManager.ListRunningAppsRich(); } catch { return new(); }
    }
    private static (string? Layout, bool Caps, bool Num, bool Scroll) SafeKeyboard()
    {
        try { return WindowsAppManager.GetKeyboardState(); } catch { return (null, false, false, false); }
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

    /// <summary>
    /// Enumerate Start Menu shortcuts AND resolve each .lnk's target
    /// path so the agent gets a name → exe-path map. Without the
    /// target, the agent would have to guess at `launch_app`
    /// arguments; with the path, it can issue a precise call.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (int Count, List<InstalledAppInfo> Apps) ProbeInstalledApps()
    {
        var apps = new Dictionary<string, InstalledAppInfo>(StringComparer.OrdinalIgnoreCase);
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
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (apps.ContainsKey(n)) continue;
                    // Resolve the .lnk target. Failures are non-fatal —
                    // we just store the name with a null exe path.
                    var target = SafeResolveShortcut(lnk);
                    apps[n] = new InstalledAppInfo(n, target);
                }
            }
            catch { /* individual subfolder access failures are fine */ }
        }
        // Stable order: alphabetical by name.
        var list = apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (list.Count, list);
    }

    [SupportedOSPlatform("windows")]
    private static string? SafeResolveShortcut(string lnkPath)
    {
        try
        {
            // WScript.Shell COM is the standard shortcut resolver. We
            // avoid taking a hard reference so the probe never throws
            // if WSH is unavailable on locked-down systems. ProgID lookup
            // is wrapped because Type.GetTypeFromProgID returns null on
            // systems where WSH has been disabled by policy; the null
            // check makes that a "no target" rather than an NRE.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            try
            {
                dynamic? shortcut = shell.CreateShortcut(lnkPath);
                if (shortcut is null) return null;
                try
                {
                    var target = shortcut.TargetPath as string;
                    return string.IsNullOrWhiteSpace(target) ? null : target;
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch
        {
            return null;
        }
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
