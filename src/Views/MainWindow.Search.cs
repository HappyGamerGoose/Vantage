// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Search.cs
//
// Search handlers — filters the sidebar conversation list AND scans
// message content for the current search query. Surfaces up to 40
// SearchResult rows with snippet previews.

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vantage.Models;

namespace Vantage;

public sealed partial class MainWindow
{
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

        SearchResultsHost.Visibility = SearchResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConversationList.SelectedItem = _activeConversation;
    }

    private static bool MessageMatches(ChatMessage message, string query)
    {
        return message.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (!string.IsNullOrWhiteSpace(message.ImagePath)
                && message.ImagePath.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string BuildSnippet(ChatMessage message, string query)
    {
        var source = !string.IsNullOrWhiteSpace(message.Text)
            ? message.Text
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
        InputBox.Focus(FocusState.Programmatic);
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }
}
