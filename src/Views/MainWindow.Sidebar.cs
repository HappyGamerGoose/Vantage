// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Sidebar.cs
//
// Sidebar (pane) state, expand/collapse, page routing, Escape handler,
// composer-focus styling, and the clear-history dialog.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Vantage;

public sealed partial class MainWindow
{
    private bool _sidebarExpanded = true;

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;
        UpdateSidebarVisibility();
    }

    private void SidebarToggleAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _sidebarExpanded = !_sidebarExpanded;
        UpdateSidebarVisibility();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (_responseCts is { IsCancellationRequested: false })
        {
            StopResponse();
            return;
        }

        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    private void ShowPage(string pageName)
    {
        Workspace.Visibility = pageName == "chat" ? Visibility.Visible : Visibility.Collapsed;
        SettingsGrid.Visibility = pageName == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersGrid.Visibility = pageName == "providers" ? Visibility.Visible : Visibility.Collapsed;

        ConversationList.SelectedItem = pageName == "chat" ? _activeConversation : null;

        // Settings page carries the palette picker; hidden WinUI subtrees
        // don't always fire `Loaded`, so we kick the build when the user
        // actually navigates here. The call is idempotent.
        if (pageName == "settings") BuildPalettePicker();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowPage("settings");

    private void ProvidersButton_Click(object sender, RoutedEventArgs e) => ShowPage("providers");

    private void UpdateSidebarVisibility()
    {
        if (SidebarColumn is null) return;

        if (_sidebarExpanded)
        {
            SidebarColumn.Width = new GridLength(288);
            if (ExpandedTopRow is not null) ExpandedTopRow.Visibility = Visibility.Visible;
            if (CollapsedTopStack is not null) CollapsedTopStack.Visibility = Visibility.Collapsed;
            if (MiddleSection is not null) MiddleSection.Visibility = Visibility.Visible;
            if (ExpandedBottomStack is not null) ExpandedBottomStack.Visibility = Visibility.Visible;
            if (CollapsedBottomStack is not null) CollapsedBottomStack.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarColumn.Width = new GridLength(72);
            if (ExpandedTopRow is not null) ExpandedTopRow.Visibility = Visibility.Collapsed;
            if (CollapsedTopStack is not null) CollapsedTopStack.Visibility = Visibility.Visible;
            if (MiddleSection is not null) MiddleSection.Visibility = Visibility.Collapsed;
            if (ExpandedBottomStack is not null) ExpandedBottomStack.Visibility = Visibility.Collapsed;
            if (CollapsedBottomStack is not null) CollapsedBottomStack.Visibility = Visibility.Visible;
        }
    }

    private void UpdateComposerFocus()
    {
        var accent = (Brush)RootGrid.Resources["AccentBrush"];
        var composerBorder = (Brush)RootGrid.Resources["ComposerBorderBrush"];
        InputBox.GotFocus  += (_, _) => ComposerCard.BorderBrush = accent;
        InputBox.LostFocus += (_, _) => ComposerCard.BorderBrush = composerBorder;
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear All Chat History",
            Content = "Are you sure you want to permanently delete all conversations? This action cannot be undone.",
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            Conversations.Clear();
            FilteredConversations.Clear();
            _activeConversation = null;
            MessagesList.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            ConversationTitleBlock.Text = "Vantage";

            await CreateConversationAsync();
            ShowPage("chat");
        }
    }
}
