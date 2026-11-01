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
    /// <summary>Models were added, removed or renamed: persist and refresh the selector.</summary>
    public required Action ModelsChanged { get; init; }
    public required Func<Provider, Task> TestProvider { get; init; }
    public required Func<Provider, Task> DeleteProvider { get; init; }
    /// <summary>Open a card that has no remembered state (the only provider).</summary>
    public bool ExpandByDefault { get; init; }

    public Brush Brush(string key) => (Brush)Host.Resources[key];
}

public static class ProviderCardRenderer
{
    // Rendering rebuilds the cards; open/closed state survives that here.
    private static readonly Dictionary<string, bool> s_expanded = new();

    /// <summary>Small, quiet label used above form fields instead of a body-sized header.</summary>
    public static TextBlock FieldLabel(string text, Brush foreground) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = foreground,
        Margin = new Thickness(0, 0, 0, 2),
    };

    private const double NarrowModelRowWidth = 560;

    private static void ApplyModelRowLayout(Grid row, FrameworkElement label, FrameworkElement id, FrameworkElement vision, FrameworkElement remove, bool narrow)
    {
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);
        Grid.SetColumnSpan(label, narrow ? 2 : 1);
        Grid.SetRow(id, narrow ? 1 : 0);
        Grid.SetColumn(id, narrow ? 0 : 1);
        Grid.SetColumnSpan(id, narrow ? 4 : 1);
        Grid.SetRow(vision, 0);
        Grid.SetColumn(vision, 2);
        Grid.SetRow(remove, 0);
        Grid.SetColumn(remove, 3);
    }

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

        // ======================== SUMMARY ROW ========================
        var headerGrid = new Grid
        {
            Padding = new Thickness(0, 10, 0, 10),
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        static Grid StatusLine(Brush dot, TextBlock text)
        {
            var line = new Grid { ColumnSpacing = 7 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = dot, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            return line;
        }

        static string Monogram(string name) =>
            string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

        var monogramText = new TextBlock
        {
            Text = Monogram(provider.Name),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var providerIcon = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = accentSoftBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = monogramText,
        };
        headerGrid.Children.Add(providerIcon);

        var nameBlock = new TextBlock
        {
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primaryTxt,
            Text = provider.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var dotBrush = provider.Status switch
        {
            ProviderStatus.Ok     => successBrush,
            ProviderStatus.Failed => dangerBrush,
            _                     => mutedTxt
        };
        var countText = new TextBlock { FontSize = 12, Foreground = secondaryTxt, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        void UpdateCount()
        {
            var n = provider.Models.Count(m => !string.IsNullOrWhiteSpace(m.Id));
            var tested = string.IsNullOrEmpty(provider.LastTestedText) ? string.Empty : "  ·  " + provider.LastTestedText;
            countText.Text = $"{provider.StatusText}{tested}  ·  {(n == 1 ? "1 model" : $"{n} models")}";
        }
        UpdateCount();
        var summary = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                nameBlock,
                StatusLine(dotBrush, countText)
            }
        };
        Grid.SetColumn(summary, 1);
        headerGrid.Children.Add(summary);

        var testBtn = new Button
        {
            Padding = new Thickness(12, 5, 12, 6),
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = provider,
            Content = new TextBlock { FontSize = 12.5, Foreground = primaryTxt, Text = "Test" },
        };
        testBtn.Click += async (s, _) =>
        {
            if (s is Button b && b.Tag is Provider p)
            {
                b.IsEnabled = false;
                try { await ctx.TestProvider(p); }
                finally { b.IsEnabled = true; }
            }
        };
        Grid.SetColumn(testBtn, 2);
        headerGrid.Children.Add(testBtn);

        var toggle = new ToggleSwitch
        {
            IsOn = provider.IsEnabled,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            Margin = new Thickness(0, 0, -6, 0),
            Tag = provider,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(toggle, "Use this provider");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Use this provider");
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

        // ======================== FIELDS PANEL ========================
        var fields = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 10,
            Padding = new Thickness(20, 12, 20, 12),
        };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        var nameField = new TextBox
        {
            Header = FieldLabel("Provider name", secondaryTxt),
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
                monogramText.Text = Monogram(tb.Text);
                ctx.SaveProviders();
            }
        };
        Grid.SetRow(nameField, 0);
        Grid.SetColumn(nameField, 0);
        fields.Children.Add(nameField);

        var keyField = new PasswordBox
        {
            Header = FieldLabel("API key", secondaryTxt),
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
        Grid.SetRow(keyField, 0);
        Grid.SetColumn(keyField, 1);
        fields.Children.Add(keyField);

        var urlField = new TextBox
        {
            Header = FieldLabel("Base URL", secondaryTxt),
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

        // ── Models ──────────────────────────────────────────────────
        // Each row: the label shown in the model selector, the id sent to
        // the API, and a per-model vision setting. Text edits commit when
        // the box loses focus (or on Enter) so half-typed ids never reach
        // the selector or the saved file.
        var modelsPanel = new StackPanel { Spacing = 8 };

        var modelsHeader = new Grid { ColumnSpacing = 8 };
        modelsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        modelsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        modelsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        void HeaderCell(string text, int col)
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, Foreground = secondaryTxt };
            Grid.SetColumn(t, col);
            modelsHeader.Children.Add(t);
        }
        HeaderCell("Shown in selector as", 0);
        HeaderCell("Model id", 1);
        HeaderCell("Vision", 2);

        void AddModelRow(ProviderModel model, bool focus)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var labelBox = new TextBox
            {
                Text = model.DisplayName,
                PlaceholderText = string.IsNullOrWhiteSpace(model.Id) ? "Name to show" : model.Id,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var idBox = new TextBox
            {
                Text = model.Id,
                PlaceholderText = "model-id",
                VerticalAlignment = VerticalAlignment.Center,
            };

            void CommitLabel()
            {
                var v = labelBox.Text.Trim();
                if (v == model.DisplayName) return;
                model.DisplayName = v;
                ctx.ModelsChanged();
            }
            void CommitId()
            {
                var v = idBox.Text.Trim();
                if (v == model.Id) return;
                var clash = v.Length > 0 && provider.Models.Any(m => !ReferenceEquals(m, model) && m.Id == v);
                if (v.Length == 0 || clash)
                {
                    idBox.Text = model.Id;   // revert: a model needs a unique id
                    return;
                }
                model.Id = v;
                labelBox.PlaceholderText = v;
                ctx.VisionCapability.InvalidateProvider(provider.Id);
                UpdateCount();
                ctx.ModelsChanged();
            }
            void OnEnter(object s, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                CommitLabel();
                CommitId();
                e.Handled = true;
            }
            labelBox.LostFocus += (_, _) => CommitLabel();
            idBox.LostFocus += (_, _) => CommitId();
            labelBox.KeyDown += OnEnter;
            idBox.KeyDown += OnEnter;

            var vision = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 38,
            };
            ToolTipService.SetToolTip(vision, "Auto detects image support from the model name and a live probe. Pin it if the detection is wrong.");
            vision.Items.Add("Auto-detect");
            vision.Items.Add("Accepts images");
            vision.Items.Add("Text only");
            vision.SelectedIndex = model.VisionOverride switch
            {
                VisionOverride.ForceYes => 1,
                VisionOverride.ForceNo  => 2,
                _                       => 0,
            };
            vision.SelectionChanged += (_, _) =>
            {
                model.VisionOverride = vision.SelectedIndex switch
                {
                    1 => VisionOverride.ForceYes,
                    2 => VisionOverride.ForceNo,
                    _ => VisionOverride.Auto,
                };
                ctx.VisionCapability.InvalidateProvider(provider.Id);
                ctx.ModelsChanged();
            };

            var remove = new Button
            {
                Width = 32, Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { FontSize = 13, Foreground = dangerBrush, Glyph = "" },
            };
            ToolTipService.SetToolTip(remove, "Remove this model");
            remove.Click += (_, _) =>
            {
                provider.Models.Remove(model);
                modelsPanel.Children.Remove(row);
                UpdateCount();
                ctx.ModelsChanged();
            };

            Grid.SetColumn(labelBox, 0);
            Grid.SetColumn(idBox, 1);
            Grid.SetColumn(vision, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(labelBox);
            row.Children.Add(idBox);
            row.Children.Add(vision);
            row.Children.Add(remove);
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowSpacing = 6;
            ApplyModelRowLayout(row, labelBox, idBox, vision, remove, modelsPanel.ActualWidth is > 0 and < NarrowModelRowWidth);
            row.Tag = model;
            modelsPanel.Children.Add(row);

            if (focus) idBox.Loaded += (_, _) => idBox.Focus(FocusState.Programmatic);
        }
        modelsPanel.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < NarrowModelRowWidth;
            modelsHeader.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            foreach (var row in modelsPanel.Children.OfType<Grid>())
            {
                if (row.Children.Count == 4)
                    ApplyModelRowLayout(row, (FrameworkElement)row.Children[0], (FrameworkElement)row.Children[1], (FrameworkElement)row.Children[2], (FrameworkElement)row.Children[3], narrow);
            }
        };
        foreach (var m in provider.Models) AddModelRow(m, focus: false);

        Button ModelActionButton(string glyph, string text)
        {
            var b = new Button
            {
                Padding = new Thickness(10, 5, 10, 5),
                Background = subtleBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
            };
            b.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { FontSize = 12, Foreground = secondaryTxt, Glyph = glyph },
                    new TextBlock { FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = primaryTxt, Text = text },
                }
            };
            return b;
        }

        var addModelBtn = ModelActionButton("", "Add model");
        addModelBtn.Click += (_, _) =>
        {
            var m = new ProviderModel();
            provider.Models.Add(m);
            AddModelRow(m, focus: true);
        };

        var browseBtn = ModelActionButton("", "Browse provider models");
        browseBtn.Click += async (_, _) =>
        {
            var picked = await ModelPickerDialog.ShowAsync(
                ctx.Host.XamlRoot, provider.BaseUrl, provider.ApiKey,
                provider.Models.Select(m => m.Id));
            if (picked is null || picked.Count == 0) return;

            foreach (var id in picked)
            {
                // Reuse an untouched empty row instead of leaving it behind.
                var blank = provider.Models.FirstOrDefault(m => string.IsNullOrWhiteSpace(m.Id));
                if (blank is not null)
                {
                    provider.Models.Remove(blank);
                    var staleRow = modelsPanel.Children.OfType<Grid>().FirstOrDefault(g => ReferenceEquals(g.Tag, blank));
                    if (staleRow is not null) modelsPanel.Children.Remove(staleRow);
                }
                var m = new ProviderModel { Id = id };
                provider.Models.Add(m);
                AddModelRow(m, focus: false);
            }
            UpdateCount();
            ctx.ModelsChanged();
        };

        var modelButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modelButtons.Children.Add(addModelBtn);
        modelButtons.Children.Add(browseBtn);

        var modelsSection = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        modelsSection.Children.Add(new TextBlock
        {
            Text = "Models",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primaryTxt,
        });
        modelsSection.Children.Add(modelsHeader);
        modelsSection.Children.Add(modelsPanel);
        modelsSection.Children.Add(modelButtons);
        Grid.SetRow(modelsSection, 2);
        Grid.SetColumnSpan(modelsSection, 2);
        fields.Children.Add(modelsSection);

        // Removing a provider sits at the end of its details, away from the
        // everyday Test and on/off controls in the summary row.
        var deleteBtn = new Button
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 5, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Tag = provider,
            Content = new TextBlock { FontSize = 12.5, Foreground = dangerBrush, Text = "Remove provider" },
        };
        deleteBtn.Click += async (s, _) =>
        {
            if (s is Button b && b.Tag is Provider p) await ctx.DeleteProvider(p);
        };
        Grid.SetRow(deleteBtn, 3);
        Grid.SetColumnSpan(deleteBtn, 2);
        fields.Children.Add(deleteBtn);

        var card = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            IsExpanded = s_expanded.TryGetValue(provider.Id, out var wasExpanded) ? wasExpanded : ctx.ExpandByDefault,
            Header = headerGrid,
            Content = fields,
        };
        card.Resources["ExpanderHeaderBackground"] = ctx.Brush("SurfaceElevatedBrush");
        card.Resources["ExpanderHeaderBackgroundPointerOver"] = ctx.Brush("SurfaceElevatedBrush");
        card.Resources["ExpanderHeaderBackgroundPressed"] = ctx.Brush("SurfaceElevatedBrush");
        card.Resources["ExpanderHeaderBorderBrush"] = strokeBrush;
        card.Resources["ExpanderHeaderBorderBrushPointerOver"] = strokeBrush;
        card.Resources["ExpanderHeaderBorderBrushPressed"] = strokeBrush;
        card.Resources["ExpanderContentBackground"] = ctx.Brush("SurfaceElevatedBrush");
        card.Resources["ExpanderContentBorderBrush"] = strokeBrush;
        card.Expanding += (_, _) => s_expanded[provider.Id] = true;
        card.Collapsed += (_, _) => s_expanded[provider.Id] = false;
        return card;
    }
}
