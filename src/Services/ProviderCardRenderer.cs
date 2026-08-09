// SPDX-License-Identifier: MIT
// Vantage — Services/ProviderCardRenderer.cs
//
// Builds the rich per-provider card on the AI Providers page.
// Encapsulates 370 lines of code-behind visual work that used to live
// inside MainWindow so the page can stay focused on state + dispatch.
//
// Context object carries the few things the card builder needs from the
// host: brushes looked up from the RootGrid resources, the VisionCapability
// classifier, and the callbacks the card invokes when the user toggles,
// edits, tests, or deletes a provider.

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vantage.Models;
using Windows.UI;

namespace Vantage.Services;

public sealed class ProviderCardContext
{
    public required FrameworkElement Host { get; init; }
    public required VisionCapability VisionCapability { get; init; }
    public required Action SaveProviders { get; init; }
    public required Func<Provider, Task> TestProvider { get; init; }
    public required Func<Provider, Task> DeleteProvider { get; init; }

    public Brush Brush(string key) => (Brush)Host.Resources[key];
}

public static class ProviderCardRenderer
{
    public static UIElement Build(Provider provider, ProviderCardContext ctx)
    {
        var strokeBrush     = ctx.Brush("SoftStrokeBrush");
        var subtleBrush     = ctx.Brush("SubtleSurfaceBrush");
        var accentBrush     = ctx.Brush("AccentBrush");
        var accentSoftBrush = ctx.Brush("AccentSoftBrush");
        var primaryTxt      = ctx.Brush("PrimaryTextBrush");
        var secondaryTxt    = ctx.Brush("SecondaryTextBrush");
        var mutedTxt        = ctx.Brush("MutedTextBrush");
        var dangerBrush     = ctx.Brush("DangerBrush");
        var successBrush    = ctx.Brush("SuccessBrush");

        var card = new Border
        {
            Background = ctx.Brush("SurfaceElevatedBrush"),
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // status
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // fields

        // ======================== HEADER ROW ========================
        var headerGrid = new Grid
        {
            Padding = new Thickness(16, 14, 14, 11),
            ColumnSpacing = 10
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var providerIcon = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = accentSoftBrush,
            Child = new FontIcon { FontSize = 15, Foreground = accentBrush, Glyph = "\uE967" },
        };
        Grid.SetColumn(providerIcon, 0);
        headerGrid.Children.Add(providerIcon);

        // Toggle
        var toggle = new ToggleSwitch
        {
            IsOn = provider.IsEnabled,
            OnContent = "On",
            OffContent = "Off",
            MinWidth = 0,
            Tag = provider,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (s, _) =>
        {
            if (s is ToggleSwitch ts && ts.Tag is Provider p)
            {
                p.IsEnabled = ts.IsOn;
                ctx.SaveProviders();
            }
        };
        Grid.SetColumn(toggle, 3);
        headerGrid.Children.Add(toggle);

        // Name + PROVIDER + VISION badges
        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);

        var nameBlock = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primaryTxt,
            Text = provider.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(nameBlock);

        // VISION / TEXT-ONLY badge — different fill so the user can see at
        // a glance which providers drive the desktop agent.
        var (visionLabel, visionBg, visionFg) = ComputeVisionBadge(provider, ctx.VisionCapability);
        var visionBadge = new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = visionBg,
            Child = new TextBlock
            {
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = visionFg,
                Text = visionLabel
            }
        };
        titleStack.Children.Add(visionBadge);

        // Test
        var testBtn = new Button
        {
            Padding = new Thickness(10, 5, 10, 5),
            Background = subtleBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = provider
        };
        var testIcon = new FontIcon { FontSize = 12, Foreground = secondaryTxt, Glyph = "\uE9F5" };
        testBtn.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                testIcon,
                new TextBlock
                {
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = primaryTxt,
                    Text = "Test"
                }
            }
        };
        testBtn.Click += async (s, _) =>
        {
            if (s is Button b && b.Tag is Provider p)
            {
                testIcon.Glyph = "\uE895"; // spinning
                await ctx.TestProvider(p);
            }
        };
        Grid.SetColumn(testBtn, 2);
        headerGrid.Children.Add(testBtn);

        // Trash
        var deleteBtn = new Button
        {
            Width = 32, Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = provider,
            Content = new FontIcon { FontSize = 14, Foreground = dangerBrush, Glyph = "\uE74D" }
        };
        deleteBtn.Click += async (s, _) =>
        {
            if (s is Button b && b.Tag is Provider p) await ctx.DeleteProvider(p);
        };
        Grid.SetColumn(deleteBtn, 4);
        headerGrid.Children.Add(deleteBtn);

        Grid.SetRow(headerGrid, 0);
        root.Children.Add(headerGrid);

        // ======================== STATUS ROW ========================
        var dotBrush = provider.Status switch
        {
            ProviderStatus.Ok     => successBrush,
            ProviderStatus.Failed => dangerBrush,
            _                     => mutedTxt
        };
        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(8, 0, 0, 0)),
            Padding = new Thickness(16, 6, 14, 6),
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = dotBrush },
                new TextBlock
                {
                    FontSize = 11.5,
                    Foreground = secondaryTxt,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = $"{provider.StatusText}{(string.IsNullOrEmpty(provider.LastTestedText) ? string.Empty : "  ·  " + provider.LastTestedText)}"
                }
            }
        };
        statusBorder.Child = statusRow;
        Grid.SetRow(statusBorder, 1);
        root.Children.Add(statusBorder);

        // ======================== FIELDS PANEL ========================
        var fields = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 10,
            Padding = new Thickness(16, 14, 16, 17),
        };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        var nameField = new TextBox
        {
            Header = "Display name",
            Text = provider.Name,
            Tag = ("Name", provider, nameBlock)
        };
        nameField.TextChanged += (s, _) =>
        {
            if (s is TextBox tb && tb.Tag is ValueTuple<string, Provider, TextBlock> tag
                && tag.Item1 == "Name" && tag.Item2 is Provider p)
            {
                p.Name = tb.Text;
                tag.Item3.Text = tb.Text;
                ctx.SaveProviders();
            }
        };
        Grid.SetRow(nameField, 0);
        Grid.SetColumn(nameField, 0);
        fields.Children.Add(nameField);

        var urlField = new TextBox
        {
            Header = "Base URL",
            Text = provider.BaseUrl,
            PlaceholderText = "https://api.example.com/v1",
            Tag = ("BaseUrl", provider)
        };
        urlField.TextChanged += (s, _) =>
        {
            if (s is TextBox tb && tb.Tag is ValueTuple<string, Provider> tag
                && tag.Item1 == "BaseUrl" && tag.Item2 is Provider p)
            {
                p.BaseUrl = tb.Text;
                ctx.VisionCapability.InvalidateProvider(p.Id);
                ctx.SaveProviders();
            }
        };
        Grid.SetRow(urlField, 1);
        Grid.SetColumnSpan(urlField, 2);
        fields.Children.Add(urlField);

        var keyField = new PasswordBox
        {
            Header = "API key",
            Password = provider.ApiKey,
            PlaceholderText = "Paste your API key",
            Tag = provider
        };
        keyField.PasswordChanged += (s, _) =>
        {
            if (s is PasswordBox pb && pb.Tag is Provider p)
            {
                p.ApiKey = pb.Password;
                ctx.VisionCapability.InvalidateProvider(p.Id);
                ctx.SaveProviders();
            }
        };
        Grid.SetRow(keyField, 2);
        Grid.SetColumn(keyField, 0);
        fields.Children.Add(keyField);

        var modelField = new TextBox
        {
            Header = "Default model",
            Text = provider.DefaultModel,
            PlaceholderText = "model-id",
            Tag = provider
        };
        modelField.TextChanged += (s, _) =>
        {
            if (s is TextBox tb && tb.Tag is Provider p)
            {
                p.DefaultModel = tb.Text;
                ctx.SaveProviders();
                ctx.VisionCapability.InvalidateProvider(p.Id);
            }
        };
        Grid.SetRow(modelField, 0);
        Grid.SetColumn(modelField, 1);
        fields.Children.Add(modelField);

        // Vision override (Auto / Force Yes / Force No) — bulletproof
        // escape hatch when the heuristic can't classify a custom endpoint.
        var visionCombo = new ComboBox
        {
            Header = "Vision override",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 360,
            Tag = ("Vision", provider, visionBadge),
            SelectedIndex = provider.VisionOverride switch
            {
                VisionOverride.ForceYes => 1,
                VisionOverride.ForceNo  => 2,
                _                       => 0,
            }
        };
        visionCombo.SelectionChanged += (s, _) =>
        {
            if (s is not ComboBox cb || cb.Tag is not ValueTuple<string, Provider, Border> tag) return;
            var (_, p, badge) = tag;
            p.VisionOverride = cb.SelectedIndex switch
            {
                1 => VisionOverride.ForceYes,
                2 => VisionOverride.ForceNo,
                _ => VisionOverride.Auto,
            };
            ctx.VisionCapability.InvalidateProvider(p.Id);
            UpdateVisionBadge(p, badge, ctx.VisionCapability);
            ctx.SaveProviders();
        };
        visionCombo.Items.Add("Auto (heuristic + probe)");
        visionCombo.Items.Add("Force Yes — model accepts images");
        visionCombo.Items.Add("Force No — model is text-only");
        Grid.SetRow(visionCombo, 2);
        Grid.SetColumn(visionCombo, 1);
        fields.Children.Add(visionCombo);

        Grid.SetRow(fields, 2);
        root.Children.Add(fields);

        card.Child = root;
        return card;
    }

    /// <summary>Re-stamps the vision badge with the latest verdict + override.</summary>
    public static void UpdateVisionBadge(Provider provider, Border badge, VisionCapability capability)
    {
        var (label, bg, fg) = ComputeVisionBadge(provider, capability);
        badge.Background = bg;
        if (badge.Child is TextBlock tb) tb.Text = label;
        else badge.Child = new TextBlock
        {
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = fg,
            Text = label
        };
    }

    private static (string Label, Brush Background, Brush Foreground)
        ComputeVisionBadge(Provider provider, VisionCapability capability)
    {
        var verdict = capability.Classify(provider);
        var label = verdict switch
        {
            VisionVerdict.Yes => "VISION",
            VisionVerdict.No  => "TEXT-ONLY",
            VisionVerdict.Unknown when provider.VisionOverride == VisionOverride.ForceYes => "VISION (forced)",
            VisionVerdict.Unknown when provider.VisionOverride == VisionOverride.ForceNo  => "TEXT-ONLY (forced)",
            _ => "AUTO-DETECT"
        };
        var bg = verdict switch
        {
            VisionVerdict.Yes => new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xEF, 0xE2)),
            VisionVerdict.No  => new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0xE2, 0xE2)),
            _                 => new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xEC, 0xF1))
        };
        var fg = verdict switch
        {
            VisionVerdict.Yes => new SolidColorBrush(Color.FromArgb(0xFF, 0x23, 0x7A, 0x4B)),
            VisionVerdict.No  => new SolidColorBrush(Color.FromArgb(0xFF, 0xA8, 0x32, 0x32)),
            _                 => new SolidColorBrush(Color.FromArgb(0xFF, 0x6D, 0x7D, 0x86))
        };
        return (label, bg, fg);
    }
}
