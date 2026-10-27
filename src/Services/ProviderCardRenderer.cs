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

    public Brush Brush(string key) => (Brush)Host.Resources[key];
}

public static class ProviderCardRenderer
{
    /// <summary>Small, quiet label used above form fields instead of a body-sized header.</summary>
    public static TextBlock FieldLabel(string text, Brush foreground) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = foreground,
        Margin = new Thickness(0, 0, 0, 2),
    };

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
            CornerRadius = new CornerRadius(14),
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
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(11),
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

        // Model-count badge, e.g. "2 MODELS".
        var countText = new TextBlock
        {
            FontSize = 9.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = accentBrush,
        };
        var countBadge = new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = accentSoftBrush,
            Child = countText
        };
        void UpdateCount()
        {
            var n = provider.Models.Count(m => !string.IsNullOrWhiteSpace(m.Id));
            countText.Text = n == 1 ? "1 MODEL" : $"{n} MODELS";
        }
        UpdateCount();
        titleStack.Children.Add(countBadge);

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
            Background = subtleBrush,
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
        for (var i = 0; i < 3; i++)
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
            row.Tag = model;
            modelsPanel.Children.Add(row);

            if (focus) idBox.Loaded += (_, _) => idBox.Focus(FocusState.Programmatic);
        }
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

        Grid.SetRow(fields, 2);
        root.Children.Add(fields);

        card.Child = root;
        return card;
    }
}
