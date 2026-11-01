// SPDX-License-Identifier: MIT
// Vantage — MainWindow.ThemePalette.cs
//
// Theme + palette picker code. The settings are persisted to LocalPreferences
// under "ThemePalette" + "ThemeMode" and reapplied on startup. `ApplyCurrentTheme`
// resolves the current ThemeMode (Light/Dark/System), walks the palette
// through ThemeManager.Apply, and updates the Window chrome's ElementTheme
// so the WinUI controls know whether to ask for light or dark theme resources.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vantage.Services;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Vantage;

public sealed partial class MainWindow
{
    private const string PaletteKey   = "ThemePalette";
    private const string ThemeModeKey = "ThemeMode";
    private const string ReferenceDesignPaletteKey = "ReferenceDesignPaletteApplied";

    // Single OS-theme listener, subscribed on first apply. Listens to
    // `ColorValuesChanged` from the system's UISettings — fires whenever
    // the user toggles dark mode in Windows Settings (or the OS does,
    // e.g. at sunset on a theme-aware schedule). When the user's
    // ThemeMode is "System (follow Windows)" we repaint automatically;
    // when the user has explicitly picked Light/Dark, we leave them alone.
    private UISettings? _osThemeWatcher;

    private bool IsOsDarkMode()
    {
        // We can't read `RootGrid.ActualTheme` for this — ActualTheme
        // reflects what we JUST PAINTED, not what the OS currently says.
        // Querying UISettings.ColorValuesChanged + foreground color is
        // the supported WinUI 3 way. Default to Light if anything fails.
        try
        {
            var ui = new UISettings();
            var fg = ui.GetColorValue(UIColorType.Foreground);
            // Foreground is "almost black" (high R/G/B, all dark hex digits)
            // in light mode and "almost white" in dark mode. Checking
            // luminance is the conventional heuristic.
            int r = fg.R, g = fg.G, b = fg.B;
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            return luma > 128;
        }
        catch
        {
            return RootGrid.ActualTheme == ElementTheme.Dark;
        }
    }

    private void StartOsThemeWatcher()
    {
        if (_osThemeWatcher is not null) return;
        try
        {
            _osThemeWatcher = new UISettings();
            // ColorValuesChanged fires on a background thread. Marshal the
            // repaint back to the UI thread so the brushes are mutated in
            // a single writer (the dispatcher queue).
            _osThemeWatcher.ColorValuesChanged += (sender, args) =>
            {
                if (this.DispatcherQueue is { } dq)
                {
                    dq.TryEnqueue(() => ApplyCurrentTheme(animate: true));
                }
            };
        }
        catch
        {
            // Older Windows builds or sandboxed contexts may fail to
            // create UISettings. We silently fall back to the persisted
            // Light/Dark choice — System mode becomes a one-shot read.
        }
    }

    private bool _palettePickerBuilt;

    private void PalettePicker_Loaded(object sender, RoutedEventArgs e)
    {
        BuildPalettePicker();
    }

    /// <summary>
    /// Populate the palette picker once, on first reveal. WinUI 3's
    /// Loaded event fires when the element is added to the tree, but for
    /// a hidden sub-tree on app startup it may not fire until the parent
    /// actually becomes visible — so we also call this from
    /// <c>ShowPage("settings")</c> as a belt-and-suspenders. Idempotent
    /// via the <c>_palettePickerBuilt</c> flag — the call is cheap, but
    /// avoiding the rebuild on every settings navigation keeps the
    /// active-card highlight from flickering.
    /// </summary>
    private void BuildPalettePicker()
    {
        if (_palettePickerBuilt || PalettePicker is null) return;
        _palettePickerBuilt = true;
        // Build the swatch rows directly in code. WinUI 3 omits WrapPanel
        // so we skip the ItemsControl + DataTemplate path entirely; the
        // palette list is tiny (eight rows) so the markup overhead isn't
        // worth it.
        PalettePicker.Children.Clear();
        foreach (var p in ThemeManager.Palettes)
        {
            var accent = ThemeManager.ParseColor(p.Light.Accent)
                         ?? Color.FromArgb(255, 0, 120, 212);
            var card = BuildPaletteCard(p, accent);
            PalettePicker.Children.Add(card);
        }
        HighlightActivePaletteCard();
        // Start watching for OS theme changes once the picker has rendered
        // so we don't repaint before the brushes are populated.
        StartOsThemeWatcher();
    }

    private readonly Dictionary<string, (Border Ring, FontIcon Check)> _paletteSwatches = new(StringComparer.OrdinalIgnoreCase);

    private Button BuildPaletteCard(ThemePalette palette, Color swatchColor)
    {
        var check = new FontIcon
        {
            FontSize = 13,
            Glyph = "",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Visibility = Visibility.Collapsed,
        };
        var dot = new Grid
        {
            Width = 34, Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(swatchColor),
            Children = { check },
        };
        var ring = new Border
        {
            Width = 44, Height = 44,
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = dot,
        };
        _paletteSwatches[palette.Name] = (ring, check);

        var button = new Button
        {
            Style = (Style)RootGrid.Resources["GhostIconButtonStyle"],
            Width = 76, Height = 78,
            Padding = new Thickness(0, 4, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Top,
            Tag = palette.Name,
            Content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    ring,
                    new TextBlock
                    {
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)RootGrid.Resources["SecondaryTextBrush"],
                        Text = palette.Name,
                    },
                }
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, palette.Name + " accent");
        button.Click += (_, _) =>
        {
            LocalPreferences.SetString(PaletteKey, palette.Name);
            ApplyCurrentTheme();
        };
        return button;
    }

    private void HighlightActivePaletteCard()
    {
        var currentName = LocalPreferences.GetString(PaletteKey, "Violet");
        foreach (var (name, parts) in _paletteSwatches)
        {
            var isActive = string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase);
            parts.Ring.BorderBrush = isActive
                ? (Brush)RootGrid.Resources["AccentBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            parts.Check.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (tag == LocalPreferences.GetString(ThemeModeKey, "System")) return;
        LocalPreferences.SetString(ThemeModeKey, tag);
        ApplyCurrentTheme();
    }

    /// <summary>
    /// Resolve persisted palette + theme and repaint the window. The
    /// previous version read <c>RootGrid.ActualTheme</c> to detect what
    /// to paint when mode=System — but ActualTheme reflects what we
    /// JUST PAINTED, not what the OS actually says. Picking "System"
    /// while previously in Dark and the OS set to Light used to leave
    /// the app stuck in Dark. Now we query <see cref="IsOsDarkMode"/>
    /// directly against <c>UISettings.GetColorValue(UIColorType.Foreground)</c>,
    /// so a pick of "System" instantly flips to whatever the OS is doing
    /// AND the <see cref="StartOsThemeWatcher"/> callback keeps us in sync
    /// when the user changes the OS theme later.
    /// </summary>
    private void ApplyCurrentTheme(bool animate = true)
    {
        if (!LocalPreferences.GetBool(ReferenceDesignPaletteKey, false))
        {
            LocalPreferences.SetString(PaletteKey, "Violet");
            LocalPreferences.SetBool(ReferenceDesignPaletteKey, true);
        }
        var paletteName = LocalPreferences.GetString(PaletteKey, "Violet");
        var modeTag = LocalPreferences.GetString(ThemeModeKey, "System");
        var mode = modeTag switch
        {
            "Dark"   => AppThemeMode.Dark,
            "Light"  => AppThemeMode.Light,
            _        => AppThemeMode.System,
        };
        var useDark = mode == AppThemeMode.Dark
                   || (mode == AppThemeMode.System && IsOsDarkMode());
        ThemeManager.Apply(RootGrid, paletteName, useDark, animate);
        UpdateTitleBarColors(useDark);
        RootGrid.RequestedTheme = mode == AppThemeMode.System
            ? ElementTheme.Default
            : (useDark ? ElementTheme.Dark : ElementTheme.Light);
        var option = modeTag switch
        {
            "Light" => ThemeLightOption,
            "Dark"  => ThemeDarkOption,
            _       => ThemeSystemOption,
        };
        if (option.IsChecked != true) option.IsChecked = true;
        HighlightActivePaletteCard();
    }

    private void UpdateTitleBarColors(bool useDark)
    {
        try
        {
            if (AppWindow.TitleBar is not { } titleBar) return;

            var foreground = useDark
                ? Color.FromArgb(0xFF, 0xE7, 0xEA, 0xEC)
                : Color.FromArgb(0xFF, 0x10, 0x1A, 0x24);
            var inactive = useDark
                ? Color.FromArgb(0xFF, 0x73, 0x78, 0x7E)
                : Color.FromArgb(0xFF, 0x94, 0xA0, 0xA6);
            var hover = useDark
                ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x16, 0x07, 0x14, 0x1A);
            var pressed = useDark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x26, 0x07, 0x14, 0x1A);

            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = inactive;
        }
        catch
        {
            // Native title-bar theming is best-effort on older Windows builds.
        }
    }
}
