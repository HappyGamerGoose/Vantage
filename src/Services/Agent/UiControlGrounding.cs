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
    private const int MinPrefixLength = 4;
    private const string TabRole = "tab-item";

    private const int BoundingRectanglePropertyId = 30001;
    private const int ControlTypePropertyId = 30003;
    private const int NamePropertyId = 30005;
    private const int IsEnabledPropertyId = 30010;
    private const int IsOffscreenPropertyId = 30022;

    private static readonly int[] TargetControlTypes =
    {
        50000, 50002, 50003, 50004, 50005, 50007, 50011, 50013, 50015, 50019, 50024, 50029,
    };

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
        ("combobox", new[] { "combobox", "dropdown", "drop down", "combo", "selector" }),
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
        IUIAutomationCacheRequest? cache = null;
        IUIAutomationElementArray? found = null;
        var conditions = new List<object>();
        try
        {
            automation = new CUIAutomation();
            root = automation.ElementFromHandle(windowHandle);

            // Large web pages have thousands of nodes, and reading each one's
            // properties separately ran past the time limit and returned nothing.
            // The provider filters here and returns all the fields in one call.
            cache = automation.CreateCacheRequest();
            cache.AddProperty(NamePropertyId);
            cache.AddProperty(ControlTypePropertyId);
            cache.AddProperty(IsEnabledPropertyId);
            cache.AddProperty(IsOffscreenPropertyId);
            cache.AddProperty(BoundingRectanglePropertyId);

            IUIAutomationCondition? anyRole = null;
            foreach (var type in TargetControlTypes)
            {
                var one = automation.CreatePropertyCondition(ControlTypePropertyId, type);
                conditions.Add(one);
                anyRole = anyRole is null ? one : automation.CreateOrCondition(anyRole, one);
                conditions.Add(anyRole);
            }
            var onScreen = automation.CreatePropertyCondition(IsOffscreenPropertyId, false);
            conditions.Add(onScreen);
            condition = automation.CreateAndCondition(onScreen, anyRole!);

            found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, cache);
            var length = found.Length;
            // Page content comes before the tab strip, so tabs are read even past
            // the cap: on a long page the model could not see any tab, and a click
            // meant for one landed on a similarly named link in the page.
            for (var i = 0; i < length; i++)
            {
                if (ct.IsCancellationRequested) break;
                IUIAutomationElement? element = null;
                try
                {
                    element = found.GetElement(i);
                    var role = ControlRole(element.CachedControlType);
                    if (!TargetRoles.Contains(role)) continue;
                    if (controls.Count >= MaxControls && role != TabRole) continue;
                    var name = (element.CachedName ?? string.Empty).Trim();
                    if (name.Length < MinNameLength) continue;
                    var rect = element.CachedBoundingRectangle;
                    var width = Math.Max(0, rect.right - rect.left);
                    var height = Math.Max(0, rect.bottom - rect.top);
                    if (width == 0 || height == 0) continue;
                    controls.Add(new UiControl(
                        name,
                        role,
                        element.CachedIsEnabled != 0,
                        element.CachedIsOffscreen != 0,
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
            foreach (var part in conditions) Release(part);
            Release(cache);
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

        // "the Check for updates button at the top of the Windows Update page":
        // the target comes first, and what follows the preposition only says
        // where it is. A control named in that trailing part is not the target.
        var targetEnd = TargetPhraseEnd(padded);
        var core = TargetCore(padded[..targetEnd]);
        var requiredRoles = RolesNamedBy(padded[..targetEnd]);

        var scored = new List<(UiControl Control, double Score)>();
        foreach (var control in controls)
        {
            if (!control.Enabled || control.Offscreen) continue;
            if (requiredRoles is not null && !requiredRoles.Contains(control.Role)) continue;
            var name = Normalize(control.Name);
            if (name.Length < MinNameLength) continue;

            double score;
            var position = padded.IndexOf(" " + name + " ", StringComparison.Ordinal);
            if (string.Equals(name, wanted, StringComparison.Ordinal))
            {
                score = 100 + name.Length;
            }
            else if (position >= 0 && position < targetEnd)
            {
                score = name.Length;
            }
            else if (core.Length >= MinPrefixLength
                     && (name + " ").StartsWith(core + " ", StringComparison.Ordinal))
            {
                // "the Search box" for a control named "Search box, Find a setting".
                score = core.Length - 1;
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

    private static readonly string[] LocationWords =
    {
        " in ", " on ", " at ", " of ", " from ", " under ", " inside ", " within ", " near ",
        " next to ", " beside ", " below ", " above ", " to the ", " that ", " which ",
    };

    private static readonly string[] LeadingWords = { "the ", "a ", "an ", "this ", "that " };

    private static readonly string[] TrailingRoleWords =
    {
        " button", " link", " hyperlink", " tab", " option", " item", " entry", " icon",
        " menu", " list item", " checkbox", " check box", " toggle", " control",
    };

    // Web pages style links as buttons and the reverse, so those two share a group.
    private static readonly (string Word, string[] Roles)[] RoleNames =
    {
        (" tab", new[] { TabRole }),
        (" button", new[] { "button", "hyperlink", "menu-item" }),
        (" link", new[] { "hyperlink", "button" }),
        (" hyperlink", new[] { "hyperlink", "button" }),
        (" checkbox", new[] { "checkbox" }),
        (" check box", new[] { "checkbox" }),
    };

    /// <summary>
    /// When the target phrase ends by naming a kind of control, only that kind can
    /// be the target. "The Mount Everest tab" once matched a link with those words
    /// and replaced the page it was on.
    /// </summary>
    private static string[]? RolesNamedBy(string targetPhrase)
    {
        var phrase = targetPhrase.TrimEnd();
        foreach (var (word, roles) in RoleNames)
        {
            if (phrase.EndsWith(word, StringComparison.Ordinal)) return roles;
        }
        return null;
    }

    /// <summary>Index in <paramref name="padded"/> where the location clause starts.</summary>
    private static int TargetPhraseEnd(string padded)
    {
        var end = padded.Length;
        foreach (var word in LocationWords)
        {
            var index = padded.IndexOf(word, StringComparison.Ordinal);
            // A description that opens with its location names the target later.
            if (index > 0 && index < end) end = index;
        }
        return end;
    }

    // Words that say what kind of thing a target is, or where, but not which one.
    private static readonly HashSet<string> GenericTargetWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "this", "that", "my", "and", "or", "to", "for", "click", "open", "select", "press", "choose",
        "button", "btn", "link", "hyperlink", "tab", "menu", "item", "entry", "icon", "option", "control", "element",
        "field", "box", "input", "textbox", "bar", "dropdown", "drop", "down", "combo", "combobox", "selector",
        "checkbox", "check", "toggle", "radio", "list", "row", "result", "page", "window", "dialog", "pane", "panel",
        "section", "area", "first", "second", "third", "fourth", "fifth", "last", "next", "previous",
        "top", "bottom", "left", "right", "middle", "navigation", "sidebar", "main",
    };

    /// <summary>The text box a plain left click on a listed control lands in, if it is one.</summary>
    public static UiControl? TextFieldClicked(AgentAction action, IReadOnlyList<UiControl> controls)
    {
        if (!action.Action.Trim().Equals("click", StringComparison.OrdinalIgnoreCase)) return null;
        if (!(action.GetString("button") ?? "left").Equals("left", StringComparison.OrdinalIgnoreCase)) return null;
        if ((action.GetInt("num_clicks") ?? 1) != 1) return null;
        if (action.GetInt("control") is not { } number || number < 0 || number >= controls.Count) return null;
        return controls[number].Role.Equals("edit", StringComparison.OrdinalIgnoreCase) ? controls[number] : null;
    }

    /// <summary>The one action to send once a text box has the caret.</summary>
    public static string TypeIntoField(UiControl field, int number, string? windowTitle)
    {
        var text = SavedFileTitle.IsSaveDialog(windowTitle) ? "<the file's whole path>" : "<the text>";
        return $"The \"{field.Name}\" box has the caret now, and clicking it again changes nothing. " +
               $"Type into it next: {{\"action\":\"type\",\"control\":{number},\"text\":\"{text}\",\"overwrite\":true}}.";
    }

    /// <summary>
    /// True when a control number and the description sent with it name different
    /// things. A model once clicked "Save as type:" 146 times while describing it
    /// as Desktop: the dropdown opened and closed, so every click looked like
    /// progress. Only the target phrase counts, since "Desktop in the Save As
    /// dialog" shares words with that dropdown only through its location.
    /// </summary>
    public static bool DescriptionContradicts(string? description, UiControl control)
    {
        var padded = " " + Normalize(description ?? string.Empty) + " ";
        var wanted = padded[..TargetPhraseEnd(padded)]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !GenericTargetWords.Contains(word))
            .ToList();
        if (wanted.Count == 0) return false;

        var named = Normalize(control.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return !wanted.Any(word => named.Any(name => SameWord(word, name)));
    }

    // "Download" and "Downloads" are one word to a person.
    private static bool SameWord(string a, string b) =>
        a == b
        || (Math.Min(a.Length, b.Length) >= MinPrefixLength
            && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));

    private static string TargetCore(string phrase)
    {
        var core = phrase.Trim();
        foreach (var word in LeadingWords)
        {
            if (core.StartsWith(word, StringComparison.Ordinal))
            {
                core = core[word.Length..];
                break;
            }
        }
        foreach (var word in TrailingRoleWords)
        {
            if (core.EndsWith(word, StringComparison.Ordinal))
            {
                core = core[..^word.Length];
                break;
            }
        }
        return core;
    }

    /// <summary>Smallest control under a point, for tightening a vision estimate.</summary>
    public static UiControl? ControlAt(int physicalX, int physicalY, IReadOnlyList<UiControl> controls) =>
        controls
            .Where(c => c.Enabled && !c.Offscreen
                && physicalX >= c.Left && physicalX < c.Left + c.Width
                && physicalY >= c.Top && physicalY < c.Top + c.Height)
            .OrderBy(c => (long)c.Width * c.Height)
            .FirstOrDefault();

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
        int maxEntries = 60)
    {
        var usable = controls
            .Select((control, index) => (Control: control, Index: index))
            .Where(c => c.Control.Enabled && !c.Control.Offscreen && !IsWindowChrome(c.Control))
            .OrderBy(c => c.Control.Role == TabRole ? 0 : 1)
            .ToList();
        if (usable.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("FOREGROUND_CONTROLS (window \"").Append(OneLine(windowTitle, 60))
          .AppendLine("\"; click one exactly with {\"action\":\"click\",\"control\":N}, valid this turn only):");
        foreach (var (control, index) in usable.Take(maxEntries))
        {
            var cx = control.CenterX / Math.Max(scale, 0.01);
            var cy = control.CenterY / Math.Max(scale, 0.01);
            sb.Append("  [").Append(index).Append("] \"").Append(OneLine(control.Name, 60)).Append("\" ")
              .Append(control.Role).Append(" @")
              .Append(Region(cy, monitorHeight, "top", "middle", "bottom")).Append('-')
              .AppendLine(Region(cx, monitorWidth, "left", "center", "right"));
        }
        if (usable.Count > maxEntries)
            sb.Append("  … ").Append(usable.Count - maxEntries).AppendLine(" more not listed");
        return sb.ToString().TrimEnd();
    }

    // The caption buttons are the same in every window and close_app covers
    // the one that matters; listing them only costs tokens.
    private static bool IsWindowChrome(UiControl control)
    {
        if (control.Role != "button") return false;
        var name = control.Name;
        return name.StartsWith("Minimi", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Maximi", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Restore", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Close", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("Close ", StringComparison.OrdinalIgnoreCase) && !name.Equals("Close tab", StringComparison.OrdinalIgnoreCase));
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

/// <summary>How a vision model expresses the coordinates it returns.</summary>
internal enum CoordinateConvention { ImagePixels, Permille }

internal static class CoordinateCalibration
{
    // Many vision models answer on a 0-1000 grid even when asked for pixels,
    // which misplaces every click. A control's true position is known from the
    // accessibility tree, so one measured answer shows which grid the model uses.
    public static CoordinateConvention Choose(
        int returnedX, int returnedY, double expectedX, double expectedY, int imageWidth, int imageHeight)
    {
        if ((returnedX > imageWidth || returnedY > imageHeight) && returnedX <= 1000 && returnedY <= 1000)
            return CoordinateConvention.Permille;

        var pixelError = Distance(returnedX, returnedY, expectedX, expectedY);
        var permille = ToImage(CoordinateConvention.Permille, returnedX, returnedY, imageWidth, imageHeight);
        var permilleError = Distance(permille.X, permille.Y, expectedX, expectedY);
        return permilleError < pixelError * 0.6 ? CoordinateConvention.Permille : CoordinateConvention.ImagePixels;
    }

    public static (double X, double Y) ToImage(CoordinateConvention convention, int x, int y, int imageWidth, int imageHeight) =>
        convention == CoordinateConvention.Permille
            ? (x * imageWidth / 1000.0, y * imageHeight / 1000.0)
            : (x, y);

    private static double Distance(double ax, double ay, double bx, double by) =>
        Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
}

internal static class BatchPolicy
{
    /// <summary>
    /// A `done` after state-changing steps is a claim nobody has checked, since
    /// those steps ran after the model's last look at the screen. Only steps
    /// that cannot change anything may precede a real `done`.
    /// </summary>
    public static bool DoneNeedsAFreshLook(IEnumerable<AgentAction> stepsBeforeDone) =>
        stepsBeforeDone.Any(step => step.Action.Trim().ToLowerInvariant() is not ("wait" or "screenshot"));
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
