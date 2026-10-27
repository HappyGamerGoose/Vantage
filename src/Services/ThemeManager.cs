// SPDX-License-Identifier: MIT
// Vantage — Services/ThemeManager.cs
//
// Centralised palette + dark-mode management. Defines:
//
//   1. Eight accent palettes (Green · Violet · Yellow · Blue · Teal ·
//      Orange · Pink · Slate), each with Light + Dark brush sets
//      (Accent / AccentHover / AccentPressed / AccentSoft / PaneEdge).
//
//   2. A Light/Dark neutral set (Page / Surface / SurfaceElevated /
//      SubtleSurface / text brushes / Sidebar / Rail / Chat canvas /
//      Composer chrome) generated per palette: each palette's hue tints
//      the neutrals, so a palette changes the whole mood and not just
//      the accent. Toggling dark mode swaps the lightness ladder.
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
    // ── Neutrals ────────────────────────────────────────────────────
    // Neutrals are no longer one shared grey set: each palette carries a hue
    // and the page, sidebar, cards, strokes and text are tinted toward it.
    // That is what makes switching palette feel like a different theme
    // instead of a recoloured button. Lightness values are fixed so contrast
    // stays predictable (primary text ≥ 12:1, secondary ≥ 6:1, muted ≥ 4:1).
    private static ThemeNeutralSet BuildNeutral(ThemePalette p, bool dark)
    {
        var h = p.Hue;
        // Warm hues (orange/amber) turn to chocolate-brown when tinted at the
        // dark lightness levels, so they get a lighter touch there.
        var t = p.TintStrength * (dark && h is >= 10 and <= 60 ? 0.6 : 1.0);
        string C(double s, double l, string alpha = "") => Hsl(h, Math.Clamp(s * t, 0, 1), l, alpha);

        if (!dark)
        {
            return new ThemeNeutralSet(
                Page            : C(0.60, 0.975),
                Surface         : C(0.40, 0.995, "FA"),
                SurfaceElevated : "#FFFFFFFF",
                SubtleSurface   : C(0.55, 0.950),
                Stroke          : C(0.45, 0.20, "24"),
                SoftStroke      : C(0.45, 0.20, "16"),
                Hairline        : C(0.45, 0.20, "0E"),
                PrimaryText     : C(0.40, 0.12),
                SecondaryText   : C(0.20, 0.36),
                MutedText       : C(0.15, 0.49),
                PaneStart       : C(0.65, 0.955),
                PaneEnd         : C(0.65, 0.935),
                Composer        : "#FFFFFFFF",
                ComposerBorder  : C(0.45, 0.87),
                Hover           : C(0.60, 0.50, "14"),
                Background      : C(0.60, 0.975),
                Rail            : C(0.65, 0.940),
                ChatCanvas      : C(0.55, 0.985));
        }

        return new ThemeNeutralSet(
            Page            : C(0.42, 0.095, "F2"),
            Surface         : C(0.36, 0.140, "F5"),
            SurfaceElevated : C(0.34, 0.180),
            SubtleSurface   : C(0.32, 0.205),
            Stroke          : C(0.60, 0.88, "38"),
            SoftStroke      : C(0.60, 0.88, "1E"),
            Hairline        : C(0.60, 0.88, "12"),
            PrimaryText     : C(0.30, 0.94),
            SecondaryText   : C(0.18, 0.74),
            MutedText       : C(0.14, 0.58),
            PaneStart       : C(0.44, 0.120, "F2"),
            PaneEnd         : C(0.40, 0.145),
            Composer        : C(0.34, 0.180),
            ComposerBorder  : C(0.30, 0.290),
            Hover           : C(0.80, 0.80, "1C"),
            Background      : C(0.42, 0.095),
            Rail            : C(0.44, 0.110, "F2"),
            ChatCanvas      : C(0.40, 0.085, "F0"));
    }

    /// <summary>HSL (h in degrees, s/l in 0..1) to the "#AARRGGBB" form ParseColor reads.</summary>
    private static string Hsl(double hue, double s, double l, string alpha = "")
    {
        var h = (((hue % 360) + 360) % 360) / 360.0;
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        var r = (byte)Math.Round(Channel(p, q, h + 1.0 / 3) * 255);
        var g = (byte)Math.Round(Channel(p, q, h) * 255);
        var b = (byte)Math.Round(Channel(p, q, h - 1.0 / 3) * 255);
        return $"#{alpha}{r:X2}{g:X2}{b:X2}";
    }

    // ── Accent palettes ─────────────────────────────────────────────
    // Light accents are all ≥ 4.5:1 on white so they stay legible as text
    // (they colour the "New chat" label and author names); dark accents are
    // lifted to ≥ 7:1 on the dark surfaces. Hue/tint drive the neutrals.
    public static readonly IReadOnlyList<ThemePalette> Palettes = new ThemePalette[]
    {
        // 1. Green — fresh and calm.
        new ThemePalette("Green",
            new AccentSet("#17853F", "#1F9A4C", "#106B31", "#E2F5E8", "#2617853F"),
            new AccentSet("#5FDC8A", "#7DE7A1", "#46C070", "#12361F", "#505FDC8A"),
            Hue: 142, TintStrength: 1.0),

        // 2. Violet — the default; vivid indigo-violet.
        new ThemePalette("Violet",
            new AccentSet("#6A4DF4", "#7C63F7", "#5538D8", "#F0ECFF", "#266A4DF4"),
            new AccentSet("#A99BFF", "#BBB0FF", "#8F7FF0", "#2A2250", "#50A99BFF"),
            Hue: 252, TintStrength: 1.0),

        // 3. Yellow — golden honey on a cream canvas (pure yellow is not
        // readable as text, so the warmth lives in the surfaces).
        new ThemePalette("Yellow",
            new AccentSet("#A56700", "#BA7600", "#8A5500", "#FFF3D1", "#26A56700"),
            new AccentSet("#FFCB47", "#FFD86F", "#F2B72A", "#3D2F0C", "#50FFCB47"),
            Hue: 42, TintStrength: 1.0),

        // 4. Blue — clear cobalt.
        new ThemePalette("Blue",
            new AccentSet("#2468E8", "#3A7BF0", "#1B54C7", "#E7F0FF", "#262468E8"),
            new AccentSet("#6AAEFF", "#86BEFF", "#4F97EE", "#14274A", "#506AAEFF"),
            Hue: 220, TintStrength: 1.0),

        // 5. Teal — the classic Vantage accent, brightened.
        new ThemePalette("Teal",
            new AccentSet("#0B7F75", "#129186", "#08675F", "#E0F5F2", "#260B7F75"),
            new AccentSet("#3FD6C2", "#62E2D1", "#2DBBA8", "#0F3A38", "#503FD6C2"),
            Hue: 174, TintStrength: 1.0),

        // 6. Orange — warm sunset, vivid rather than brown.
        new ThemePalette("Orange",
            new AccentSet("#CC4509", "#E0540F", "#AD3905", "#FFEADB", "#26CC4509"),
            new AccentSet("#FF9558", "#FFAA77", "#F57C37", "#43220F", "#50FF9558"),
            Hue: 22, TintStrength: 1.0),

        // 7. Pink — raspberry.
        new ThemePalette("Pink",
            new AccentSet("#D01F6B", "#E0327D", "#AF1558", "#FFE6F1", "#26D01F6B"),
            new AccentSet("#FF7DB1", "#FF98C2", "#F25F9A", "#431631", "#50FF7DB1"),
            Hue: 336, TintStrength: 1.0),

        // 8. Slate — restrained steel blue; tint is kept low on purpose.
        new ThemePalette("Slate",
            new AccentSet("#4A5C78", "#5A6D8A", "#3B4A62", "#E9EEF6", "#264A5C78"),
            new AccentSet("#9DB2CC", "#B2C4DA", "#869CB8", "#1F2A3A", "#509DB2CC"),
            Hue: 218, TintStrength: 0.45),
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
        var neutral = BuildNeutral(palette, darkMode);

        var targets = new List<(string Key, string ColorHex)>(96);
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
        Queue("RailBrush",              neutral.Rail);
        Queue("ChatCanvasBrush",        neutral.ChatCanvas);
        Queue("HoverBrush",             neutral.Hover);
        Queue("ComposerBrush",          neutral.Composer);
        Queue("ComposerBorderBrush",    neutral.ComposerBorder);
        Queue("InputBrush",             neutral.Composer);
        Queue("PendingBackgroundBrush", neutral.Page);

        // Text inputs: inset fill + themed border/placeholder in every state,
        // so WinUI's system greys never show through (see MainWindow.xaml).
        Queue("TextControlBackground",                       neutral.SubtleSurface);
        Queue("TextControlBackgroundPointerOver",            neutral.SubtleSurface);
        Queue("TextControlBackgroundFocused",                neutral.SubtleSurface);
        Queue("TextControlBackgroundDisabled",               WithAlpha(neutral.SubtleSurface, "80"));
        Queue("TextControlBorderBrush",                      neutral.ComposerBorder);
        Queue("TextControlBorderBrushPointerOver",           neutral.Stroke);
        Queue("TextControlBorderBrushFocused",               accent.Accent);
        Queue("TextControlBorderBrushDisabled",              neutral.Hairline);
        Queue("TextControlForeground",                       neutral.PrimaryText);
        Queue("TextControlForegroundPointerOver",            neutral.PrimaryText);
        Queue("TextControlForegroundFocused",                neutral.PrimaryText);
        Queue("TextControlForegroundDisabled",               neutral.MutedText);
        Queue("TextControlPlaceholderForeground",            neutral.MutedText);
        Queue("TextControlPlaceholderForegroundPointerOver", neutral.MutedText);
        Queue("TextControlPlaceholderForegroundFocused",     neutral.MutedText);
        Queue("TextControlPlaceholderForegroundDisabled",    neutral.MutedText);
        Queue("TextControlSelectionHighlightColor",          accent.Accent);
        Queue("ToggleSwitchFillOn",                          accent.Accent);
        Queue("ToggleSwitchFillOnPointerOver",               accent.AccentHover);
        Queue("ToggleSwitchFillOnPressed",                   accent.AccentPressed);
        Queue("ToggleSwitchStrokeOn",                        accent.Accent);
        Queue("ToggleSwitchStrokeOnPointerOver",             accent.AccentHover);
        Queue("ToggleSwitchStrokeOnPressed",                 accent.AccentPressed);
        Queue("ComboBoxBackground",                          neutral.SubtleSurface);
        Queue("ComboBoxBackgroundPointerOver",               neutral.SubtleSurface);
        Queue("ComboBoxBackgroundPressed",                   neutral.SubtleSurface);
        Queue("ComboBoxBackgroundFocused",                   neutral.SubtleSurface);
        Queue("ComboBoxBorderBrush",                         neutral.ComposerBorder);
        Queue("ComboBoxBorderBrushPointerOver",              neutral.Stroke);
        Queue("ComboBoxBorderBrushPressed",                  neutral.Stroke);
        Queue("TextControlButtonForeground",                 neutral.SecondaryText);
        Queue("TextControlButtonForegroundPointerOver",      neutral.PrimaryText);

        // Accent palette
        Queue("AccentBrush",            accent.Accent);
        Queue("AccentHoverBrush",       accent.AccentHover);
        Queue("AccentPressedBrush",     accent.AccentPressed);
        Queue("AccentSoftBrush",        accent.AccentSoft);
        Queue("PaneEdgeBrush",          neutral.SoftStroke);
        Queue("PanelTintBrush",         darkMode ? WithAlpha(accent.Accent, "26") : accent.AccentSoft);

        // Warm / Success / Danger
        // "Warm" colours the Stop button: a clear coral-red in both modes so
        // it reads as "interrupt" regardless of the chosen accent.
        var warmAccent    = darkMode ? "#E5575F" : "#D9434A";
        var successAccent = darkMode ? "#5FDC8A" : "#1E8A4C";
        var successSoft   = darkMode ? "#12361F" : "#DDF4E6";
        var dangerAccent  = darkMode ? "#FF8A90" : "#C62F36";
        var warmHover     = darkMode ? "#F0707A" : "#E5595F";
        var warmPressed   = darkMode ? "#C9444C" : "#B3343A";
        Queue("WarmBrush",              warmAccent);
        Queue("WarmHoverBrush",         warmHover);
        Queue("WarmPressedBrush",       warmPressed);
        Queue("SuccessBrush",           successAccent);
        Queue("SuccessSoftBrush",       successSoft);
        Queue("DangerBrush",            dangerAccent);

        var errorText = darkMode ? "#FF9A9A" : "#B3261E";
        Queue("ErrorTextBrush",         errorText);

        // Message bubbles & avatars
        var bubbleUser          = darkMode ? WithAlpha(accent.Accent, "26") : accent.AccentSoft;
        var bubbleAssistant     = "#00FFFFFF";
        var bubbleError         = darkMode ? "#3F1E24" : "#FFECEE";
        var bubbleBorderUser    = WithAlpha(accent.Accent, darkMode ? "55" : "38");
        var bubbleBorderAssist  = "#00000000";
        var bubbleBorderError   = darkMode ? "#80FF8A90" : "#F2B4B8";
        var avatarUser          = neutral.SubtleSurface;
        var avatarAssistant     = darkMode ? WithAlpha(accent.Accent, "38") : accent.AccentSoft;
        var avatarError         = darkMode ? "#3F1E24" : "#FFECEE";
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
        Queue("AuthorErrorBrush",           errorText);

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

public sealed record ThemePalette(
    string Name,
    AccentSet Light,
    AccentSet Dark,
    double Hue = 252,
    double TintStrength = 1.0);

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
    string Background,
    string Rail,
    string ChatCanvas);
