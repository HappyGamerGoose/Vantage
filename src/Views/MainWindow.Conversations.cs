// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Conversations.cs
//
// Conversation lifecycle: load on startup, create on first message,
// activate / rename / delete, and prune legacy seeded "welcome" stubs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vantage.Models;
using Vantage.Services;
using Vantage.Services.Agent;

namespace Vantage;

public sealed partial class MainWindow
{
    private async Task LoadConversationsAsync()
    {
        try
        {
            var loaded = await _historyStore.LoadAsync();
            foreach (var conversation in loaded)
            {
                if (RemoveSeededMessages(conversation))
                {
                    conversation.Touch();
                }
                Conversations.Add(conversation);
            }
            RemoveExtraEmptyConversations();
        }
        catch
        {
            // History failed to load — start fresh. Errors are recoverable on next save.
        }
    }

    private static bool RemoveSeededMessages(Conversation conversation)
    {
        // Drop any seeded "Welcome to Vantage" messages from older versions
        // so the chat starts clean on first launch.
        if (conversation.Messages.Count == 0) return false;

        var seededIndex = -1;
        for (var i = 0; i < conversation.Messages.Count; i++)
        {
            var m = conversation.Messages[i];
            if (!string.IsNullOrEmpty(m.Text) &&
                m.Text.Contains("Welcome to Vantage", StringComparison.OrdinalIgnoreCase))
            {
                seededIndex = i;
                break;
            }
        }
        if (seededIndex < 0) return false;

        conversation.Messages.RemoveAt(seededIndex);
        return true;
    }

    private bool RemoveExtraEmptyConversations()
    {
        var emptyStubs = Conversations
            .Where(conversation => conversation.Title == "New conversation" && conversation.Messages.Count == 0)
            .ToList();
        if (emptyStubs.Count == 0) return false;
        foreach (var c in emptyStubs) Conversations.Remove(c);
        return true;
    }

    private async Task<Conversation> CreateConversationAsync()
    {
        StopResponse();

        var conversation = new Conversation
        {
            Title = "New conversation",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };

        Conversations.Insert(0, conversation);
        RefreshSearchAndConversations();
        ActivateConversation(conversation);
        await PersistAsync();
        return conversation;
    }

    private void ActivateConversation(Conversation? conversation)
    {
        _activeConversation = conversation;
        ConversationTitleBlock.Text = conversation?.Title ?? "New chat";
        ConversationSubtitleBlock.Text = conversation is null
            ? "Chat or act on your PC"
            : $"{conversation.Messages.Count} {(conversation.Messages.Count == 1 ? "message" : "messages")}  •  stored locally";
        MessagesList.ItemsSource = conversation?.Messages;
        EmptyState.Visibility = conversation is null || conversation.Messages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ConversationList.SelectedItem = conversation;

        if (conversation?.Messages.LastOrDefault() is { } lastMessage)
        {
            MessagesList.ScrollIntoView(lastMessage);
        }
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        StopResponse();
        ActivateConversation(null);
        ShowPage("chat");
        InputBox.Focus(FocusState.Programmatic);
    }

    private void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Conversation conversation)
        {
            ActivateConversation(conversation);
            ShowPage("chat");
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    private async void DeleteConversationButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteConversationAsync(_activeConversation);
    }

    private async void RenameConversationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Conversation conversation })
        {
            await RenameConversationAsync(conversation);
        }
    }

    private async void DeleteConversationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Conversation conversation })
        {
            await DeleteConversationAsync(conversation);
        }
    }

    private async Task RenameConversationAsync(Conversation conversation)
    {
        var input = new TextBox
        {
            Text = conversation.Title,
            MinWidth = 280,
            SelectionStart = 0,
            SelectionLength = conversation.Title.Length
        };

        var dialog = new ContentDialog
        {
            Title = "Rename chat",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var title = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        conversation.Title = title;
        conversation.Touch();
        MoveConversationToTop(conversation);
        RefreshSearchAndConversations();
        ActivateConversation(conversation);
        await PersistAsync();
    }

    private async Task DeleteConversationAsync(Conversation? conversation)
    {
        if (conversation is null) return;

        var index = Conversations.IndexOf(conversation);
        if (index < 0) return;

        if (conversation == _activeConversation
            || ReferenceEquals(conversation, _responseConversation))
        {
            StopResponse();
        }

        Conversations.Remove(conversation);
        PersistentTaskContext.Delete(conversation.Id);

        // When the deleted conversation was the active one, pick a
        // neighbour or fall back to empty state. Previously we
        // auto-created a new conversation here — but that made
        // "delete the only conversation" appear to do nothing because
        // a fresh blank conversation immediately replaced the deleted
        // one. Honour the user's explicit delete: drop active state.
        if (Conversations.Count == 0)
        {
            RefreshSearchAndConversations();
            ActivateConversation(null);
            await PersistAsync();
            return;
        }

        RefreshSearchAndConversations();
        ActivateConversation(conversation == _activeConversation
            ? Conversations[Math.Clamp(index, 0, Conversations.Count - 1)]
            : _activeConversation ?? Conversations[0]);
        await PersistAsync();
    }

    /// <summary>
    /// Push the main UI into its empty / no-active-conversation state.
    /// Routes through <see cref="ActivateConversation"/> so the title,
    /// subtitle, message-list binding, and EmptyState visibility all
    /// switch together — no risk of one pane getting out of sync.
    /// </summary>
    private void UpdateUiForEmptyState()
    {
        try
        {
            ActivateConversation(null);
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("empty-state-update-failed", ex.Message);
        }
    }

    private void MoveConversationToTop(Conversation conversation)
    {
        var currentIndex = Conversations.IndexOf(conversation);
        if (currentIndex > 0) Conversations.Move(currentIndex, 0);
    }

    private void NewChatAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StopResponse();
        ActivateConversation(null);
        ShowPage("chat");
        InputBox.Focus(FocusState.Programmatic);
    }
}
