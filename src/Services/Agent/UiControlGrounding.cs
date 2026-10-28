// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/UiControlGrounding.cs
//
// Resolves a target description such as "the Windows Update option in the
// left sidebar" to a control of the foreground window using the Windows
// accessibility tree, before any vision model is asked for coordinates.
// A control match is exact (the centre of the control's bounds), takes no
// model round trip, and is reused as the per-turn control list the planner
// sees. The vision grounding model remains the fallback when nothing matches.

using System.Diagnostics;
using System.Text;
using Microsoft.UIAutomation;

namespace Vantage.Services.Agent;

internal sealed record UiControl(
    string Name,
    string Role,
    bool Enabled,
    bool Offscreen,
    int Left,
    int Top,
    int Width,
    int Height)
{
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

internal static class UiControlGrounding
{
    private const int MaxControls = 160;
    private const int MinNameLength = 2;

    // Roles a person (or the model) would name when describing something to
    // click or fill. Panes, groups, plain text and images are layout, not targets.
    private static readonly HashSet<string> TargetRoles = new(StringComparer.Ordinal)
    {
        "button", "checkbox", "combobox", "edit", "hyperlink", "list-item",
        "menu-item", "radio-button", "tab-item", "tree-item", "data-item", "slider",
    };

    // Words in a description that hint at a role, so "the Save button" prefers
    // a button over a same-named label.
    private static readonly (string Role, string[] Words)[] RoleWords =
    {
        ("button", new[] { "button", "btn" }),
        ("hyperlink", new[] { "link", "hyperlink" }),
        ("tab-item", new[] { "tab" }),
        ("menu-item", new[] { "menu", "menu item" }),
        ("checkbox", new[] { "checkbox", "check box", "toggle" }),
        ("radio-button", new[] { "radio" }),
        ("edit", new[] { "box", "field", "input", "bar", "textbox" }),
        ("combobox", new[] { "dropdown", "drop down", "combo", "selector" }),
        ("list-item", new[] { "option", "item", "entry", "row", "sidebar", "navigation" }),
        ("tree-item", new[] { "folder", "node" }),
    };

    public static async Task<IReadOnlyList<UiControl>> SnapshotAsync(IntPtr windowHandle, TimeSpan budget)
    {
        if (windowHandle == IntPtr.Zero) return Array.Empty<UiControl>();
        using var cts = new CancellationTokenSource(budget);
        try
        {
            return await Task.Run(() => Snapshot(windowHandle, cts.Token), cts.Token)
                .WaitAsync(budget + TimeSpan.FromMilliseconds(150))
                .ConfigureAwait(false);
        }
        catch
        {
            // Accessibility is an accelerator. A slow or broken provider must
            // never delay or fail the step; the vision path still works.
            return Array.Empty<UiControl>();
        }
    }

    private static IReadOnlyList<UiControl> Snapshot(IntPtr windowHandle, CancellationToken ct)
    {
        var controls = new List<UiControl>();
        IUIAutomation? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? found = null;
        try
        {
            automation = new CUIAutomation();
            root = automation.ElementFromHandle(windowHandle);
            condition = automation.CreateTrueCondition();
            found = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            var length = found.Length;
            for (var i = 0; i < length && controls.Count < MaxControls; i++)
            {
                if (ct.IsCancellationRequested) break;
                IUIAutomationElement? element = null;
                try
                {
                    element = found.GetElement(i);
                    var role = ControlRole(element.CurrentControlType);
                    if (!TargetRoles.Contains(role)) continue;
                    var name = (element.CurrentName ?? string.Empty).Trim();
                    if (name.Length < MinNameLength) continue;
                    var rect = element.CurrentBoundingRectangle;
                    var width = Math.Max(0, rect.right - rect.left);
                    var height = Math.Max(0, rect.bottom - rect.top);
                    if (width == 0 || height == 0) continue;
                    controls.Add(new UiControl(
                        name,
                        role,
                        element.CurrentIsEnabled != 0,
                        element.CurrentIsOffscreen != 0,
                        rect.left,
                        rect.top,
                        width,
                        height));
                }
                catch
                {
                    // UIA trees are live; an element can vanish mid-enumeration.
                }
                finally
                {
                    Release(element);
                }
            }
        }
        finally
        {
            Release(found);
            Release(condition);
            Release(root);
            Release(automation);
        }
        return controls;
    }

    /// <summary>
    /// Returns the single control that best fits the description, or null when
    /// nothing fits or the best candidates cannot be told apart.
    /// </summary>
    public static UiControl? Match(string description, IReadOnlyList<UiControl> controls)
    {
        var wanted = Normalize(description);
        if (wanted.Length == 0 || controls.Count == 0) return null;
        var padded = " " + wanted + " ";

        var scored = new List<(UiControl Control, double Score)>();
        foreach (var control in controls)
        {
            if (!control.Enabled || control.Offscreen) continue;
            var name = Normalize(control.Name);
            if (name.Length < MinNameLength) continue;

            double score;
            if (string.Equals(name, wanted, StringComparison.Ordinal))
            {
                score = 100 + name.Length;
            }
            else if (padded.Contains(" " + name + " ", StringComparison.Ordinal))
            {
                score = name.Length;
            }
            else
            {
                continue;
            }

            if (RoleMentioned(control.Role, padded)) score += 3;
            scored.Add((control, score));
        }
        if (scored.Count == 0) return null;

        var best = scored.Max(s => s.Score);
        var top = scored.Where(s => s.Score >= best - 0.001).Select(s => s.Control).ToList();
        if (top.Count == 1) return top[0];

        // Several controls share the winning name (a sidebar entry and a card
        // with the same label are common). Let the words in the description
        // that name a position decide, and refuse to guess if they cannot.
        var ranked = top
            .Select(c => (Control: c, Bias: PositionBias(padded, c, controls)))
            .OrderByDescending(x => x.Bias)
            .ToList();
        if (ranked[0].Bias <= 0) return null;
        if (ranked.Count > 1 && ranked[0].Bias - ranked[1].Bias < 0.15) return null;
        return ranked[0].Control;
    }

    private static double PositionBias(string padded, UiControl candidate, IReadOnlyList<UiControl> all)
    {
        var minX = all.Min(c => c.CenterX);
        var maxX = all.Max(c => c.CenterX);
        var minY = all.Min(c => c.CenterY);
        var maxY = all.Max(c => c.CenterY);
        double Norm(int v, int lo, int hi) => hi > lo ? (double)(v - lo) / (hi - lo) : 0.5;
        var nx = Norm(candidate.CenterX, minX, maxX);
        var ny = Norm(candidate.CenterY, minY, maxY);

        double bias = 0;
        if (Has(padded, "left") || Has(padded, "sidebar") || Has(padded, "navigation")) bias += 1 - nx;
        if (Has(padded, "right")) bias += nx;
        if (Has(padded, "top") || Has(padded, "header")) bias += 1 - ny;
        if (Has(padded, "bottom") || Has(padded, "footer")) bias += ny;
        return bias;
    }

    private static bool Has(string padded, string word) => padded.Contains(" " + word + " ", StringComparison.Ordinal);

    private static bool RoleMentioned(string role, string padded)
    {
        foreach (var (candidateRole, words) in RoleWords)
        {
            if (candidateRole != role) continue;
            foreach (var word in words)
                if (padded.Contains(" " + word + " ", StringComparison.Ordinal) || padded.Contains(" " + word + "s ", StringComparison.Ordinal))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Compact list of what is clickable in the foreground window, for the
    /// planner. Names are what <see cref="Match"/> looks for, so a model that
    /// repeats one in its description is resolved without a vision call.
    /// </summary>
    public static string BuildPromptBlock(
        IReadOnlyList<UiControl> controls,
        string windowTitle,
        int monitorWidth,
        int monitorHeight,
        double scale,
        int maxEntries = 40)
    {
        var usable = controls.Where(c => c.Enabled && !c.Offscreen).ToList();
        if (usable.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("FOREGROUND_CONTROLS (window \"").Append(OneLine(windowTitle, 60))
          .AppendLine("\"; copy a control's exact name into `description` to click it with no guessing):");
        foreach (var control in usable.Take(maxEntries))
        {
            var cx = control.CenterX / Math.Max(scale, 0.01);
            var cy = control.CenterY / Math.Max(scale, 0.01);
            sb.Append("  \"").Append(OneLine(control.Name, 60)).Append("\" ")
              .Append(control.Role).Append(" @")
              .Append(Region(cy, monitorHeight, "top", "middle", "bottom")).Append('-')
              .AppendLine(Region(cx, monitorWidth, "left", "center", "right"));
        }
        if (usable.Count > maxEntries)
            sb.Append("  … ").Append(usable.Count - maxEntries).AppendLine(" more not listed");
        return sb.ToString().TrimEnd();
    }

    private static string Region(double value, int extent, string low, string mid, string high)
    {
        if (extent <= 0) return mid;
        var ratio = value / extent;
        return ratio < 0.34 ? low : ratio > 0.66 ? high : mid;
    }

    internal static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static string OneLine(string value, int max)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max] + "...";
    }

    private static string ControlRole(int id) => id switch
    {
        50000 => "button", 50002 => "checkbox", 50003 => "combobox",
        50004 => "edit", 50005 => "hyperlink", 50007 => "list-item",
        50011 => "menu-item", 50013 => "radio-button", 50015 => "slider",
        50019 => "tab-item", 50024 => "tree-item", 50029 => "data-item",
        _ => "other",
    };

    private static void Release(object? value)
    {
        try
        {
            if (value is not null && System.Runtime.InteropServices.Marshal.IsComObject(value))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(value);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Which actions may skip the before/after check. Anything whose target came
/// from a guess (a description resolved by a model, or coordinates the model
/// estimated) must be verified, otherwise a click that lands on nothing is
/// recorded as progress and repeated indefinitely.
/// </summary>
internal static class FastPathPolicy
{
    public static bool IsEligible(AgentAction action) => IsEligible(action, depth: 0);

    private static bool IsEligible(AgentAction action, int depth)
    {
        var name = action.Action.Trim().ToLowerInvariant();
        switch (name)
        {
            case "batch":
                if (depth > 0) return false;
                var steps = action.GetActionList("steps");
                return steps.Count > 0 && steps.All(step => IsEligible(step, depth + 1));

            // Typing into whatever already has focus is deterministic;
            // typing that first has to find a field by description is not.
            case "type":
                return action.GetPoint() is null && string.IsNullOrWhiteSpace(action.GetString("description"));

            case "type_text" or "type_window_text" or "set_value"
                or "key" or "press_key" or "press_window_key" or "click_element"
                or "launch_app" or "focus_app" or "resize_app" or "close_app"
                or "run_powershell" or "done" or "wait" or "vantage_set_clipboard":
                return true;

            default:
                return false;
        }
    }
}
