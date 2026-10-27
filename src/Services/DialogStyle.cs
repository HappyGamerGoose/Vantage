// SPDX-License-Identifier: MIT
// Vantage — Services/DialogStyle.cs
//
// ContentDialogs live in the window's popup layer, outside RootGrid, so they
// miss both the requested theme and the palette brushes ThemeManager keeps in
// RootGrid.Resources. This makes a dialog look like the rest of the app:
// same light/dark theme, same surface colour, and the very same brush
// instances for text boxes / combo boxes (so a focused field doesn't turn
// system-grey and a palette change still reaches an open dialog).

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Vantage.Services;

public static class DialogStyle
{
    private static readonly string[] SharedPrefixes = { "TextControl", "ComboBox", "ToggleSwitch" };

    public static void Apply(ContentDialog dialog, FrameworkElement appRoot)
    {
        dialog.RequestedTheme = appRoot.ActualTheme;
        if (appRoot.Resources.TryGetValue("SurfaceElevatedBrush", out var surface) && surface is Brush brush)
        {
            dialog.Background = brush;
        }

        // Cosmetic only: a failure here must never stop the dialog from opening.
        try
        {
            foreach (var entry in appRoot.Resources)
            {
                if (entry.Key is string key
                    && entry.Value is Brush
                    && SharedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                {
                    dialog.Resources[key] = entry.Value;
                }
            }
        }
        catch
        {
        }
    }
}
