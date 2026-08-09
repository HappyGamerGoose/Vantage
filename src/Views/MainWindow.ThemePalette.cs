// SPDX-License-Identifier: MIT
// Vantage — MainWindow.ThemePalette.cs
//
// Theme + palette picker code. The settings are persisted to LocalSettings
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
                    dq.TryEnqueue(ApplyCurrentTheme);
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

    private Border BuildPaletteCard(ThemePalette palette, Color swatchColor)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background   = (Brush)RootGrid.Resources["SurfaceElevatedBrush"],
            BorderBrush  = (Brush)RootGrid.Resources["SoftStrokeBrush"],
            BorderThickness = new Thickness(1),
            Padding      = new Thickness(10, 6, 12, 6),
            Margin       = new Thickness(0, 0, 0, 0),
            Tag          = palette.Name,
        };
        var inner = new Grid { ColumnSpacing = 8 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 18, Height = 18,
            CornerRadius = new CornerRadius(9),
            Background   = new SolidColorBrush(swatchColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(dot, 0);
        inner.Children.Add(dot);

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
        };
        text.Children.Add(new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)RootGrid.Resources["PrimaryTextBrush"],
            Text = palette.Name,
        });
        text.Children.Add(new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)RootGrid.Resources["MutedTextBrush"],
            Text = PaletteTagline(palette.Name),
        });
        Grid.SetColumn(text, 1);
        inner.Children.Add(text);

        card.Child = inner;
        card.Tapped += (_, _) =>
        {
            try { Windows.Storage.ApplicationData.Current.LocalSettings.Values[PaletteKey] = palette.Name; } catch { }
            ApplyCurrentTheme();
        };
        return card;
    }

    private void HighlightActivePaletteCard()
    {
        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        var currentName = localSettings.Values.TryGetValue(PaletteKey, out var v) && v is string s ? s : "Violet";
        foreach (var child in PalettePicker.Children)
        {
            if (child is Border b && b.Tag is string paletteName)
            {
                var isActive = string.Equals(paletteName, currentName, StringComparison.OrdinalIgnoreCase);
                b.BorderBrush = isActive
                    ? (Brush)RootGrid.Resources["AccentBrush"]
                    : (Brush)RootGrid.Resources["SoftStrokeBrush"];
                b.BorderThickness = new Thickness(isActive ? 2 : 1);
            }
        }
    }

    private static string PaletteTagline(string name) => name switch
    {
        "Green"  => "Windows nature · calm",
        "Violet" => "Flow accent · playful",
        "Yellow" => "Sunshine · bold",
        "Blue"   => "Windows 11 default",
        "Teal"   => "Vantage classic · focused",
        "Orange" => "Sunset · warm",
        "Pink"   => "Magenta hint · soft",
        "Slate"  => "Neutral · recedes",
        _        => string.Empty
    };

    private void ThemeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeModeCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "Light";
        try { Windows.Storage.ApplicationData.Current.LocalSettings.Values[ThemeModeKey] = tag; } catch { }
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
    private void ApplyCurrentTheme()
    {
        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        if (!localSettings.Values.TryGetValue(ReferenceDesignPaletteKey, out var migrated) || migrated is not true)
        {
            localSettings.Values[PaletteKey] = "Violet";
            localSettings.Values[ReferenceDesignPaletteKey] = true;
        }
        var paletteName = localSettings.Values.TryGetValue(PaletteKey, out var pal) && pal is string ps
            ? ps : "Violet";
        var modeTag = localSettings.Values.TryGetValue(ThemeModeKey, out var tm) && tm is string ts
            ? ts : "System";
        var mode = modeTag switch
        {
            "Dark"   => AppThemeMode.Dark,
            "System" => AppThemeMode.System,
            _        => AppThemeMode.System,
        };
        var useDark = mode == AppThemeMode.Dark
                   || (mode == AppThemeMode.System && IsOsDarkMode());
        ThemeManager.Apply(RootGrid, paletteName, useDark);
        UpdateTitleBarColors(useDark);
        RootGrid.RequestedTheme = mode == AppThemeMode.System
            ? ElementTheme.Default
            : (useDark ? ElementTheme.Dark : ElementTheme.Light);
        if (ThemeModeCombo.SelectedIndex < 0 && ThemeModeCombo.Items.Count > 0)
        {
            var idx = modeTag switch
            {
                "Light"  => 0,
                "Dark"   => 1,
                "System" => 2,
                _        => 0,
            };
            ThemeModeCombo.SelectedIndex = idx;
        }
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
