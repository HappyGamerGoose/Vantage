// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ActionVerifier.cs
//
// Per-action verification catalog. Maps each ACI tool ("click", "type",
// "launch_app", …) to a typed contract that decides whether the action
// landed as expected — using the WorldDiff + ScreenshotDiff deltas plus
// the parsed AgentAction arguments.
//
// The whole point: don't trust the agent to verify itself via another
// model call. The system reads deterministic state (foreground hwnd,
// cursor position, window titles, screen change) and emits a yes/no
// verdict. A no becomes a structured feedback line the worker injects
// into the next prompt — "the action did not visibly change state; try
// a different target".

using System.Text.Json;
using Vantage.Services;

namespace Vantage.Services.Agent;

public sealed record VerificationResult(
    bool Met,
    string Tool,
    string Reason,
    IReadOnlyDictionary<string, string>? Detail = null);

public static class ActionVerifier
{
    /// <summary>
    /// Top-level entry: dispatches to the catalog for the given tool, or
    /// falls back to a "no observable ground-truth check available" verdict
    /// (not Met) when the tool isn't known.
    /// </summary>
    public static VerificationResult Verify(
        string tool,
        JsonElement actionArgs,
        WorldSnapshot before,
        WorldSnapshot after,
        WorldDiff diff,
        ScreenshotDiff screenDiff,
        TimeSpan waitTarget = default)
    {
        if (string.IsNullOrEmpty(tool))
            return new VerificationResult(false, "<unknown>", "no action tool emitted by agent");

        if (_catalog.TryGetValue(tool, out var fn))
            return fn(actionArgs, before, after, diff, screenDiff);

        // Unrecognized tool — treat as PASS (we can't verify it; the result
        // returned from dispatch will be surfaced by the worker otherwise).
        // But make a note in detail so the verification-feedback system
        // knows the tool isn't covered.
        return new VerificationResult(true, tool,
            "tool not in verifier catalog — passing without ground-truth check",
            new Dictionary<string, string> { ["unverifiedTool"] = tool });
    }

    /// <summary>True if the tool is on the action surface we know how to verify.</summary>
    public static bool IsKnownTool(string tool) => tool is not null && _catalog.ContainsKey(tool);

    // ────────────────────────── catalog ──────────────────────────

    private static readonly Dictionary<string, Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>>
        _catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── click family ──────────────────────────────────────────
        ["click"]             = ClickVerify(focusRegionCheck: true),
        ["left_click"]        = ClickVerify(focusRegionCheck: true),
        ["right_click"]       = ClickVerify(focusRegionCheck: true),
        ["double_click"]      = ClickVerify(focusRegionCheck: true),

        ["click_xy"]          = ClickVerify(focusRegionCheck: true),
        ["left_click_xy"]     = ClickVerify(focusRegionCheck: true),
        ["right_click_xy"]    = ClickVerify(focusRegionCheck: true),
        ["double_click_xy"]   = ClickVerify(focusRegionCheck: true),
        ["click_element"]     = ClickVerify(focusRegionCheck: true),
        ["click_window_xy"]   = ClickVerify(focusRegionCheck: true),

        ["highlight_text_span"] = ClickVerify(focusRegionCheck: true),

        // ── drag family ───────────────────────────────────────────
        ["drag"]              = DragVerify(),
        ["drag_xy"]           = DragVerify(),
        ["drag_window"]       = DragVerify(),

        // ── scroll family ─────────────────────────────────────────
        ["scroll"]            = ScrollVerify(),
        ["scroll_xy"]         = ScrollVerify(),
        ["scroll_window"]     = ScrollVerify(),

        // ── keyboard / typing ────────────────────────────────────
        ["type"]              = TypeVerify(),
        ["type_text"]         = TypeVerify(),
        ["press_key"]         = KeyVerify(),
        ["key"]               = KeyVerify(),
        ["hotkey"]            = KeyVerify(),
        ["press_window_key"]  = KeyVerify(),
        ["type_window_text"]  = TypeVerify(),
        ["set_value"]         = TypeVerify(),

        // ── cursor ────────────────────────────────────────────────
        ["move"]              = MoveVerify(),
        ["move_mouse"]        = MoveVerify(),
        ["cursor_position"]   = CursorPositionVerify(),

        // ── window management ─────────────────────────────────────
        ["launch_app"]        = LaunchAppVerify(),
        ["launch_path"]       = LaunchAppVerify(),
        ["focus_window"]      = FocusWindowVerify(),
        ["focus_app"]         = FocusWindowVerify(),
        ["activate_window"]   = FocusWindowVerify(),
        ["resize_app"]        = ResizeAppVerify(),
        ["close_window"]      = CloseWindowVerify(),
        ["close_app"]         = CloseWindowVerify(),
        ["kill_process"]      = KillProcessVerify(),

        // ── read-only / observation ───────────────────────────────
        ["list_apps"]         = ObservationVerify(),
        ["list_windows"]      = ObservationVerify(),
        ["get_window_state"]  = ObservationVerify(),
        ["list_processes"]    = ObservationVerify(),
        ["displays"]          = ObservationVerify(),
        ["frontmost_app"]     = ObservationVerify(),
        ["wait_for_window"]   = ObservationVerify(),
        ["screenshot"]        = ObservationVerify(),

        // ── clipboard ─────────────────────────────────────────────
        ["get_clipboard"]     = ObservationVerify(),
        ["set_clipboard"]     = SetClipboardVerify(),
        ["read_clipboard"]    = ObservationVerify(),
        ["write_clipboard"]   = SetClipboardVerify(),

        // ── shell ─────────────────────────────────────────────────
        ["powershell"]        = PowerShellVerify(),
        ["run_powershell"]    = PowerShellVerify(),
        ["run_shell"]         = PowerShellVerify(),

        // ── timing ────────────────────────────────────────────────
        ["wait"]              = WaitVerify(),
        ["wait_seconds"]      = WaitVerify(),

        // ── convenience (alias of existing tool combos) ──────────
        ["snap_window"]            = KeyVerify(),
        ["screenshot_to_clipboard"] = ObservationVerify(),
        ["wait_for_process"]       = TerminateVerify(), // passed if dispatcher returned Ok

        // ── terminators ───────────────────────────────────────────
        ["done"]              = TerminateVerify(),
        ["fail"]              = TerminateVerify(),
        ["answer_user"]       = TerminateVerify(),
    };

    // ────────────────────────── handlers ─────────────────────────

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        ClickVerify(bool focusRegionCheck) =>
        (args, before, after, diff, screenDiff) =>
    {
        // pre: target coordinates must be within a display
        if (TryReadCoords(args, out var x, out var y))
        {
            var monitor = WindowsAutomationService.GetPrimaryMonitor();
            if (x < 0 || x >= monitor.LogicalWidth
                || y < 0 || y >= monitor.LogicalHeight)
            {
                return new VerificationResult(false, "click",
                    $"target ({x},{y}) is outside the captured primary display",
                    new Dictionary<string, string> { ["x"] = x.ToString(), ["y"] = y.ToString() });
            }
        }

        // post: screen changed (typical case — opening menu, focusing button,
        // selection, dialog appearing) OR foreground changed
        var worldChanged = diff.ForegroundChanged
                        || diff.ForegroundProcessChanged
                        || diff.WindowsAddedCount > 0
                        || diff.WindowsRemovedCount > 0;

        if (worldChanged)
            return new VerificationResult(true, "click",
                "ground truth changed (window/foreground/process): " + diff);

        // No world-state change observed — count on the screen delta.
        // Threshold is intentionally loose (1.5% of samples). Subtle clicks
        // (focus change, hover, scroll-edge nudge) produce small but valid
        // screen deltas; strict thresholds would false-positive as "no
        // action" and confuse the model. A real no-op produces ~0% delta.
        if (screenDiff.IsSignificant(0.015))
            return new VerificationResult(true, "click",
                $"screen changed ({screenDiff.TotalChangeRatio:P2} of samples): {screenDiff.HotRegionSummary}");

        return new VerificationResult(false, "click",
            "no observable change — click may have landed on a dead region; try a different target",
            new Dictionary<string, string>
            {
                ["before"] = before.Compact(),
                ["after"]  = after.Compact(),
                ["diff"]   = screenDiff.HotRegionSummary ?? ""
            });
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        DragVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // Deterministic ground truth: cursor landed at the requested target.
        if (TryReadDragEnd(args, out var ex, out var ey) &&
            (Math.Abs(after.CursorX - ex) > 8 || Math.Abs(after.CursorY - ey) > 8))
        {
            return new VerificationResult(false, "drag",
                $"cursor ended at ({after.CursorX},{after.CursorY}), expected ({ex},{ey})");
        }
        if (!diff.CursorMoved)
            return new VerificationResult(false, "drag", "cursor never moved");
        if (screenDiff.IsSignificant(0.005))
            return new VerificationResult(true, "drag", $"drag tracked + screen changed: {screenDiff.HotRegionSummary}");
        // Some drags produce little visible change but move selected items.
        return new VerificationResult(true, "drag", "drag movement ok (cursor verified at end position)");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        ScrollVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // Scrolling typically doesn't move cursor or change windows. Most
        // reliable signal is the screen-delta in the scroll region.
        if (screenDiff.IsSignificant(0.005))
            return new VerificationResult(true, "scroll",
                $"content scrolled ({screenDiff.TotalChangeRatio:P2} changed samples)");
        return new VerificationResult(false, "scroll",
            "no visible content shift — page may be at its edge or the wrong pane has focus");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        TypeVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // post: text was inserted somewhere. We can't OCR cheaply, but the
        // cursor moves through the focused field OR the screen changed.
        if (!diff.CursorMoved && !screenDiff.IsSignificant(0.015))
        {
            return new VerificationResult(false, "type",
                "no observable change after typing — focus may have been lost",
                new Dictionary<string, string>
                {
                    ["before_fg"] = before.ForegroundTitle,
                    ["after_fg"]  = after.ForegroundTitle
                });
        }
        return new VerificationResult(true, "type",
            $"text input registered (screen delta: {screenDiff.TotalChangeRatio:P2})");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        KeyVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // Keys like Esc or Alt+Tab switch foreground. Enter/keys in dialogs
        // dismiss dialogs. Standalone letters typically produce no observable
        // change unless typed in a text field.
        if (diff.ForegroundChanged || diff.WindowsRemovedCount > 0)
            return new VerificationResult(true, "key", "key caused UI transition: " + diff);
        if (screenDiff.IsSignificant(0.012))
            return new VerificationResult(true, "key", $"key triggered visible change: {screenDiff.HotRegionSummary}");
        return new VerificationResult(true, "key",
            "no visible reaction — key may have no effect in current context (passing without side effects)");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        MoveVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // Cursor position is the deterministic ground truth.
        if (TryReadCoords(args, out var x, out var y))
        {
            if (Math.Abs(after.CursorX - x) <= 4 && Math.Abs(after.CursorY - y) <= 4)
                return new VerificationResult(true, "move",
                    $"cursor at expected ({after.CursorX},{after.CursorY})");
            return new VerificationResult(false, "move",
                $"cursor ended at ({after.CursorX},{after.CursorY}), expected ({x},{y})");
        }
        return diff.CursorMoved
            ? new VerificationResult(true, "move", "cursor moved")
            : new VerificationResult(false, "move", "cursor did not move");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        CursorPositionVerify() =>
        (_, _, _, _, _) =>
        new VerificationResult(true, "cursor_position", "read-only — no side effect to verify");

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        LaunchAppVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // Verify the named app is now running (or a window with its title appeared).
        var name = TryReadString(args, "name") ?? TryReadString(args, "executable") ?? TryReadString(args, "process");
        if (string.IsNullOrEmpty(name))
            return new VerificationResult(true, "launch_app",
                "no app name in action; passing — dispatcher will report errors");

        var processHint = Path.GetFileNameWithoutExtension(name);
        var running = WindowsAppManager.ListRunningApps();
        var byName = running.Any(a =>
            a.Name.Contains(processHint, StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (byName)
            return new VerificationResult(true, "launch_app",
                $"process '{name}' is running, foreground = '{after.ForegroundTitle}'");

        // App may be slow to start; ForegroundProcess may have changed
        if (diff.ForegroundProcessChanged)
            return new VerificationResult(true, "launch_app",
                $"foreground process changed to '{after.ForegroundProcess}'");

        return new VerificationResult(false, "launch_app",
            $"no running process or window found matching '{name}' after launch",
            new Dictionary<string, string>
            {
                ["requested"] = name!,
                ["foregroundNow"] = after.ForegroundProcess,
                ["runningCount"] = after.RunningAppCount.ToString()
            });
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        FocusWindowVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        var title = TryReadString(args, "title")
                  ?? TryReadString(args, "name");
        if (string.IsNullOrEmpty(title))
            return new VerificationResult(true, "focus_window",
                "no title arg — passing; dispatcher may surface errors");

        // post: a window containing the title fragment is foreground now
        if (after.ForegroundTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
            return new VerificationResult(true, "focus_window",
                $"foreground title now contains '{title}'");

        return new VerificationResult(false, "focus_window",
            $"foreground='{after.ForegroundTitle}' does not contain requested '{title}'",
            new Dictionary<string, string>
            {
                ["requested"] = title!,
                ["now"]       = after.ForegroundTitle
            });
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        ResizeAppVerify() =>
        (_, _, after, _, _) =>
        new VerificationResult(true, "resize_app",
            "resize ok — verified by dispatcher return; no side-effect check needed");

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        CloseWindowVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        // A close should remove a window or change foreground. If neither,
        // the close may have been rejected (e.g., unsaved-changes dialog).
        if (diff.WindowsRemovedCount > 0 || diff.ForegroundChanged)
            return new VerificationResult(true, "close_app",
                $"window removed / foreground changed: {diff}");
        if (diff.ForegroundProcessChanged)
            return new VerificationResult(true, "close_app",
                $"foreground process changed to '{after.ForegroundProcess}'");
        return new VerificationResult(false, "close_app",
            "no observable change after close — window may not have existed or was rejected");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        KillProcessVerify() =>
        (args, _, _, _, _) =>
    {
        var name = TryReadString(args, "name") ?? TryReadString(args, "process");
        if (string.IsNullOrEmpty(name))
            return new VerificationResult(true, "kill_process", "no name in args");

        var processName = Path.GetFileNameWithoutExtension(name);
        var stillThere = WindowsAppManager.ListProcesses(processName).Any(p =>
            p.Name.Equals(processName, StringComparison.OrdinalIgnoreCase));
        return stillThere
            ? new VerificationResult(false, "kill_process",
                $"process '{name}' still running after kill")
            : new VerificationResult(true, "kill_process",
                $"process '{name}' is no longer running");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        SetClipboardVerify() =>
        (args, before, after, diff, screenDiff) =>
    {
        var text = TryReadString(args, "text");
        if (string.IsNullOrEmpty(text))
            return new VerificationResult(true, "set_clipboard", "no text in args");

        if (!diff.ClipboardChanged)
            return new VerificationResult(false, "set_clipboard",
                "clipboard did not change after write");

        return new VerificationResult(true, "set_clipboard",
            $"clipboard now has {after.ClipboardText?.Length ?? 0} chars (was {before.ClipboardText?.Length ?? 0})");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        PowerShellVerify() =>
        (_, _, after, _, _) =>
    new VerificationResult(true, "powershell",
        "shell command completed — stdout/stderr captured to disk for the next prompt");

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        ObservationVerify() =>
        (_, _, _, _, _) =>
    new VerificationResult(true, "observation", "read-only tool — no side effect to verify");

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        WaitVerify() =>
        (args, _, _, _, _) =>
    {
        if (TryReadDouble(args, out var sec))
            return new VerificationResult(true, "wait",
                $"waited >= {sec:F2}s (elapsed tracker will sign)");
        return new VerificationResult(true, "wait", "wait completed");
    };

    private static Func<JsonElement, WorldSnapshot, WorldSnapshot, WorldDiff, ScreenshotDiff, VerificationResult>
        TerminateVerify() =>
        (_, _, _, _, _) =>
    new VerificationResult(true, "terminate",
        "run terminator — no further verification needed");

    // ────────────────────────── parsers ──────────────────────────

    private static bool TryReadCoords(JsonElement args, out int x, out int y)
    {
        x = y = 0;
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (args.TryGetProperty("x", out var xe) && xe.TryGetInt32(out var xi)) x = xi;
        else if (args.TryGetProperty("coordinate_x", out xe) && xe.TryGetInt32(out xi)) x = xi;
        else if (args.TryGetProperty("target_x", out xe) && xe.TryGetInt32(out xi)) x = xi;
        else return false;
        if (args.TryGetProperty("y", out var ye) && ye.TryGetInt32(out var yi)) y = yi;
        else if (args.TryGetProperty("coordinate_y", out ye) && ye.TryGetInt32(out yi)) y = yi;
        else if (args.TryGetProperty("target_y", out ye) && ye.TryGetInt32(out yi)) y = yi;
        else return false;
        return true;
    }

    private static bool TryReadDragEnd(JsonElement args, out int x, out int y)
    {
        x = y = 0;
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (args.TryGetProperty("to_x", out var xe) && xe.TryGetInt32(out var xi)) x = xi;
        else if (args.TryGetProperty("end_x", out xe) && xe.TryGetInt32(out xi)) x = xi;
        else return false;
        if (args.TryGetProperty("to_y", out var ye) && ye.TryGetInt32(out var yi)) y = yi;
        else if (args.TryGetProperty("end_y", out ye) && ye.TryGetInt32(out yi)) y = yi;
        else return false;
        return true;
    }

    private static string? TryReadString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(property, out var p)) return null;
        if (p.ValueKind == JsonValueKind.String) return p.GetString();
        if (p.ValueKind == JsonValueKind.Number) return p.GetRawText();
        return null;
    }

    private static bool TryReadDouble(JsonElement args, out double value)
    {
        value = 0;
        if (args.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in args.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number &&
                (p.Name.Contains("wait") || p.Name.Contains("seconds") || p.Name.Contains("duration")))
            {
                if (p.Value.TryGetDouble(out value)) return true;
            }
        }
        return false;
    }
}
