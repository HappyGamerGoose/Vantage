// SPDX-License-Identifier: MIT
// Vantage — Services/ModelPickerDialog.cs
//
// "Browse models" picker: fetches the provider's model list and lets the
// user tick any number of models, with a search box. The provider card shows
// it as a dialog. The Add provider dialog shows it as a flyout instead: only
// one ContentDialog can be open at a time, and opening a second one from
// inside Add provider crashed the app.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Vantage.Services;

public static class ModelPickerDialog
{
    /// <summary>Returns the newly ticked model ids, or null when cancelled.</summary>
    public static async Task<IReadOnlyList<string>?> ShowAsync(
        XamlRoot xamlRoot, string baseUrl, string apiKey, IEnumerable<string> alreadyAdded)
    {
        var picker = new Picker(alreadyAdded);
        var dialog = new ContentDialog
        {
            Title = "Browse models",
            Content = picker.Body,
            PrimaryButtonText = AddLabel(0),
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot,
        };
        picker.SelectionChanged += count =>
        {
            dialog.PrimaryButtonText = AddLabel(count);
            dialog.IsPrimaryButtonEnabled = count > 0;
        };

        using var cts = new CancellationTokenSource();
        dialog.Opened += async (_, _) => await picker.LoadAsync(baseUrl, apiKey, cts.Token);

        if (xamlRoot.Content is FrameworkElement appRoot) DialogStyle.Apply(dialog, appRoot);
        var result = await dialog.ShowAsync();
        cts.Cancel();
        return result == ContentDialogResult.Primary ? picker.Selected() : null;
    }

    /// <summary>
    /// The same picker in a flyout under <paramref name="anchor"/>, for use inside
    /// a dialog that is already open. Returns null when it is dismissed.
    /// </summary>
    public static Task<IReadOnlyList<string>?> ShowFlyoutAsync(
        FrameworkElement anchor, string baseUrl, string apiKey, IEnumerable<string> alreadyAdded)
    {
        var picker = new Picker(alreadyAdded);
        var add = new Button { Content = AddLabel(0), IsEnabled = false };
        try
        {
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style style)
                add.Style = style;
        }
        catch
        {
            // Cosmetic only: a plain button still works.
        }
        var cancel = new Button { Content = "Cancel" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(add);
        actions.Children.Add(cancel);

        var content = new StackPanel { Spacing = 12, Width = 440 };
        content.Children.Add(new TextBlock { Text = "Browse models", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(picker.Body);
        content.Children.Add(actions);

        // The default presenter is narrower than the list and would cut it off.
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 520.0));
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom, FlyoutPresenterStyle = presenter };

        var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource<IReadOnlyList<string>?>();
        picker.SelectionChanged += count =>
        {
            add.Content = AddLabel(count);
            add.IsEnabled = count > 0;
        };
        add.Click += (_, _) =>
        {
            done.TrySetResult(picker.Selected());
            flyout.Hide();
        };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.Opened += async (_, _) => await picker.LoadAsync(baseUrl, apiKey, cts.Token);
        flyout.Closed += (_, _) =>
        {
            cts.Cancel();
            done.TrySetResult(null);
        };
        flyout.ShowAt(anchor);
        return done.Task;
    }

    private static string AddLabel(int count) => count > 0 ? $"Add {count} selected" : "Add selected";

    // The search box, status line and ticked list that both forms show.
    private sealed class Picker
    {
        private readonly HashSet<string> _existing;
        private readonly TextBox _search = new() { PlaceholderText = "Search models", IsEnabled = false };
        private readonly TextBlock _status = new() { Text = "Loading models…", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12.5 };
        private readonly StackPanel _list = new() { Spacing = 0 };
        private readonly List<CheckBox> _boxes = new();
        private bool _loading;

        public event Action<int>? SelectionChanged;

        public StackPanel Body { get; } = new() { Spacing = 10, MinWidth = 440 };

        public Picker(IEnumerable<string> alreadyAdded)
        {
            _existing = new HashSet<string>(alreadyAdded, StringComparer.Ordinal);
            Body.Children.Add(_search);
            Body.Children.Add(_status);
            Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            _search.TextChanged += (_, _) =>
            {
                var q = _search.Text.Trim();
                foreach (var b in _boxes)
                {
                    b.Visibility = q.Length == 0 || ((string)b.Tag).Contains(q, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            };
        }

        public async Task LoadAsync(string baseUrl, string apiKey, CancellationToken ct)
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var ids = await ModelCatalogService.FetchAsync(baseUrl, apiKey, ct);
                foreach (var id in ids)
                {
                    var added = _existing.Contains(id);
                    var box = new CheckBox
                    {
                        Content = added ? $"{id}  (already added)" : id,
                        Tag = id,
                        IsChecked = added,
                        IsEnabled = !added,
                        MinHeight = 32,
                    };
                    box.Checked += (_, _) => SelectionChanged?.Invoke(Selected().Count);
                    box.Unchecked += (_, _) => SelectionChanged?.Invoke(Selected().Count);
                    _boxes.Add(box);
                    _list.Children.Add(box);
                }
                _status.Text = ids.Count == 0
                    ? "The provider returned no models. Close this and type the model id instead."
                    : $"{ids.Count} models available.";
                _search.IsEnabled = ids.Count > 0;
                if (ids.Count > 0) _search.Focus(FocusState.Programmatic);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _status.Text = ex is TaskCanceledException ? "The provider didn't answer in time." : ex.Message;
            }
        }

        public IReadOnlyList<string> Selected() =>
            _boxes.Where(b => b.IsEnabled && b.IsChecked == true).Select(b => (string)b.Tag).ToList();
    }
}
