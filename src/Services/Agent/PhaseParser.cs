// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/PhaseParser.cs
//
// Parses an ActionResult description ("1x left-click at (24, 959)") into the
// PhaseRecord data the structured AgentRunViewModel renders. Maps raw text
// into a PhaseKind enum + human Title/Subtitle so the DataTemplate can
// choose the right glyph and counter bucket without XAML branching on each
// string.
//
// Lives in Vantage.Services.Agent because both Worker.cs (which produces
// ActionResults) and the visualization layer consume this output.

using System.Text.RegularExpressions;
using Vantage.Models;

namespace Vantage.Services.Agent;

public static class PhaseParser
{
    private static readonly Regex ClickRx = new(
        @"^(?:(\d+)\s*[xX]\s*)?(left|right|middle|double)?-?click at \((\d+),\s*(\d+)\)(?:\s*[—-]\s*""(.+?)""\s*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PhaseRecord Parse(int step, ActionResult result)
    {
        var desc = result.Description ?? "(no description)";
        var (kind, title, subtitle) = Classify(desc);
        // Counter glyph: prefer the counter-bucket glyph (the one shown in
        // the pill) but fall back to the kind itself.
        var counter = ForCounter(kind) ?? kind;
        var glyph = AgentRunViewModel.CounterGlyph(counter);
        return new PhaseRecord
        {
            Index = step,
            Kind = kind,
            Counter = ForCounter(kind),
            IconGlyph = glyph,
            Title = title,
            Subtitle = subtitle,
            StartedAt = DateTimeOffset.UtcNow,
        };
    }

    private static (PhaseKind kind, string title, string? subtitle) Classify(string desc)
    {
        var d = desc.Trim();

        // Click family — "1x left-click at (24, 959) — "the Start""
        var m = ClickRx.Match(d);
        if (m.Success)
        {
            var n = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : "1";
            var btn = m.Groups[2].Value;
            if (string.IsNullOrEmpty(btn)) btn = "left";
            var coord = m.Groups[3].Value + "," + m.Groups[4].Value;
            var target = m.Groups[5].Value;
            var suffix = n == "1" ? "" : n + "× ";
            var kind = btn switch
            {
                "right"   => PhaseKind.RightClick,
                "middle"  => PhaseKind.Click,
                "double"  => PhaseKind.DoubleClick,
                _         => PhaseKind.Click,
            };
            var pretty = btn switch
            {
                "right"  => "Right-click",
                "middle" => "Middle-click",
                "double" => "Double-click",
                _        => "Click",
            };
            var detail = string.IsNullOrEmpty(target)
                ? $"at ({coord})"
                : $"\"{target}\" · at ({coord})";
            return (kind, $"{suffix}{pretty}", detail);
        }

        // Suffix extracted via shared helpers — strip the verb (everything
        // before the first space) and trim the remainder in one pass. Each
        // match path used to do three allocations (IndexOf + Substring +
        // Trim); the TailAfterSpace helper does one. Across 50 steps with
        // multiple action matches per step that's hundreds of saved
        // allocations per run.

        // Pressed ctrl+s / Pressed Win+E
        if (StartsWithAnyWord(d, "Pressed"))
            return (PhaseKind.Key, "Press", TailAfterSpace(d));
        if (d.StartsWith("pressed combo ", StringComparison.OrdinalIgnoreCase))
            return (PhaseKind.Key, "Press", TailAfterOffset(d, "pressed combo ".Length));

        // Typed "hello world"
        if (StartsWithAnyWord(d, "Typed"))
            return (PhaseKind.Type, "Type", TailAfterSpace(d).Trim('"'));

        // Waited 1.2s
        if (StartsWithAnyWord(d, "Waited", "Wait"))
            return (PhaseKind.Wait, "Wait", TailAfterSpace(d));

        // Scrolled up 1 notch
        if (StartsWithAnyWord(d, "Scrolled"))
            return (PhaseKind.Scroll, "Scroll", TailAfterSpace(d));

        // Moved cursor to (24, 959)
        if (StartsWithAnyWord(d, "Moved"))
            return (PhaseKind.MoveMouse, "Move", TailAfterSpace(d));

        if (StartsWithAnyWord(d, "Launched"))
            return (PhaseKind.LaunchApp, "Launch", TailAfterSpace(d));

        if (StartsWithAnyWord(d, "Focused", "Activated"))
            return (PhaseKind.FocusApp, "Focus", TailAfterSpace(d));

        if (StartsWithAnyWord(d, "Closed"))
            return (PhaseKind.CloseApp, "Close", TailAfterSpace(d));

        if (StartsWithAnyWord(d, "Killed"))
            return (PhaseKind.KillProcess, "Kill", TailAfterSpace(d));

        if (StartsWithAnyWord(d, "Listed", "Found"))
            return (PhaseKind.ListProcesses, "List", TailAfterSpace(d));

        // PowerShell output — capture first line as subtitle.
        if (StartsWithAnyWord(d, "Ran", "Executed"))
        {
            var rest = TailAfterSpace(d);
            var title = rest.StartsWith("PowerShell", StringComparison.OrdinalIgnoreCase)
                ? "PowerShell"
                : rest.StartsWith("script", StringComparison.OrdinalIgnoreCase) ? "Script" : "Shell";
            return (PhaseKind.RunPowerShell, title, rest);
        }

            // Fallback: an action without a recognized prefix. Try to
            // surface *something* useful so the UI never shows the unhelpful
            // "—" placeholder. If d is empty or whitespace, mark as
            // malformed so the user can see at a glance the model emitted
            // a bad action (rather than the parser failing silently).
            var fallbackTitle = string.IsNullOrWhiteSpace(d) ? "(malformed action)" : "Other";
            var fallbackSub = string.IsNullOrWhiteSpace(d) ? "agent emitted an empty action; see agent-debug.log" : d;
            return (PhaseKind.Other, fallbackTitle, fallbackSub);
    }

    /// <summary>Case-insensitive word-prefix match. Mirrors a
    /// <c>StartsWith(<paramref name="d"/> "<paramref name="w"/> ")</c> —
    /// i.e. "Pressed" matches "Pressed ctrl+s" but NOT "PressedBold".
    /// Used to classify action descriptions consistently.</summary>
    private static bool StartsWithAnyWord(string d, params string[] words)
    {
        foreach (var w in words)
        {
            if (d.Length >= w.Length
                && d.StartsWith(w, StringComparison.OrdinalIgnoreCase)
                && (d.Length == w.Length || d[w.Length] == ' '))
                return true;
        }
        return false;
    }

    /// <summary>Return everything after the first space, trimmed. Common
    /// pattern in Classify: parse "Pressed ctrl+s" → "ctrl+s". One
    /// allocation instead of the IndexOf + Substring + Trim trio the
    /// pattern required previously.</summary>
    private static string TailAfterSpace(string d)
    {
        var i = d.IndexOf(' ');
        if (i < 0) return string.Empty;
        var span = d.AsSpan(i + 1).Trim();
        return span.IsEmpty ? string.Empty : span.ToString();
    }

    /// <summary>Tail-after-fixed-offset variant — e.g. "pressed combo ".</summary>
    private static string TailAfterOffset(string d, int offset)
    {
        if (d.Length <= offset) return string.Empty;
        var span = d.AsSpan(offset).Trim();
        return span.IsEmpty ? string.Empty : span.ToString();
    }

    /// <summary>
    /// Maps a PhaseKind to its Counter bucket. Some kinds roll up into the
    /// same counter (e.g. RightClick / DoubleClick / Click all count as
    /// "clicks"), but each retains its own PhaseKind so the stepper shows
    /// the precise icon.
    /// </summary>
    public static PhaseKind? ForCounter(PhaseKind k) => k switch
    {
        PhaseKind.Click       => PhaseKind.Click,
        PhaseKind.RightClick  => PhaseKind.Click,
        PhaseKind.DoubleClick => PhaseKind.Click,
        PhaseKind.Type        => PhaseKind.Type,
        PhaseKind.Key         => PhaseKind.Key,
        PhaseKind.Scroll      => PhaseKind.Scroll,
        PhaseKind.Wait        => PhaseKind.Wait,
        PhaseKind.MoveMouse   => PhaseKind.MoveMouse,
        PhaseKind.LaunchApp   => PhaseKind.LaunchApp,
        PhaseKind.FocusApp    => PhaseKind.FocusApp,
        PhaseKind.CloseApp    => PhaseKind.CloseApp,
        PhaseKind.KillProcess => PhaseKind.KillProcess,
        PhaseKind.ListProcesses => PhaseKind.ListProcesses,
        PhaseKind.RunPowerShell => PhaseKind.RunPowerShell,
        _ => null,
    };
}
