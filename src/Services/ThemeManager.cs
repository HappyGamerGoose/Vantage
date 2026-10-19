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
//      brushes' Colors smoothly rather than tearing down the resources.

using System;
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
        Page              : "#FFFCFCFE",
        Surface           : "#F8FFFFFF",
        SurfaceElevated   : "#F7FFFFFF",
        SubtleSurface     : "#F4F4F9",
        Stroke            : "#1F182033",
        SoftStroke        : "#14182033",
        Hairline          : "#0D182033",
        PrimaryText       : "#111522",
        SecondaryText     : "#66708A",
        MutedText         : "#9299AA",
        PaneStart         : "#FAFAFD",
        PaneEnd           : "#F6F7FC",
        Composer          : "#FAFFFFFF",
        ComposerBorder    : "#E3E5EC",
        Hover             : "#0A5442F5",
        Background        : "#FCFCFE");

    // ── Dark neutrals — calibrated for late-night reading. We avoid
    // pure #000 backgrounds because they look harsh next to a wide
    // monitor; instead the page sits one notch below the system
    // dark grey so chrome elements read as layered, not negative.
    private static readonly ThemeNeutralSet DarkNeutral = new(
        Page              : "#D20F1115",
        Surface           : "#D91A1D23",
        SurfaceElevated   : "#F022252B",
        SubtleSurface     : "#E123262D",
        Stroke            : "#40FFFFFF",
        SoftStroke        : "#20FFFFFF",
        Hairline          : "#12FFFFFF",
        PrimaryText       : "#E7EAEC",
        SecondaryText     : "#A4ADB5",
        MutedText         : "#73787E",
        PaneStart         : "#D916181E",
        PaneEnd           : "#1F2128",
        Composer          : "#F022252B",
        ComposerBorder    : "#323640",
        Hover             : "#14FFFFFF",
        Background        : "#15171C");

    // ── Accent palettes ─────────────────────────────────────────────
    public static readonly IReadOnlyList<ThemePalette> Palettes = new ThemePalette[]
    {
        // 1. Green — Windows "Nature" emoji, classic Win10 start menu accent.
        new ThemePalette("Green",
            new AccentSet("#107C10", "#1F8A1F", "#0C660C", "#E5F3E5", "#26107C10"),
            new AccentSet("#6CCB5E", "#88D67B", "#5BAB4F", "#1B3326", "#506CCB5E")),

        // 2. Violet — Tailwind violet + Windows 11 Flow accent.
        new ThemePalette("Violet",
            new AccentSet("#5442F5", "#6555FA", "#4534D9", "#EFEDFF", "#265442F5"),
            new AccentSet("#968BFF", "#AAA1FF", "#7F72F0", "#292445", "#50968BFF")),

        // 3. Yellow — sunshine / amber. Reads warm without being alarm-orange.
        new ThemePalette("Yellow",
            new AccentSet("#B27B00", "#C88B00", "#9A6A00", "#FFF6DB", "#26B27B00"),
            new AccentSet("#FFD54F", "#FFE082", "#FFCA28", "#332A15", "#50FFD54F")),

        // 4. Blue — Windows 11 default accent.
        new ThemePalette("Blue",
            new AccentSet("#0078D4", "#1084D9", "#0064B0", "#E8F1FB", "#260078D4"),
            new AccentSet("#4CC2FF", "#68CDFF", "#3AA8E8", "#172A3A", "#504CC2FF")),

        // 5. Teal — classic Vantage brand accent.
        new ThemePalette("Teal",
            new AccentSet("#0E7A6E", "#148A7D", "#0A655B", "#E3F4F1", "#260E7A6E"),
            new AccentSet("#4ED2C1", "#6DE0D1", "#3EBFAD", "#142F2B", "#504ED2C1")),

        // 6. Orange — warm sunset accent.
        new ThemePalette("Orange",
            new AccentSet("#C44E12", "#D45D1E", "#AA420E", "#FCEEE5", "#26C44E12"),
            new AccentSet("#FF8A50", "#FFA06D", "#F47133", "#382319", "#50FF8A50")),

        // 7. Pink — vibrant modern magenta hint.
        new ThemePalette("Pink",
            new AccentSet("#C2185B", "#D1286C", "#A8114D", "#FDE7F0", "#26C2185B"),
            new AccentSet("#FF6599", "#FF82B0", "#F04D85", "#381724", "#50FF6599")),

        // 8. Slate — restrained, recedes into the background.
        new ThemePalette("Slate",
            new AccentSet("#5A6B7C", "#687B8D", "#4C5B6B", "#EEF2F6", "#265A6B7C"),
            new AccentSet("#9AAEC2", "#ADC0D3", "#879CB1", "#202730", "#509AAEC2")),
    };

    public static ThemePalette GetPalette(string name)
    {
        foreach (var p in Palettes)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        return Palettes[1]; // default Violet
    }

    private sealed class ColorTransition
    {
        public SolidColorBrush Brush { get; }
        public Color StartColor { get; }
        public Color TargetColor { get; }

        public ColorTransition(SolidColorBrush brush, Color startColor, Color targetColor)
        {
            Brush = brush;
            StartColor = startColor;
            TargetColor = targetColor;
        }
    }

    private static System.Diagnostics.Stopwatch? _colorAnimStopwatch;
    private static List<ColorTransition>? _activeTransitions;
    private static bool _colorAnimHooked;
    private const double ColorAnimDurationMs = 240.0;

    /// <summary>
    /// Apply a palette + theme to the running window. Mutates the brush
    /// colors smoothly in place so existing {StaticResource} bindings stay live.
    /// Supports fluid color interpolation across theme toggles and palette changes.
    /// </summary>
    public static void Apply(FrameworkElement root, string paletteName, bool darkMode, bool animate = true)
    {
        if (root is null) return;
        var palette = GetPalette(paletteName);
        var accent  = darkMode ? palette.Dark : palette.Light;
        var neutral = darkMode ? DarkNeutral : LightNeutral;

        var targets = new List<(string Key, string ColorHex)>(42);
        void Queue(string key, string hex) => targets.Add((key, hex));

        // Neutrals — page surfaces, text, separators, composer chrome
        Queue("PageBrush",              neutral.Page);
        Queue("SurfaceBrush",           neutral.Surface);
        Queue("SurfaceElevatedBrush",   neutral.SurfaceElevated);
        Queue("SubtleSurfaceBrush",     neutral.SubtleSurface);
        Queue("StrokeBrush",            neutral.Stroke);
        Queue("SoftStrokeBrush",        neutral.SoftStroke);
        Queue("HairlineBrush",          neutral.Hairline);
        Queue("PrimaryTextBrush",       neutral.PrimaryText);
        Queue("SecondaryTextBrush",     neutral.SecondaryText);
        Queue("MutedTextBrush",         neutral.MutedText);
        Queue("PaneBrush",              neutral.PaneStart);
        Queue("RailBrush",              darkMode ? "#F0121820" : "#F8F9FD");
        Queue("ChatCanvasBrush",        darkMode ? "#E8101217" : "#FCFCFE");
        Queue("HoverBrush",             neutral.Hover);
        Queue("ComposerBrush",          neutral.Composer);
        Queue("ComposerBorderBrush",    neutral.ComposerBorder);
        Queue("InputBrush",             neutral.Composer);
        Queue("PendingBackgroundBrush", neutral.Page);

        // Accent palette
        Queue("AccentBrush",            accent.Accent);
        Queue("AccentHoverBrush",       accent.AccentHover);
        Queue("AccentPressedBrush",     accent.AccentPressed);
        Queue("AccentSoftBrush",        accent.AccentSoft);
        Queue("PaneEdgeBrush",          neutral.SoftStroke);
        Queue("PanelTintBrush",         darkMode ? WithAlpha(accent.Accent, "26") : accent.AccentSoft);

        // Warm / Success / Danger
        var warmAccent    = darkMode ? WithAlpha(accent.Accent, "EA") : "#B66A42";
        var successAccent = darkMode ? "#6CD676" : "#237A4B";
        var successSoft   = darkMode ? "#1B3326" : "#D8EFE2";
        var dangerAccent  = darkMode ? "#E67878" : "#A83232";
        var warmHover     = darkMode ? WithAlpha(accent.Accent, "FF") : "#C77C58";
        var warmPressed   = darkMode ? WithAlpha(accent.Accent, "C0") : "#8E5030";
        Queue("WarmBrush",              warmAccent);
        Queue("WarmHoverBrush",         warmHover);
        Queue("WarmPressedBrush",       warmPressed);
        Queue("SuccessBrush",           successAccent);
        Queue("SuccessSoftBrush",       successSoft);
        Queue("DangerBrush",            dangerAccent);

        var errorText = darkMode ? "#F4836D" : "#B3261E";
        Queue("ErrorTextBrush",         errorText);

        // Message bubbles & avatars
        var bubbleUser          = darkMode ? WithAlpha(accent.Accent, "26") : accent.AccentSoft;
        var bubbleAssistant     = "#00FFFFFF";
        var bubbleError         = darkMode ? "#3A1F1C" : "#FBEEE8";
        var bubbleBorderUser    = WithAlpha(accent.Accent, darkMode ? "55" : "32");
        var bubbleBorderAssist  = "#00000000";
        var bubbleBorderError   = darkMode ? "#80E67878" : "#E9B9A7";
        var avatarUser          = darkMode ? "#262C32" : "#E2E9EE";
        var avatarAssistant     = darkMode ? WithAlpha(accent.Accent, "28") : accent.AccentSoft;
        var avatarError         = darkMode ? "#3A1F1C" : "#FBEEE8";
        var authorText          = darkMode
                                    ? WithAlpha(accent.Accent, "FF")
                                    : WithAlpha(accent.AccentPressed, "FF");
        Queue("BubbleUserBrush",            bubbleUser);
        Queue("BubbleAssistantBrush",       bubbleAssistant);
        Queue("BubbleErrorBrush",           bubbleError);
        Queue("BubbleBorderUserBrush",      bubbleBorderUser);
        Queue("BubbleBorderAssistantBrush", bubbleBorderAssist);
        Queue("BubbleBorderErrorBrush",     bubbleBorderError);
        Queue("AvatarUserBrush",            avatarUser);
        Queue("AvatarAssistantBrush",       avatarAssistant);
        Queue("AvatarErrorBrush",           avatarError);
        Queue("AuthorTextBrush",            authorText);

        ApplyBrushSwaps(root, targets, animate);
    }

    private static void ApplyBrushSwaps(FrameworkElement root, List<(string Key, string ColorHex)> targets, bool animate)
    {
        StopColorAnimation();

        var transitions = new List<ColorTransition>(targets.Count);
        foreach (var (key, hex) in targets)
        {
            try
            {
                if (ParseColor(hex) is not { } targetColor) continue;
                if (root.Resources.TryGetValue(key, out var val) && val is SolidColorBrush brush)
                {
                    if (!animate || !AreSystemAnimationsEnabled())
                    {
                        brush.Color = targetColor;
                    }
                    else if (brush.Color != targetColor)
                    {
                        transitions.Add(new ColorTransition(brush, brush.Color, targetColor));
                    }
                }
            }
            catch
            {
                // Never let a theme lookup crash the process
            }
        }

        if (!animate || !AreSystemAnimationsEnabled() || transitions.Count == 0) return;

        _activeTransitions = transitions;
        _colorAnimStopwatch ??= new System.Diagnostics.Stopwatch();
        _colorAnimStopwatch.Restart();

        if (!_colorAnimHooked)
        {
            CompositionTarget.Rendering += OnColorRendering;
            _colorAnimHooked = true;
        }
    }

    private static void OnColorRendering(object? sender, object e)
    {
        if (_colorAnimStopwatch is null || _activeTransitions is null)
        {
            StopColorAnimation();
            return;
        }

        double elapsed = _colorAnimStopwatch.Elapsed.TotalMilliseconds;
        double progress = Math.Clamp(elapsed / ColorAnimDurationMs, 0.0, 1.0);
        double ease = -(Math.Cos(Math.PI * progress) - 1.0) / 2.0;

        foreach (var trans in _activeTransitions)
        {
            trans.Brush.Color = LerpColor(trans.StartColor, trans.TargetColor, ease);
        }

        if (progress >= 1.0)
        {
            foreach (var trans in _activeTransitions)
            {
                trans.Brush.Color = trans.TargetColor;
            }
            StopColorAnimation();
        }
    }

    public static void StopColorAnimation()
    {
        if (_colorAnimHooked)
        {
            CompositionTarget.Rendering -= OnColorRendering;
            _colorAnimHooked = false;
        }
        _colorAnimStopwatch?.Stop();
        _activeTransitions = null;
    }

    private static Color LerpColor(Color c1, Color c2, double t)
    {
        byte a = (byte)Math.Clamp(Math.Round(c1.A + (c2.A - c1.A) * t), 0, 255);
        byte r = (byte)Math.Clamp(Math.Round(c1.R + (c2.R - c1.R) * t), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(c1.G + (c2.G - c1.G) * t), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(c1.B + (c2.B - c1.B) * t), 0, 255);
        return Color.FromArgb(a, r, g, b);
    }

    private static bool AreSystemAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
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
    /// Apply a two-digit alpha value to an accent color.
    /// </summary>
    private static string WithAlpha(string baseColor, string alphaHex)
    {
        var b = ParseColor(baseColor);
        var normalizedAlpha = alphaHex.TrimStart('#');
        if (b is null || normalizedAlpha.Length != 2 ||
            !byte.TryParse(normalizedAlpha, System.Globalization.NumberStyles.HexNumber, null, out var alpha))
        {
            return baseColor;
        }

        var mixed = Color.FromArgb(alpha, b.Value.R, b.Value.G, b.Value.B);
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
