// SPDX-License-Identifier: MIT
// Vantage — Services/ModelPickerDialog.cs
//
// "Browse models" dialog: fetches the provider's model list and lets the
// user tick any number of models, with a search box. Shared by the Add
// provider flow and the provider card.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Vantage.Services;

public static class ModelPickerDialog
{
    /// <summary>Returns the newly ticked model ids, or null when cancelled.</summary>
    public static async Task<IReadOnlyList<string>?> ShowAsync(
        XamlRoot xamlRoot, string baseUrl, string apiKey, IEnumerable<string> alreadyAdded)
    {
        var existing = new HashSet<string>(alreadyAdded, StringComparer.Ordinal);

        var search = new TextBox { PlaceholderText = "Search models", IsEnabled = false };
        var status = new TextBlock { Text = "Loading models…", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12.5 };
        var list = new StackPanel { Spacing = 0 };
        var scroller = new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var boxes = new List<CheckBox>();

        var body = new StackPanel { Spacing = 10, MinWidth = 440 };
        body.Children.Add(search);
        body.Children.Add(status);
        body.Children.Add(scroller);

        var dialog = new ContentDialog
        {
            Title = "Browse models",
            Content = body,
            PrimaryButtonText = "Add selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot,
        };

        void UpdateCount()
        {
            var n = boxes.Count(b => b.IsEnabled && b.IsChecked == true);
            dialog.PrimaryButtonText = n > 0 ? $"Add {n} selected" : "Add selected";
            dialog.IsPrimaryButtonEnabled = n > 0;
        }

        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            foreach (var b in boxes)
            {
                b.Visibility = q.Length == 0 || ((string)b.Tag).Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };

        using var cts = new CancellationTokenSource();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var ids = await ModelCatalogService.FetchAsync(baseUrl, apiKey, cts.Token);
                foreach (var id in ids)
                {
                    var added = existing.Contains(id);
                    var box = new CheckBox
                    {
                        Content = added ? $"{id}  (already added)" : id,
                        Tag = id,
                        IsChecked = added,
                        IsEnabled = !added,
                        MinHeight = 32,
                    };
                    box.Checked += (_, _) => UpdateCount();
                    box.Unchecked += (_, _) => UpdateCount();
                    boxes.Add(box);
                    list.Children.Add(box);
                }
                status.Text = ids.Count == 0
                    ? "The provider returned no models. Close this and type the model id instead."
                    : $"{ids.Count} models available.";
                search.IsEnabled = ids.Count > 0;
                if (ids.Count > 0) search.Focus(FocusState.Programmatic);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text = ex is TaskCanceledException ? "The provider didn't answer in time." : ex.Message;
            }
        };

        var result = await dialog.ShowAsync();
        cts.Cancel();
        if (result != ContentDialogResult.Primary) return null;

        return boxes.Where(b => b.IsEnabled && b.IsChecked == true).Select(b => (string)b.Tag).ToList();
    }
}
