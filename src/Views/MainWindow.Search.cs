// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Search.cs
//
// Search handlers — filters the sidebar conversation list AND scans
// message content for the current search query. Surfaces up to 40
// SearchResult rows with snippet previews. Smoothly animates search
// panel opening, closing, and result surfacing.

using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vantage.Models;

namespace Vantage;

public sealed partial class MainWindow
{
    private readonly System.Diagnostics.Stopwatch _searchAnimStopwatch = new();
    private bool _searchAnimating;
    private bool _searchOpening;
    private const double SearchDurationMs = 180.0;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshSearchAndConversations();
    }

    private void RefreshSearchAndConversations()
    {
        var query = SearchBox.Text.Trim();
        FilteredConversations.Clear();

        var conversations = string.IsNullOrWhiteSpace(query)
            ? Conversations
            : Conversations.Where(conversation => conversation.Contains(query));

        foreach (var conversation in conversations.OrderByDescending(conversation => conversation.UpdatedAt))
        {
            FilteredConversations.Add(conversation);
        }

        SidebarEmptyState.Text = Conversations.Count == 0 ? "No conversations yet" : "No matches";
        SidebarEmptyState.Visibility = FilteredConversations.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SearchResults.Clear();

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var conversation in Conversations.OrderByDescending(conversation => conversation.UpdatedAt))
            {
                foreach (var message in conversation.Messages)
                {
                    if (MessageMatches(message, query))
                    {
                        SearchResults.Add(new SearchResult
                        {
                            ConversationId = conversation.Id,
                            ConversationTitle = conversation.Title,
                            Message = message,
                            Snippet = BuildSnippet(message, query)
                        });
                    }

                    if (SearchResults.Count >= 40) break;
                }

                if (SearchResults.Count >= 40) break;
            }
        }

        if (SearchResults.Count > 0)
        {
            if (SearchResultsHost.Visibility != Visibility.Visible)
            {
                SearchResultsHost.Visibility = Visibility.Visible;
                SearchResultsHost.Opacity = 0.0;
                SearchResultsHost.Translation = new System.Numerics.Vector3(0, 8f, 0);
            }
            SearchResultsHost.Opacity = 1.0;
            SearchResultsHost.Translation = System.Numerics.Vector3.Zero;
        }
        else
        {
            SearchResultsHost.Visibility = Visibility.Collapsed;
        }

        ConversationList.SelectedItem = _activeConversation;
    }

    private static bool MessageMatches(ChatMessage message, string query)
    {
        return message.SearchableText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (!string.IsNullOrWhiteSpace(message.ImagePath)
                && message.ImagePath.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string BuildSnippet(ChatMessage message, string query)
    {
        var source = !string.IsNullOrWhiteSpace(message.SearchableText)
            ? message.SearchableText
            : Path.GetFileName(message.ImagePath ?? string.Empty);

        if (string.IsNullOrWhiteSpace(source)) return "Image";

        var index = source.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            return source.Length <= 110 ? source : $"{source[..107]}...";
        }

        var start = Math.Max(0, index - 36);
        var length = Math.Min(source.Length - start, 110);
        var snippet = source.Substring(start, length);

        if (start > 0) snippet = $"...{snippet}";
        if (start + length < source.Length) snippet += "...";
        return snippet;
    }

    private void SearchResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResult result) return;

        var conversation = Conversations.FirstOrDefault(item => item.Id == result.ConversationId);
        ActivateConversation(conversation);
        MessagesList.ScrollIntoView(result.Message);
        CloseConversationSearch();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusConversationSearch();
    }

    private void HeaderSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchPanel.Visibility == Visibility.Visible && string.IsNullOrEmpty(SearchBox.Text))
        {
            CloseConversationSearch();
        }
        else
        {
            FocusConversationSearch();
        }
    }

    private void FocusConversationSearch()
    {
        if (!_sidebarExpanded)
        {
            _sidebarExpanded = true;
            UpdateSidebarVisibility();
            SaveSidebarPreference();
        }

        if (SearchPanel.Visibility != Visibility.Visible || _searchAnimating)
        {
            AnimateSearchPanel(open: true);
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void CloseConversationSearch()
    {
        if (SearchPanel.Visibility != Visibility.Visible && !_searchAnimating) return;
        AnimateSearchPanel(open: false);
    }

    private void AnimateSearchPanel(bool open)
    {
        if (!AreSystemAnimationsEnabled())
        {
            StopSearchAnimation();
            SearchPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SearchPanel.Opacity = open ? 1.0 : 0.0;
            SearchPanel.Translation = System.Numerics.Vector3.Zero;
            if (!open)
            {
                SearchBox.Text = string.Empty;
                SearchResultsHost.Visibility = Visibility.Collapsed;
            }
            return;
        }

        _searchOpening = open;
        SearchPanel.Visibility = Visibility.Visible;

        _searchAnimStopwatch.Restart();
        if (!_searchAnimating)
        {
            CompositionTarget.Rendering += OnSearchRendering;
            _searchAnimating = true;
        }
    }

    private void OnSearchRendering(object? sender, object e)
    {
        double elapsed = _searchAnimStopwatch.Elapsed.TotalMilliseconds;
        double progress = Math.Clamp(elapsed / SearchDurationMs, 0.0, 1.0);
        double ease = 1.0 - Math.Pow(1.0 - progress, 3.0);

        if (_searchOpening)
        {
            SearchPanel.Opacity = ease;
            float offsetY = (float)(-10.0 * (1.0 - ease));
            SearchPanel.Translation = new System.Numerics.Vector3(0, offsetY, 0);
        }
        else
        {
            SearchPanel.Opacity = Math.Clamp(1.0 - (progress * 1.4), 0.0, 1.0);
            float offsetY = (float)(-10.0 * ease);
            SearchPanel.Translation = new System.Numerics.Vector3(0, offsetY, 0);
        }

        if (progress >= 1.0)
        {
            StopSearchAnimation();
            if (!_searchOpening)
            {
                SearchPanel.Visibility = Visibility.Collapsed;
                SearchBox.Text = string.Empty;
                SearchResultsHost.Visibility = Visibility.Collapsed;
                SearchPanel.Translation = System.Numerics.Vector3.Zero;
            }
            else
            {
                SearchPanel.Opacity = 1.0;
                SearchPanel.Translation = System.Numerics.Vector3.Zero;
            }
        }
    }

    internal void StopSearchAnimation()
    {
        if (_searchAnimating)
        {
            CompositionTarget.Rendering -= OnSearchRendering;
            _searchAnimating = false;
        }
        _searchAnimStopwatch.Stop();
    }
}
