// SPDX-License-Identifier: MIT
// Vantage — Services/BrushResolver.cs
//
// Theme-aware brush lookup used by non-UI code that needs to compute
// SolidColorBrush values without a XAML path. The active page's
// RootGrid.Resources are registered once at startup; lookups return
// the SAME SolidColorBrush instances, so when ThemeManager.Apply mutates
// `.Color` in place all the consumers update automatically.
//
// Static state is intentional: there's only one Vantage window at a time
// and the brushes it owns are app-scoped.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Vantage.Services;

public static class BrushResolver
{
    /// <summary>The ResourceDictionary the resolver looks keys up in.</summary>
    public static ResourceDictionary? Resources { get; private set; }

    /// <summary>
    /// Wire the resolver to the active window's RootGrid. Call from
    /// RootGrid_Loaded so the XAML has had a chance to populate resources.
    /// </summary>
    public static void Attach(ResourceDictionary resources)
    {
        Resources = resources;
    }

    /// <summary>
    /// Look up a brush by key. Returns null when the resolver hasn't been
    /// attached yet OR when the key isn't present — callers fall back to
    /// hard-coded defaults.
    /// </summary>
    public static Brush? TryGet(string key)
    {
        if (Resources is null) return null;
        if (Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        return null;
    }

    /// <summary>Convenience: get a SolidColorBrush with a guaranteed colour fallback.</summary>
    public static Brush GetOrDefault(string key, Windows.UI.Color fallback)
    {
        var b = TryGet(key);
        if (b is SolidColorBrush sc) return sc;
        if (b is not null) return b; // gradient or other brush types
        return new SolidColorBrush(fallback);
    }
}
