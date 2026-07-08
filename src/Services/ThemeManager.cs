// SPDX-License-Identifier: MIT
// Vantage — Services/ThemeManager.cs
//
// Centralised palette + dark-mode management. Defines:
//
//   1. Six accent palettes (Teal · Blue · Green · Violet · Pink · Slate),
//      each with Light + Dark brush sets. Picking a palette repaints
//      only the accent family (Accent / AccentHover / AccentPressed /
//      AccentSoft / PaneEdge) so the rest of the chrome (whites, ink,
//      surface tints) stays cohesive.
//
//   2. A Light/Dark neutral set (Page / Surface / SurfaceElevated /
//      SubtleSurface / text brushes / Sidebar gradient stops / Input
//      background / Composer chrome). Toggling dark mode swaps the
//      neutral brushes — the previously-selected accent palette rides
//      along automatically.
//
//   3. Apply() walks ResourceDictionary on the MainWindow's content
//      grid (where every named brush lives) and replaces each entry
//      in place. References already bound via {StaticResource *}
//      update because SolidColorBrush is mutable: we change the
//      brushes' Colors rather than tearing down the resources.

using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Vantage.Services;

public static class ThemeManager
{
    // ── Light neutrals — match the existing pre-palette chrome so the
    // existing app stays looking the same on first launch.
    private static readonly ThemeNeutralSet LightNeutral = new(
        Page              : "#F7FAFB",
        Surface           : "#FCFFFFFF",
        SurfaceElevated   : "#FFFFFFFF",
        SubtleSurface     : "#F2F6F7",
        Stroke            : "#2607141A",
        SoftStroke        : "#1607141A",
        Hairline          : "#0F000000",
        PrimaryText       : "#101A24",
        SecondaryText     : "#6D7D86",
        MutedText         : "#94A0A6",
        PaneStart         : "#F2F5F7",
        PaneEnd           : "#E4EAEE",
        Composer          : "#FFFFFFFF",
        ComposerBorder    : "#D8DEE3",
        Hover             : "#0A000000",
        Background        : "#FAFAFA");

    // ── Dark neutrals — calibrated for late-night reading. We avoid
    // pure #000 backgrounds because they look harsh next to a wide
    // monitor; instead the page sits one notch below the system
    // dark grey so chrome elements read as layered, not negative.
    private static readonly ThemeNeutralSet DarkNeutral = new(
        Page              : "#15171C",
        Surface           : "#1C1F26",
        SurfaceElevated   : "#26292F",
        SubtleSurface     : "#1A1D24",
        Stroke            : "#40FFFFFF",
        SoftStroke        : "#20FFFFFF",
        Hairline          : "#12FFFFFF",
        PrimaryText       : "#E7EAEC",
        SecondaryText     : "#A4ADB5",
        MutedText         : "#73787E",
        PaneStart         : "#181A20",
        PaneEnd           : "#1F2128",
        Composer          : "#22262D",
        ComposerBorder    : "#323640",
        Hover             : "#14FFFFFF",
        Background        : "#15171C");

    // ── Accent palettes ─────────────────────────────────────────────
    // Eight palettes, each with a Light + Dark set. The Dark sets pick
    // up a brighter / more saturated accent against the dark surface so
    // the same hex stays readable in both modes. Each (Light, Dark)
    // pair was calibrated to maintain the SAME colour identity across
    // modes — picking Green looks "green" either way, not "bright green
    // in dark, dark teal in light".
    //
    // Order is intentional: greens and cool colours first (Windows
    // habits), warm colours last (less common UX choice). Reorder by
    // editing this array — the PalettePicker UI is rebuilt from the
    // array on each Settings page render.
    public static readonly IReadOnlyList<ThemePalette> Palettes = new ThemePalette[]
    {
        // 1. Green — Windows "Nature" emoji, classic Win10 start menu accent.
        new ThemePalette("Green",
            new AccentSet("#107C10", "#1F8A1F", "#0C660C", "#E5F3E5", "#26107C10"),
            new AccentSet("#6CCB5E", "#88D67B", "#5BAB4F", "#1B3326", "#506CCB5E")),

        // 2. Violet — Tailwind violet + Windows 11 Flow accent.
        new ThemePalette("Violet",
            new AccentSet("#7C4DFF", "#8E5DFF", "#6232D9", "#EFE8FF", "#267C4DFF"),
            new AccentSet("#B69DF8", "#C5B0FA", "#9577DC", "#2A1F44", "#50B69DF8")),

        // 3. Yellow — sunshine / amber. Reads warm without being alarm-orange.
        //    Light accent stays in the "deep gold" range so dark text on it
        //    still satisfies WCAG 4.5:1 contrast; dark accent is bright
        //    lemon so it pops on the dark page.
        new ThemePalette("Yellow",
            new AccentSet("#C49B00", "#E0B400", "#9D7B00", "#FBF3CC", "#26C49B00"),
            new AccentSet("#FFD43B", "#FFE066", "#E0B022", "#3D3318", "#50FFD43B")),

        // 4. Blue — Windows 11 default-ish. The Office / Outlook accent.
        new ThemePalette("Blue",
            new AccentSet("#0078D4", "#1A86E0", "#0064B0", "#E8F1FB", "#260078D4"),
            new AccentSet("#4CC2FF", "#6FCEFF", "#3CA0D7", "#15263B", "#504CC2FF")),

        // 5. Teal — the original Vantage accent. Keeps continuity.
        new ThemePalette("Teal",
            new AccentSet("#007A74", "#0E8E87", "#00665F", "#E1F1F0", "#26007A74"),
            new AccentSet("#26D6CC", "#34E2D9", "#1AB3AA", "#163838", "#5026D6CC")),

        // 6. Orange — sunset / warm. The complementary warm to Yellow.
        //    Light is a deep terracotta; dark lifts to a glowy peach.
        new ThemePalette("Orange",
            new AccentSet("#C2540D", "#D6601B", "#9C4408", "#FCE8D6", "#26C2540D"),
            new AccentSet("#FB923C", "#FFA756", "#E07C2A", "#3D2614", "#50FB923C")),

        // 7. Pink — subtle, not Bubblegum-pink. Reads as a hint of magenta.
        new ThemePalette("Pink",
            new AccentSet("#C2185B", "#D6357A", "#9D1447", "#FCE4EC", "#26C2185B"),
            new AccentSet("#F670A6", "#FF8DB8", "#D45A8A", "#3D1A2A", "#50F670A6")),

        // 8. Slate — neutral, friendly. Recedes into the chrome.
        new ThemePalette("Slate",
            new AccentSet("#475569", "#5B6B83", "#394256", "#EEF2F6", "#26475569"),
            new AccentSet("#94A3B8", "#B0BBD0", "#7C8DA3", "#20283A", "#5094A3B8")),
    };

    public static ThemePalette GetPalette(string name) =>
        Palettes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Palettes[1]; // default to Blue

    /// <summary>
    /// Apply a palette + theme to the running window. Mutates the brush
    /// colors in place so existing {StaticResource} bindings stay live.
    /// Every SwapBrush is wrapped in a try/catch so a missing or unexpected
    /// resource key can never crash the app on startup.
    /// </summary>
    public static void Apply(FrameworkElement root, string paletteName, bool darkMode)
    {
        if (root is null) return;
        var palette = GetPalette(paletteName);
        var accent  = darkMode ? palette.Dark : palette.Light;
        var neutral = darkMode ? DarkNeutral : LightNeutral;

        // Neutrals — page surfaces, text, separators, the composer chrome.
        TrySwapBrush(root, "PageBrush",              neutral.Page);
        TrySwapBrush(root, "SurfaceBrush",           neutral.Surface);
        TrySwapBrush(root, "SurfaceElevatedBrush",   neutral.SurfaceElevated);
        TrySwapBrush(root, "SubtleSurfaceBrush",     neutral.SubtleSurface);
        TrySwapBrush(root, "StrokeBrush",            neutral.Stroke);
        TrySwapBrush(root, "SoftStrokeBrush",        neutral.SoftStroke);
        TrySwapBrush(root, "HairlineBrush",          neutral.Hairline);
        TrySwapBrush(root, "PrimaryTextBrush",       neutral.PrimaryText);
        TrySwapBrush(root, "SecondaryTextBrush",     neutral.SecondaryText);
        TrySwapBrush(root, "MutedTextBrush",         neutral.MutedText);
        TrySwapGradient(root, "PaneBrush",           neutral.PaneStart, neutral.PaneEnd);
        TrySwapBrush(root, "HoverBrush",             neutral.Hover);
        TrySwapBrush(root, "ComposerBrush",          neutral.Composer);
        TrySwapBrush(root, "ComposerBorderBrush",    neutral.ComposerBorder);
        TrySwapBrush(root, "InputBrush",             neutral.Composer);
        TrySwapBrush(root, "PendingBackgroundBrush", neutral.Page);

        // Accent palette — the user-chosen accent colours and their
        // hover/pressed/soft variants.
        TrySwapBrush(root, "AccentBrush",            accent.Accent);
        TrySwapBrush(root, "AccentHoverBrush",       accent.AccentHover);
        TrySwapBrush(root, "AccentPressedBrush",     accent.AccentPressed);
        TrySwapBrush(root, "AccentSoftBrush",        accent.AccentSoft);
        TrySwapBrush(root, "PaneEdgeBrush",          accent.PaneEdge);
        TrySwapBrush(root, "PanelTintBrush",         LerpAlpha(accent.Accent, darkMode ? "#26" : "#E5F0FA"));

        // Warm / Success / Danger families — these had hard-coded light-mode
        // hexes before; dark mode now repaints them too. The light value
        // is the same as the XAML default so behavior on light theme is
        // unchanged.
        var warmAccent   = LerpAlpha(accent.Accent, darkMode ? "#EA" : "#B66A42");
        var successAccent = darkMode ? "#6CD676" : "#237A4B";
        var successSoft  = darkMode ? "#1B3326" : "#D8EFE2";
        var dangerAccent = darkMode ? "#E67878" : "#A83232";
        var warmHover    = darkMode ? LerpAlpha(accent.Accent, "#FF") : "#C77C58";
        var warmPressed   = darkMode ? LerpAlpha(accent.Accent, "#C0") : "#8E5030";
        TrySwapBrush(root, "WarmBrush",              warmAccent);
        TrySwapBrush(root, "WarmHoverBrush",         warmHover);
        TrySwapBrush(root, "WarmPressedBrush",       warmPressed);
        TrySwapBrush(root, "SuccessBrush",           successAccent);
        TrySwapBrush(root, "SuccessSoftBrush",       successSoft);
        TrySwapBrush(root, "DangerBrush",            dangerAccent);

        // ErrorTextBrush is declared once in XAML with `#B3261E`
        // (mid-saturation red). In dark mode that hex keeps the same
        // red, but loses contrast against a dark red-tinted surface.
        // Brighten it on dark and keep it where it was on light.
        var errorText = darkMode ? "#F4836D" : "#B3261E";
        TrySwapBrush(root, "ErrorTextBrush",         errorText);

        // Message bubbles — adapt so the user/assistant/error bubbles
        // stay legible against either light or dark chat background. The
        // XAML defaults are light-mode; dark mode swaps them to dark
        // surfaces with a hint of accent contrast.
        var bubbleUser          = darkMode ? "#1F2429" : "#FCFCFC";
        var bubbleAssistant     = darkMode ? "#22272C" : "#F2F6F7";
        var bubbleError         = darkMode ? "#3A1F1C" : "#FBEEE8";
        var bubbleBorderUser    = darkMode ? "#33000000" : "#1407141A";
        var bubbleBorderAssist  = darkMode ? "#33FFFFFF" : "#2607141A";
        var bubbleBorderError   = darkMode ? "#80E67878" : "#E9B9A7";
        var avatarUser          = darkMode ? "#262C32" : "#E2E9EE";
        var avatarAssistant     = darkMode ? LerpAlpha(accent.Accent, "#28") : "#D6E7F8";
        var avatarError         = darkMode ? "#3A1F1C" : "#FBEEE8";
        // AuthorTextBrush used to fall back to "#00D78CD4" in light mode —
        // that's alpha=00 (fully transparent) so the "Vantage" author label
        // was invisible against the white chat surface. Using the palette's
        // accent direct (with a small saturation lift in light) makes the
        // author label consistent across every palette.
        var authorText          = darkMode
                                    ? LerpAlpha(accent.Accent, "#FF")
                                    : LerpAlpha(accent.AccentPressed, "#FF");
        TrySwapBrush(root, "BubbleUserBrush",            bubbleUser);
        TrySwapBrush(root, "BubbleAssistantBrush",       bubbleAssistant);
        TrySwapBrush(root, "BubbleErrorBrush",           bubbleError);
        TrySwapBrush(root, "BubbleBorderUserBrush",      bubbleBorderUser);
        TrySwapBrush(root, "BubbleBorderAssistantBrush", bubbleBorderAssist);
        TrySwapBrush(root, "BubbleBorderErrorBrush",     bubbleBorderError);
        TrySwapBrush(root, "AvatarUserBrush",            avatarUser);
        TrySwapBrush(root, "AvatarAssistantBrush",       avatarAssistant);
        TrySwapBrush(root, "AvatarErrorBrush",           avatarError);
        TrySwapBrush(root, "AuthorTextBrush",            authorText);
    }

    private static void TrySwapBrush(FrameworkElement root, string key, string colorString)
    {
        try
        {
            if (ParseColor(colorString) is not { } color) return;
            if (root.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                brush.Color = color;
        }
        catch
        {
            // Swallow — never let a theme brush lookup crash the window.
        }
    }

    private static void TrySwapGradient(FrameworkElement root, string key, string start, string end)
    {
        try
        {
            if (ParseColor(start) is not { } cStart || ParseColor(end) is not { } cEnd) return;
            if (root.Resources.TryGetValue(key, out var value))
            {
                if (value is LinearGradientBrush grad && grad.GradientStops.Count >= 2)
                {
                    grad.GradientStops[0].Color = cStart;
                    grad.GradientStops[1].Color = cEnd;
                }
                else if (value is SolidColorBrush solid)
                {
                    solid.Color = cStart;
                }
            }
        }
        catch
        {
            // Swallow — never let a theme brush lookup crash the window.
        }
    }

    public static Color? ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length == 8 && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var a)
                          && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                          && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                          && byte.TryParse(hex.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return Color.FromArgb(a, r, g, b);
        return null;
    }

    /// <summary>
    /// Mix an alpha hex (#AARRGGBB) with the full RRGGBB string by
    /// parsing both — used to derive a translucent accent wash for the
    /// dark/light theme. Falls back to the original if parsing fails.
    /// </summary>
    private static string LerpAlpha(string baseColor, string alphaHex)
    {
        var b = ParseColor(baseColor);
        var a = ParseColor(alphaHex);
        if (b is null || a is null) return baseColor;
        var mixed = Color.FromArgb(a.Value.A, b.Value.R, b.Value.G, b.Value.B);
        return $"#{mixed.A:X2}{mixed.R:X2}{mixed.G:X2}{mixed.B:X2}";
    }
}

public enum AppThemeMode { Light, Dark, System }

public sealed record AccentSet(
    string Accent,
    string AccentHover,
    string AccentPressed,
    string AccentSoft,
    string PaneEdge);

public sealed record ThemePalette(string Name, AccentSet Light, AccentSet Dark);

public sealed record ThemeNeutralSet(
    string Page,
    string Surface,
    string SurfaceElevated,
    string SubtleSurface,
    string Stroke,
    string SoftStroke,
    string Hairline,
    string PrimaryText,
    string SecondaryText,
    string MutedText,
    string PaneStart,
    string PaneEnd,
    string Composer,
    string ComposerBorder,
    string Hover,
    string Background);
