// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ActionSanitizer.cs
//
// Pre-dispatch safeguard. Some smaller models (and tired larger ones)
// occasionally emit a screen-targeted action — `click`, `scroll`, `drag`,
// `move_mouse`, etc. — with neither a `description` (which the grounding
// model resolves to coordinates) NOR raw `x` / `y` coordinates. Such an
// action can never succeed: the dispatcher falls through to a generic
// "Failed" result, the verifier records a no-op, and the agent often
// immediately gives up with `done` — even though the task is unfinished.
//
// Rather than surface those failures as `done`, this sanitizer catches
// the vague action just before dispatch and replaces it with a forced
// `screenshot`. The agent re-locates the target on the next step using
// the fresh screen rather than guessing coordinates.

using System.Text.Json;
using Vantage.Services;

namespace Vantage.Services.Agent;

public static class ActionSanitizer
{
    // Screen-targeted tools that REQUIRES either a description or coords.
    // Each entry: (tool-name, kind).
    //   "desc"  — requires `description`
    //   "coord" — requires `x` and `y` (or `from`/`to` for drag)
    private static readonly Dictionary<string, SanitizationRule> _rules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── click / drag / scroll / move with grounded descriptions ────
            ["click"]               = new SanitizationRule("description", "click"),
            ["left_click"]          = new SanitizationRule("description", "left-click"),
            ["right_click"]         = new SanitizationRule("description", "right-click"),
            ["double_click"]        = new SanitizationRule("description", "double-click"),
            ["highlight_text_span"] = new SanitizationRule("description", "highlight"),
            ["scroll"]              = new SanitizationRule("description", "scroll"),
            ["drag"]                = new SanitizationRule("description", "drag"),

            // ── direct-coordinate variants ────────────────────────────────
            ["click_xy"]            = new SanitizationRule("coord", "click"),
            ["left_click_xy"]       = new SanitizationRule("coord", "click"),
            ["right_click_xy"]      = new SanitizationRule("coord", "click"),
            ["double_click_xy"]     = new SanitizationRule("coord", "click"),
            ["scroll_xy"]           = new SanitizationRule("coord", "scroll"),
            ["drag_xy"]             = new SanitizationRule("coord", "drag"),
            ["move_mouse"]          = new SanitizationRule("coord", "move"),

            // ── text input / keyboard ────────────────────────────────────
            ["type"]                = new SanitizationRule("typed-text", "type"),
            ["type_text"]           = new SanitizationRule("typed-text", "type"),
            ["press_key"]           = new SanitizationRule("combo", "press_key"),
            ["key"]                 = new SanitizationRule("combo", "key"),
            ["hotkey"]              = new SanitizationRule("combo", "hotkey"),

            // ── app / window management ──────────────────────────────────
            ["launch_app"]          = new SanitizationRule("app-target", "launch_app"),
            ["launch_path"]         = new SanitizationRule("app-target", "launch_path"),
            ["focus_app"]           = new SanitizationRule("app-target", "focus_app"),
            ["focus_window"]        = new SanitizationRule("app-target", "focus_window"),
        };

    private sealed record SanitizationRule(string Requires, string DisplayName);

    /// <summary>
    /// Inspect the action and, if it's a vague screen target, return a
    /// replacement `screenshot` action plus a feedback line. Otherwise
    /// return (null, null).
    /// </summary>
    public static (AgentAction? Replacement, string? Feedback) Sanitize(AgentAction action)
    {
        var tool = action.Action?.Trim() ?? "";
        if (!_rules.TryGetValue(tool, out var rule))
            return (null, null);

        bool hasNeededArg = rule.Requires switch
        {
            "description" => HasDescription(action),
            "coord"       => HasCoords(action, tool),
            "typed-text"  => HasTypedText(action, tool),
            "combo"       => HasCombo(action),
            "app-target"  => HasAppTarget(action),
            _             => true,
        };

        if (hasNeededArg) return (null, null);

        // Action is too vague to dispatch — coerce to a forced screenshot.
        var screenshot = MakeScreenshotAction();
        var feedback =
            $"SANITIZED: action '{tool}' had no usable target " +
            $"(needs {rule.Requires}) — replaced with `screenshot`. " +
            $"The next turn you should re-locate the target on the FRESH screenshot " +
            $"using a `description`. Don't emit screen-target actions without one of: " +
            $"a `description` for grounding, or `x` / `y` numeric coords.";
        return (screenshot, feedback);
    }

    private static bool HasDescription(AgentAction action)
    {
        var desc = action.GetString("description") ?? "";
        return !string.IsNullOrWhiteSpace(desc);
    }

    private static bool HasTypedText(AgentAction action, string tool)
    {
        // type and type_text both need a non-empty `text` parameter.
        var t = action.GetString("text");
        return !string.IsNullOrWhiteSpace(t);
    }

    private static bool HasCombo(AgentAction action)
    {
        // `keys` array (key/hotkey) OR `combo` string (press_key).
        var keys = action.Raw.ValueKind == JsonValueKind.Object
                   && action.Raw.TryGetProperty("keys", out var k)
                   && k.ValueKind == JsonValueKind.Array
                   && k.GetArrayLength() > 0;
        var combo = !string.IsNullOrWhiteSpace(action.GetString("combo"));
        return keys || combo;
    }

    private static bool HasAppTarget(AgentAction action)
    {
        // launch_app / launch_path → `executable` (string or absolute path).
        // focus_app / focus_window → `title` or `name`.
        return !string.IsNullOrWhiteSpace(action.GetString("executable"))
            || !string.IsNullOrWhiteSpace(action.GetString("name"))
            || !string.IsNullOrWhiteSpace(action.GetString("title"));
    }

    private static bool HasCoords(AgentAction action, string tool)
    {
        if (action.Raw.ValueKind != JsonValueKind.Object) return false;

        if (string.Equals(tool, "drag_xy", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadInt(action.Raw, "from_x", out _)
                && TryReadInt(action.Raw, "from_y", out _)
                && TryReadInt(action.Raw, "to_x", out _)
                && TryReadInt(action.Raw, "to_y", out _);
        }

        return TryReadInt(action.Raw, "x", out _)
            && TryReadInt(action.Raw, "y", out _);
    }

    private static bool TryReadInt(JsonElement obj, string property, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(property, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt32(out value);
        return false;
    }

    private static AgentAction MakeScreenshotAction()
    {
        // Bare-minimum valid JSON for an AgentAction — `{"action":"screenshot"}`.
        // Action is "screenshot" (already in BuildMethodDocs) and the
        // dispatcher returns the next-screenshot as a side effect of the
        // worker's normal capture flow, so this is essentially a no-op
        // marker that pays for itself by forcing the model to think
        // about what's on screen.
        var raw = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["action"] = "screenshot"
        });
        return new AgentAction { Action = "screenshot", Raw = raw };
    }
}
