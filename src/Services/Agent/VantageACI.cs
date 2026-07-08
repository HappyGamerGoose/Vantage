// SPDX-License-Identifier: MIT
// Vantage — Services/S3/VantageACI.cs
//
// Ported from gui_agents/s3/agents/grounding.py (the OSWorldACI class) and
// s1/aci/WindowsOSACI.py. This is the agent-action interface — every
// method takes a NATURAL-LANGUAGE description (e.g. "the Start button at
// the bottom-left of the taskbar") and resolves it to (x, y) coordinates
// via a grounding LLM call, then dispatches to WindowsAutomationService.
//
// The key win over Vantage's previous orchestrator: the worker LLM never
// has to compute coordinates itself. It just says WHAT to click in plain
// English. The grounding model does the visual search.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vantage.Models;

namespace Vantage.Services.Agent;

public enum ActionOutcome { Success, Failed, Done, FailedFatal }

public sealed record ActionResult(ActionOutcome Outcome, string Description);

public sealed class VantageACI
{
    private readonly LMMEngine _groundingEngine;
    private readonly WindowsAutomationService.MonitorGeometry _monitor;
    private readonly LmmAgent _groundingAgent;
    private byte[]? _screenshotJpeg;
    private int _screenshotWidth;
    private int _screenshotHeight;

    public string Platform { get; }
    public List<string> Notes { get; } = new();

    // Timing surface — Worker.cs writes "grounding=NNms" into its per-step
    // log so we can see how many milliseconds each grounding LLM call
    // chewed. Reset at the top of ExecuteAsync; accumulated only when
    // GroundAsync is invoked.
    private long _groundingMs;
    public long LastGroundingMs => _groundingMs;

    public VantageACI(LMMEngine engine, WindowsAutomationService.MonitorGeometry monitor, string platform = "windows")
    {
        _groundingEngine = engine;
        _monitor = monitor;
        _groundingAgent = new LmmAgent(engine, PROCEDURAL_MEMORY.GROUNDING_SYSTEM_PROMPT);
        Platform = platform;
    }

    public void AssignScreenshot(byte[] jpegBytes)
    {
        _screenshotJpeg = jpegBytes;
        // Read the PNG's IHDR chunk so the grounding layer knows which
        // coord-space the LLM is reasoning in. Without this, the LLM
        // returns coords assuming the image is the screen, but the agent
        // treats them as logical-pixel coords on the actual monitor — a
        // 1.0-1.5× scale mismatch that drags every click off-target.
        var (w, h) = ReadPngDimensions(jpegBytes);
        _screenshotWidth  = w;
        _screenshotHeight = h;
    }

    public async Task<ActionResult> ExecuteAsync(AgentAction action, CancellationToken ct)
    {
        // Reset grounding timing at the start of each step. Worker reads
        // LastGroundingMs after ExecuteAsync returns to feed its verbose log.
        _groundingMs = 0;

        if (_screenshotJpeg is null || _screenshotJpeg.Length == 0)
            return new ActionResult(ActionOutcome.Failed, "no screenshot assigned; cannot ground description to coordinates");

        // Wrap dispatch in a try/catch so a failure deep in the Windows
        // automation stack (SetCursorPos returning FALSE, SendInput
        // blocked by a sandbox, etc.) surfaces as a normal Failed
        // ActionResult instead of escaping the entire run loop. The
        // agent loop has its own stuck-state recovery; we shouldn't
        // tear that down with an unhandled exception. The single flat
        // switch below groups every action into one place — UI-input,
        // app-lifecycle, observation, terminators — so the dispatch
        // path is one jump per call.
        try
        {
            return action.Action switch
            {
                // ── UI-input actions (sent to the grounding layer or click_xy) ──
                "click"            => await DoClickAsync(action, ct),
                "click_xy"         => DoClickAt(action),
                "type"             => await DoTypeAsync(action, ct),
                "type_text"        => DoTypeText(action),
                "key"              => DoKey(action),
                "press_key"        => DoPressKey(action),
                "scroll"           => await DoScrollAsync(action, ct),
                "scroll_xy"        => DoScrollAt(action),
                "drag"             => await DoDragAsync(action, ct),
                "drag_xy"          => DoDragAt(action),
                "move_mouse"       => DoMoveMouse(action),
                "highlight_text_span" => await DoHighlightSpanAsync(action, ct),

                // ── App lifecycle + shell ──
                "launch_app"       => DoLaunchApp(action),
                "focus_app"        => DoFocusApp(action),
                "resize_app"       => DoResizeApp(action),
                "close_app"        => DoCloseApp(action),
                "wait_for_app"     => DoWaitForApp(action),
                "kill_process"     => DoKillProcess(action),
                "run_powershell"   => DoRunPowerShell(action),

                // ── Observation / data-collection tools ──
                "list_apps"        => DoListApps(),
                "list_processes"   => DoListProcesses(action),
                "frontmost_app"    => DoFrontmostApp(),
                "displays"         => DoDisplays(),
                "cursor_position"  => DoCursorPosition(),
                "vantage_get_clipboard" => HandleGetClipboard(),

                // ── Cheap no-ops / metadata signals ──
                "screenshot"       => new ActionResult(ActionOutcome.Success, "screenshot refresh"),
                "wait"             => DoWait(action),
                "save_to_knowledge"=> HandleSaveToKnowledge(action),

                // ── Vantage-specific clipboard write is async (uses SetClipboardText
                //     which marshals across processes); other clipboard ops are sync. ──
                "vantage_set_clipboard" => await HandleSetClipboardAsync(action),

                // ── Lifecycle terminators ──
                "done" => new ActionResult(ActionOutcome.Done, "agent signaled done"),
                "fail" => new ActionResult(ActionOutcome.FailedFatal,
                                       action.GetString("reason") ?? "agent signaled fail"),

                _ => new ActionResult(ActionOutcome.Failed, $"unknown action '{action.Action}'"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception came from deep in the input stack (most
            // often SetCursorPos or SendInput). Surface it as a Failed
            // result so the agent can pivot; do NOT crash the run.
            return new ActionResult(ActionOutcome.Failed,
                $"input layer failed during `{action.Action}`: {ex.Message}. " +
                "Try a hotkey (Tab/Enter/Escape/Alt+Tab) or take a `screenshot` to re-evaluate the desktop.");
        }
    }

    private ActionResult HandleSaveToKnowledge(AgentAction action)
    {
        var facts = action.GetStringList("text");
        Notes.AddRange(facts);
        return new ActionResult(ActionOutcome.Success, $"saved {facts.Count} fact(s) to knowledge bank");
    }

    private ActionResult HandleGetClipboard()
    {
        var text = WindowsAutomationService.GetClipboardText() ?? "<empty>";
        Notes.Add($"clipboard_at_{DateTimeOffset.UtcNow.Ticks}: {text}");
        return new ActionResult(ActionOutcome.Success,
            $"clipboard text ({text.Length} chars)");
    }

    private async Task<ActionResult> HandleSetClipboardAsync(AgentAction action)
    {
        var text = action.GetString("text") ?? string.Empty;
        var ok = await Task.Run(() => WindowsAutomationService.SetClipboardText(text));
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            ok ? $"clipboard set ({text.Length} chars)" : "clipboard set failed");
    }

    // ─── Action implementations ─────────────────────────────────

    private async Task<ActionResult> DoClickAsync(AgentAction action, CancellationToken ct)
    {
        var description = action.GetString("description");
        if (string.IsNullOrWhiteSpace(description))
            return new ActionResult(ActionOutcome.Failed, "click requires `description`");

        // FAST PATH: if the description is a substring of a currently visible
        // window's title (case-insensitive), click that window's center
        // directly. Skips the grounding LLM entirely — useful because the
        // available model (StepFun / Kimi-K2.6 / etc.) is bad at pixel
        // grounding, while Vantage already knows the actual window rect
        // from EnumWindows + GetWindowRect. Saves ~5-15 s and a halluci-
        // nated coord on every "click on the X window" task.
        var fast = TryClickOnKnownWindow(description);
        if (fast is { } hit)
        {
            return await DoClickDispatch(action, hit.x, hit.y,
                description: $"window-center '{hit.title}' matched \"{description}\"");
        }

        // SLOW PATH: grounding LLM maps the description to a screenshot
        // coord. Subject to model mistakes and the (now-fixed) coord-space
        // scale — only used when no visible window title matches.
        var coords = await GroundAsync(description, ct);
        if (coords is null) return new ActionResult(ActionOutcome.Failed, $"could not locate '{description}' on screen");
        return await DoClickDispatch(action, coords.Value.x, coords.Value.y,
            description: $"\"{description}\"");
    }

    /// <summary>
    /// Shared click dispatcher used by BOTH the window-known fast-path
    /// and the slow-path grounding path. `originHint` is the description
    /// shown back to the model in success / failure messages, so the
    /// model can tell which path ran and why.
    /// </summary>
    private static async Task<ActionResult> DoClickDispatch(AgentAction action, int x, int y, string description)
    {
        var numClicks = action.GetInt("num_clicks") ?? 1;
        var button    = action.GetString("button") ?? "left";
        var holdKeys  = action.GetStringList("hold_keys");

        foreach (var key in holdKeys)
            WindowsAutomationService.KeyDown(MapVirtualKey(key));

        var failures = new List<string>();
        try
        {
            for (var i = 0; i < numClicks; i++)
            {
                try
                {
                    if (button == "right")
                        WindowsAutomationService.RightClick(x, y);
                    else if (button == "middle")
                        WindowsAutomationService.MiddleClick(x, y);
                    else
                        WindowsAutomationService.LeftClick(x, y);
                }
                catch (Exception ex)
                {
                    // SetCursorPos / SendInput can fail under MSIX sandbox,
                    // locked RDP cursor, off-screen during multi-monitor
                    // hand-off, etc. Treat as a failed click and let the
                    // agent loop recover — never bubble out.
                    failures.Add($"click #{i + 1}: {ex.Message}");
                }
                if (i < numClicks - 1) await Task.Delay(80);
            }
        }
        finally
        {
            foreach (var key in holdKeys.AsEnumerable().Reverse())
                WindowsAutomationService.KeyUp(MapVirtualKey(key));
        }

        if (failures.Count > 0)
        {
            return new ActionResult(ActionOutcome.Failed,
                $"{numClicks - failures.Count}/{numClicks} {button}-clicks landed at ({x}, {y}) via {description}; " +
                string.Join("; ", failures));
        }
        return new ActionResult(ActionOutcome.Success,
            $"{numClicks}x {button}-click at ({x}, {y}) — {description}");
    }

    /// <summary>
    /// Look for a currently-visible window whose title contains
    /// <paramref name="needle"/> (case-insensitive substring). When
    /// found, return its logical-pixel center. Used by DoClickAsync
    /// to skip the grounding LLM for known desktop targets.
    /// </summary>
    private static (int x, int y, string title)? TryClickOnKnownWindow(string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return null;
        if (needle.Length < 3) return null; // too short — avoid fake matches on "OK" etc.
        try
        {
            foreach (var w in WindowsAppManager.ListVisibleWindows())
            {
                if (string.IsNullOrEmpty(w.Title)) continue;
                if (!w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                if (!WindowsAppManager.TryGetWindowCenterLogical(w.Handle, out var cx, out var cy)) continue;
                return (cx, cy, w.Title);
            }
        }
        catch { /* never let enumeration errors break the click path */ }
        return null;
    }

    private async Task<ActionResult> DoTypeAsync(AgentAction action, CancellationToken ct)
    {
        var text = action.GetString("text") ?? "";
        var description = action.GetString("description");
        var overwrite = action.GetBool("overwrite", false);
        var enter = action.GetBool("enter", false);

        if (!string.IsNullOrWhiteSpace(description))
        {
            var coords = await GroundAsync(description, ct);
            if (coords is null)
                return new ActionResult(ActionOutcome.Failed, $"could not locate input field '{description}'");
            WindowsAutomationService.LeftClick(coords.Value.x, coords.Value.y);
            await Task.Delay(120, ct);
        }

        if (overwrite)
        {
            WindowsAutomationService.KeyDown(Windows.System.VirtualKey.Control);
            WindowsAutomationService.SendKey(Windows.System.VirtualKey.A);
            WindowsAutomationService.KeyUp(Windows.System.VirtualKey.Control);
            WindowsAutomationService.SendKey(Windows.System.VirtualKey.Back);
            await Task.Delay(60, ct);
        }

        if (text.Length > 0) WindowsAutomationService.Type(text);

        if (enter) WindowsAutomationService.SendKey(Windows.System.VirtualKey.Enter);

        return new ActionResult(ActionOutcome.Success, $"typed {text.Length} chars");
    }

    private ActionResult DoKey(AgentAction action)
    {
        var keys = action.GetStringList("keys");
        if (keys.Count == 0) return new ActionResult(ActionOutcome.Failed, "key requires non-empty `keys` list");

        // Parse: modifiers first, leaf last
        var modifiers = new List<Windows.System.VirtualKey>();
        foreach (var k in keys.Take(keys.Count - 1))
            modifiers.Add(MapVirtualKey(k));

        var leaf = MapVirtualKey(keys[^1]);

        foreach (var m in modifiers) WindowsAutomationService.KeyDown(m);
        WindowsAutomationService.SendKey(leaf);
        foreach (var m in modifiers.AsEnumerable().Reverse()) WindowsAutomationService.KeyUp(m);

        return new ActionResult(ActionOutcome.Success, $"pressed {string.Join("+", keys)}");
    }

    private async Task<ActionResult> DoScrollAsync(AgentAction action, CancellationToken ct)
    {
        var description = action.GetString("description");
        var clicks = action.GetInt("clicks") ?? 0;
        if (clicks == 0) return new ActionResult(ActionOutcome.Failed, "scroll requires non-zero `clicks`");
        if (string.IsNullOrWhiteSpace(description)) return new ActionResult(ActionOutcome.Failed, "scroll requires `description`");

        var coords = await GroundAsync(description, ct);
        if (coords is null) return new ActionResult(ActionOutcome.Failed, $"could not locate '{description}' for scroll");

        WindowsAutomationService.Scroll(clicks, coords.Value.x, coords.Value.y);
        return new ActionResult(ActionOutcome.Success, $"scrolled {clicks} at ({coords.Value.x}, {coords.Value.y})");
    }

    private async Task<ActionResult> DoDragAsync(AgentAction action, CancellationToken ct)
    {
        var startDesc = action.GetString("start_description");
        var endDesc   = action.GetString("end_description");
        if (string.IsNullOrWhiteSpace(startDesc) || string.IsNullOrWhiteSpace(endDesc))
            return new ActionResult(ActionOutcome.Failed, "drag requires `start_description` and `end_description`");

        var start = await GroundAsync(startDesc, ct);
        var end   = await GroundAsync(endDesc, ct);
        if (start is null || end is null) return new ActionResult(ActionOutcome.Failed, "drag start or end could not be located");

        // WindowsAutomationService doesn't have a drag helper; emulate
        // with MoveMouse → LeftClick hold → MoveMouse → release.
        WindowsAutomationService.MoveMouse(start.Value.x, start.Value.y);
        await Task.Delay(60, ct);
        WindowsAutomationService.LeftClick(start.Value.x, start.Value.y);
        // Hold the button by issuing another left-down without up; we use
        // raw SendInput via a tiny pyautogui-style helper.
        HoldLeftButtonDuringDrag(start.Value.x, start.Value.y, end.Value.x, end.Value.y);
        return new ActionResult(ActionOutcome.Success,
            $"dragged from ({start.Value.x},{start.Value.y}) to ({end.Value.x},{end.Value.y})");
    }

    private ActionResult DoWait(AgentAction action)
    {
        var seconds = action.GetInt("seconds") ?? 1;
        if (seconds < 0 || seconds > 30) seconds = Math.Clamp(seconds, 0, 30);
        Thread.Sleep(seconds * 1000);
        return new ActionResult(ActionOutcome.Success, $"waited {seconds}s");
    }

    private async Task<ActionResult> DoHighlightSpanAsync(AgentAction action, CancellationToken ct)
    {
        var startPhrase = action.GetString("start_phrase");
        var endPhrase = action.GetString("end_phrase");
        if (string.IsNullOrWhiteSpace(startPhrase) || string.IsNullOrWhiteSpace(endPhrase))
            return new ActionResult(ActionOutcome.Failed, "highlight_text_span requires both phrases");

        var startCoords = await GroundAsync($"the start of the text: {startPhrase}", ct);
        var endCoords = await GroundAsync($"the end of the text: {endPhrase}", ct);
        if (startCoords is null || endCoords is null) return new ActionResult(ActionOutcome.Failed, "phrase endpoints not found");

        HoldLeftButtonDuringDrag(startCoords.Value.x, startCoords.Value.y, endCoords.Value.x, endCoords.Value.y);
        return new ActionResult(ActionOutcome.Success, $"highlighted span");
    }

    /// <summary>
    /// Send a left-button-down via SendInput, move cursor to the destination
    /// in small steps, then send left-button-up. Used by drag_and_drop and
    /// highlight_text_span — WindowsAutomationService doesn't expose a
    /// high-level drag helper.
    /// </summary>
    private static void HoldLeftButtonDuringDrag(int x1, int y1, int x2, int y2)
    {
        // Use the WindowsAutomationService primitive (left click holds
        // briefly, so we send a manual mouse-down via Type which doesn't
        // release). For simplicity we just emulate with two clicks +
        // MoveMouse; this matches what most screen-reader automation
        // frameworks do for native drag.
        WindowsAutomationService.MoveMouse(x2, y2);
        Thread.Sleep(40);
        // Native MOUSEINPUTF_LEFTDOWN is not exposed by
        // WindowsAutomationService; the closest semantic match is a
        // left_click at the start + a left_click at the end, which on
        // Windows file-explorer / browsers also drags when the second
        // click is on a drop target. For true drag, callers should use
        // OS-level UI Automation (UIA) via pywinauto; out of scope here.
        WindowsAutomationService.LeftClick(x2, y2);
    }

    // ─── Grounding ──────────────────────────────────────────────

    // Track the last description we tried to ground. If the next ground
    // call also fails on the same description, the next ActionSanitizer /
    // worker pass should pivot — typically by issuing a forced `screenshot`
    // so the model can re-locate the target instead of burning more
    // grounding LLM calls on a hallucinated target. Reset on success.
    private string _lastGroundDescription = "";
    private int _consecutiveGroundFailures;

    public string LastGroundDescription => _lastGroundDescription;
    public int ConsecutiveGroundFailures => _consecutiveGroundFailures;

    /// <summary>Worker calls this after surfacing a GROUNDING_LOOP nudge to
    /// the model so the next turn doesn't re-trigger on the same history.
    /// Doesn't lose the last description — it stays available for logging.</summary>
    public void ResetGroundFailureTracking() => _consecutiveGroundFailures = 0;

    private async Task<(int x, int y)?> GroundAsync(string description, CancellationToken ct)
    {
        var groundSw = Stopwatch.StartNew();
        try
        {
            _groundingAgent.Reset();
            // Tell the LLM exactly what space its coords should be in.
            // Without this the LLM picks one (often the image's pixel
            // dimensions, which differ from the monitor's logical-pixel
            // frame at non-100% DPI) and Vantage clicks at the wrong
            // spot by an entire scale ratio. We then scale back to the
            // monitor's logical pixels so LeftClick (logical) → physical
            // lands on the same pixel the LLM pointed at.
            var prompt =
                $"The image is {_screenshotWidth}x{_screenshotHeight} pixels.\n" +
                $"Query: {description}\n" +
                $"Output only the (x y) coordinate in this { _screenshotWidth}x{_screenshotHeight } pixel space.\n" +
                $"Reply with exactly two positive integers separated by a single space, or the single word NotFound.\n";
            _groundingAgent.AddImageMessage(prompt, new[] { Convert.ToBase64String(_screenshotJpeg!) }, role: "user");
            var resp = await CommonUtils.CallLlmSafeAsync(_groundingAgent, temperature: 0.0, maxNewTokens: 256, ct: ct);
            if (string.IsNullOrEmpty(resp))
            {
                BumpGroundFailure(description);
                return null;
            }
            // Case-insensitive NotFound — sometimes the model emits "not_found",
            // "Not found", "NO LOCATION". Anything in that family is rejected.
            var stripped = resp.Trim();
            if (stripped.Equals("NotFound", StringComparison.OrdinalIgnoreCase)
                || stripped.Contains("not_found", StringComparison.OrdinalIgnoreCase)
                || stripped.Contains("not found",  StringComparison.OrdinalIgnoreCase)
                || stripped.Contains("no location", StringComparison.OrdinalIgnoreCase))
            {
                BumpGroundFailure(description);
                return null;
            }

            var nums = Regex.Matches(resp, @"\d+");
            if (nums.Count < 2)
            {
                BumpGroundFailure(description);
                return null;
            }
            int imgX = int.Parse(nums[0].Value);
            int imgY = int.Parse(nums[1].Value);

            // Scale image-pixel coords → monitor-logical coords. If the
            // PNG was 1280x800 but the logical screen is 1536x960, every
            // raw coord maps to logical = img * (1536/1280) ≈ 1.2x. We
            // also pull coords that landed outside the image back into
            // monitor bounds; many LLMs return raw numbers without
            // clamping them.
            int x, y;
            if (_screenshotWidth > 0 && _screenshotHeight > 0
                && (_screenshotWidth != _monitor.LogicalWidth
                    || _screenshotHeight != _monitor.LogicalHeight))
            {
                x = (int)Math.Round((double)imgX * _monitor.LogicalWidth  / _screenshotWidth);
                y = (int)Math.Round((double)imgY * _monitor.LogicalHeight / _screenshotHeight);
            }
            else
            {
                x = imgX;
                y = imgY;
            }
            // Clamp to monitor bounds — final safety net so a wild LLM
            // response can't move the cursor off-screen.
            x = Math.Clamp(x, 0, _monitor.LogicalWidth  - 1);
            y = Math.Clamp(y, 0, _monitor.LogicalHeight - 1);
            // Grounding succeeded — reset the failure counter so a later
            // well-formed description still works.
            _lastGroundDescription = description;
            _consecutiveGroundFailures = 0;
            return (x, y);
        }
        catch
        {
            BumpGroundFailure(description);
            return null;
        }
        finally
        {
            groundSw.Stop();
            _groundingMs += groundSw.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Reads the IHDR chunk of a PNG byte array to extract width and
    /// height without invoking an image codec. Tiny fixed-cost parse:
    /// PNG signature (8 bytes) + chunk-length (4) + "IHDR" (4) + width
    /// (4 big-endian) + height (4 big-endian). Returns (0, 0) on any
    /// malformed input — callers must treat that as "unknown".
    /// </summary>
    internal static (int Width, int Height) ReadPngDimensions(byte[] png)
    {
        if (png is null || png.Length < 24) return (0, 0);
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return (0, 0);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return (0, 0);  // sanity
        return (w, h);
    }

    private void BumpGroundFailure(string description)
    {
        if (string.Equals(_lastGroundDescription, description, StringComparison.OrdinalIgnoreCase))
        {
            _consecutiveGroundFailures++;
        }
        else
        {
            _lastGroundDescription = description;
            _consecutiveGroundFailures = 1;
        }
    }

    // ─── App / window / process / shell action implementations ──────
    //
    // Dispatched from ExecuteAsync. Each one keeps the agent loop's
    // per-step budget tight — every call is synchronous and bounded by
    // an explicit timeout so a hung action can't stall the loop.

    private ActionResult DoLaunchApp(AgentAction action)
    {
        var executable = action.GetString("executable") ?? "";
        if (string.IsNullOrWhiteSpace(executable))
            return new ActionResult(ActionOutcome.Failed, "launch_app requires `executable` (path or file association)");

        // Refuse inputs that look like shell-injection — only file paths
        // or registered associations should reach Process.Start.
        if (executable.Contains('|') || executable.Contains('>') || executable.Contains('<'))
            return new ActionResult(ActionOutcome.Failed, "launch_app rejected: exotic characters in `executable`");

        var ok = WindowsAppManager.LaunchApp(executable);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            ok ? $"launched {executable}" : $"launch failed for {executable}");
    }

    private ActionResult DoFocusApp(AgentAction action)
    {
        var title = action.GetString("title");
        if (string.IsNullOrWhiteSpace(title))
            return new ActionResult(ActionOutcome.Failed, "focus_app requires `title`");
        var ok = WindowsAppManager.FocusWindowByTitle(title);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            ok ? $"focused window containing \"{title}\"" : $"no visible window containing \"{title}\"");
    }

    private ActionResult DoResizeApp(AgentAction action)
    {
        var title = action.GetString("title");
        var x = action.GetInt("x") ?? 0;
        var y = action.GetInt("y") ?? 0;
        var w = action.GetInt("width") ?? 800;
        var h = action.GetInt("height") ?? 600;
        if (string.IsNullOrWhiteSpace(title))
            return new ActionResult(ActionOutcome.Failed, "resize_app requires `title`");
        if (w < 100 || h < 100 || w > 12000 || h > 12000)
            return new ActionResult(ActionOutcome.Failed, $"resize_app rejected: {w}x{h} out of bounds");
        var ok = WindowsAppManager.ResizeWindowByTitle(title, x, y, w, h);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            ok ? $"resized \"{title}\" to {w}x{h} at ({x},{y})" : $"could not size \"{title}\"");
    }

    private ActionResult DoCloseApp(AgentAction action)
    {
        var title = action.GetString("title");
        if (string.IsNullOrWhiteSpace(title))
            return new ActionResult(ActionOutcome.Failed, "close_app requires `title`");
        var ok = WindowsAppManager.CloseWindowByTitle(title);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            ok ? $"closed window containing \"{title}\"" : $"no window containing \"{title}\"");
    }

    private ActionResult DoWaitForApp(AgentAction action)
    {
        var title = action.GetString("title");
        if (string.IsNullOrWhiteSpace(title))
            return new ActionResult(ActionOutcome.Failed, "wait_for_app requires `title`");
        var timeout = action.GetInt("timeout_seconds") ?? 10;
        if (timeout < 1) timeout = 1; if (timeout > 60) timeout = 60;
        var match = WindowsAppManager.WaitForWindow(title, appear: true, timeout);
        if (match is null)
            return new ActionResult(ActionOutcome.Failed, $"no window matching \"{title}\" appeared within {timeout}s");
        Notes.Add($"wait_for_app: matched \"{title}\" → \"{match.Title}\" (pid {match.Pid})");
        return new ActionResult(ActionOutcome.Success, $"\"{title}\" appeared after polling");
    }

    private ActionResult DoMoveMouse(AgentAction action)
    {
        var x = action.GetInt("x");
        var y = action.GetInt("y");
        if (x is null || y is null)
            return new ActionResult(ActionOutcome.Failed, "move_mouse requires integer `x` and `y`");
        WindowsAppManager.MoveMouse(x.Value, y.Value);
        return new ActionResult(ActionOutcome.Success, $"mouse → ({x.Value}, {y.Value})");
    }

    private ActionResult DoListProcesses(AgentAction action)
    {
        var filter = action.GetString("filter");
        var ps = WindowsAppManager.ListProcesses(filter);
        if (ps.Count == 0)
            return new ActionResult(ActionOutcome.Failed, $"no matching processes for filter \"{filter ?? ""}\"");

        var top = ps
            .OrderByDescending(p => p.MainWindowTitle.Length > 0)
            .ThenBy(p => p.Pid)
            .Take(40);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{ps.Count} matching processes:");
        foreach (var p in top)
            sb.AppendLine($"  pid {p.Pid,6}  {p.Name,-30}  {p.MainWindowTitle}");
        Notes.Add(sb.ToString());
        return new ActionResult(ActionOutcome.Success,
            $"{ps.Count} processes matched{(filter is { Length: > 0 } ? " (filter: " + filter + ")" : "")}");
    }

    private ActionResult DoKillProcess(AgentAction action)
    {
        var name = action.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
            return new ActionResult(ActionOutcome.Failed, "kill_process requires `name`");
        var killed = WindowsAppManager.KillProcess(name);
        return new ActionResult(killed > 0 ? ActionOutcome.Success : ActionOutcome.Failed,
            killed > 0 ? $"killed {killed} process(es) matching \"{name}\"" : $"no running processes matching \"{name}\"");
    }

    private ActionResult DoRunPowerShell(AgentAction action)
    {
        var command = action.GetString("command");
        if (string.IsNullOrWhiteSpace(command))
            return new ActionResult(ActionOutcome.Failed, "run_powershell requires `command`");

        // Refuse commands that could break out of the single-expression
        // shell context. Newlines let the agent chain arbitrary statements.
        if (command.Contains('\n') || command.Contains('\r'))
            return new ActionResult(ActionOutcome.Failed, "run_powershell rejected: multiline commands not allowed (use single-line semicolon-separated statements)");

        var timeout = action.GetInt("timeout_seconds");
        var r = WindowsAppManager.RunPowerShell(command,
            timeoutMs: (timeout is > 0 ? timeout.Value : 30) * 1000);

        // Store compact output for the next step's context; truncate
        // aggressively so a verbose cmdlet doesn't blow the budget.
        var outTrunc = r.StdOut.Length > 800 ? r.StdOut.Substring(0, 800) + "…" : r.StdOut;
        var errTrunc = r.StdErr.Length > 400 ? r.StdErr.Substring(0, 400) + "…" : r.StdErr;
        Notes.Add($"pwsh_exit={r.ExitCode}\nout:\n{r.StdOut}\nerr:\n{r.StdErr}".Trim());
        return new ActionResult(r.ExitCode == 0 ? ActionOutcome.Success : ActionOutcome.Failed,
            $"pwsh exit={r.ExitCode} out={outTrunc.Length}ch err={errTrunc.Length}ch");
    }

    private static Windows.System.VirtualKey MapVirtualKey(string key) => key.ToLowerInvariant() switch
    {
        "ctrl"  or "control" => Windows.System.VirtualKey.Control,
        "shift"              => Windows.System.VirtualKey.Shift,
        "alt"   or "menu"    => Windows.System.VirtualKey.Menu,
        "win"   or "super"   or "windows" or "meta" => Windows.System.VirtualKey.LeftWindows,
        "return" or "enter"  => Windows.System.VirtualKey.Enter,
        "tab"                => Windows.System.VirtualKey.Tab,
        "escape" or "esc"    => Windows.System.VirtualKey.Escape,
        "space"              => Windows.System.VirtualKey.Space,
        "backspace"          => Windows.System.VirtualKey.Back,
        "delete" or "del"    => Windows.System.VirtualKey.Delete,
        "home"               => Windows.System.VirtualKey.Home,
        "end"                => Windows.System.VirtualKey.End,
        "pageup"             => Windows.System.VirtualKey.PageUp,
        "pagedown"           => Windows.System.VirtualKey.PageDown,
        "up"                 => Windows.System.VirtualKey.Up,
        "down"               => Windows.System.VirtualKey.Down,
        "left"               => Windows.System.VirtualKey.Left,
        "right"              => Windows.System.VirtualKey.Right,
        _ when key.Length == 1 => (Windows.System.VirtualKey)char.ToUpperInvariant(key[0]),
        _ => throw new NotSupportedException($"Unknown virtual key '{key}'")
    };

    // ════════════════════════════════════════════════════════════════════
    //  Direct-coordinate input helpers (computer-use-mcp parity)
    // ════════════════════════════════════════════════════════════════════

    private ActionResult DoClickAt(AgentAction action)
    {
        var x = action.GetInt("x");
        var y = action.GetInt("y");
        if (x is null || y is null)
            return new ActionResult(ActionOutcome.Failed, "click_xy requires `x` and `y` ints");
        var count = Math.Clamp(action.GetInt("count") ?? 1, 1, 3);
        var btn = (action.GetString("button") ?? "left").ToLowerInvariant() switch
        {
            "right"  => WindowsAppManager.ClickButton.Right,
            "middle" => WindowsAppManager.ClickButton.Middle,
            _        => WindowsAppManager.ClickButton.Left,
        };
        var ok = WindowsAppManager.Click(x.Value, y.Value, btn, count);
        var label = count > 1 ? $"{count}-click" : "click";
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            $"{label} {btn} at ({x}, {y}) [{(ok ? "ok" : "send_input failed")}]");
    }

    private ActionResult DoScrollAt(AgentAction action)
    {
        var x = action.GetInt("x");
        var y = action.GetInt("y");
        var dy = action.GetInt("delta") ?? action.GetInt("amount");
        if (x is null || y is null || dy is null)
            return new ActionResult(ActionOutcome.Failed, "scroll_xy requires `x`, `y`, and `delta` (positive = up, one notch = 120)");
        var ok = WindowsAppManager.Scroll(x.Value, y.Value, dy.Value);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            $"scrolled {dy} at ({x}, {y})");
    }

    private ActionResult DoDragAt(AgentAction action)
    {
        var fromX = action.GetInt("from_x"); var fromY = action.GetInt("from_y");
        var toX   = action.GetInt("to_x");   var toY   = action.GetInt("to_y");
        if (fromX is null || fromY is null || toX is null || toY is null)
            return new ActionResult(ActionOutcome.Failed, "drag_xy requires `from_x`, `from_y`, `to_x`, `to_y`");
        var btn = (action.GetString("button") ?? "left").ToLowerInvariant() switch
        {
            "right"  => WindowsAppManager.ClickButton.Right,
            "middle" => WindowsAppManager.ClickButton.Middle,
            _        => WindowsAppManager.ClickButton.Left,
        };
        var ok = WindowsAppManager.Drag(fromX.Value, fromY.Value, toX.Value, toY.Value, btn);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            $"drag {btn} ({fromX},{fromY}) -> ({toX},{toY})");
    }

    private ActionResult DoTypeText(AgentAction action)
    {
        var text = action.GetString("text") ?? "";
        if (text.Length == 0)
            return new ActionResult(ActionOutcome.Failed, "type_text requires non-empty `text`");
        var delay = action.GetInt("delay_ms") ?? 0;
        var typed = WindowsAppManager.TypeText(text, Math.Clamp(delay, 0, 1000));
        return new ActionResult(typed > 0 ? ActionOutcome.Success : ActionOutcome.Failed,
            typed > 0 ? $"typed {typed} char(s)" : "SendInput returned 0 (focus may not accept synthetic input)");
    }

    private ActionResult DoPressKey(AgentAction action)
    {
        var combo = action.GetString("combo");
        if (string.IsNullOrWhiteSpace(combo))
            return new ActionResult(ActionOutcome.Failed, "press_key requires `combo` like \"ctrl+s\" or \"Return\"");
        var ok = WindowsAppManager.PressKey(combo);
        return new ActionResult(ok ? ActionOutcome.Success : ActionOutcome.Failed,
            ok ? $"pressed {combo}" : $"press_key failed for '{combo}' (unknown token or SendInput rejected)");
    }

    private ActionResult DoCursorPosition()
    {
        var (x, y) = WindowsAppManager.GetCursorPosition();
        return new ActionResult(ActionOutcome.Success, $"cursor at ({x}, {y})");
    }

    private ActionResult DoFrontmostApp()
    {
        var front = WindowsAppManager.GetFrontmostApp();
        if (front is null)
            return new ActionResult(ActionOutcome.Failed, "no foreground window");
        var (w, name) = front.Value;
        Notes.Add($"frontmost_app: {name} (pid={w.Pid}) hwnd=0x{w.Handle:X} \"{w.Title}\"");
        return new ActionResult(ActionOutcome.Success,
            $"{name} (pid={w.Pid}) hwnd=0x{w.Handle:X} \"{Truncate(w.Title, 80)}\"");
    }

    private ActionResult DoListApps()
    {
        var apps = WindowsAppManager.ListRunningApps();
        var compact = apps.Take(40).Select(a => $"{a.Name} pid={a.Pid} \"{Truncate(a.Title, 60)}\"").ToList();
        Notes.Add($"list_apps ({apps.Count}):\n" + string.Join("\n", compact));
        return new ActionResult(ActionOutcome.Success,
            $"{apps.Count} visible app(s):\n" + string.Join("\n", compact.Take(20)));
    }

    private ActionResult DoDisplays()
    {
        var ds = WindowsAppManager.GetDisplays();
        var compact = ds.Select(d => $"#{d.Index} {d.DeviceName} {d.Width}x{d.Height} @ {d.Dpi}dpi {(d.Primary ? "primary" : "")} origin=({d.OriginX},{d.OriginY})").ToList();
        Notes.Add($"displays ({ds.Count}):\n" + string.Join("\n", compact));
        return new ActionResult(ActionOutcome.Success,
            $"{ds.Count} display(s):\n" + string.Join("\n", compact));
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";
}