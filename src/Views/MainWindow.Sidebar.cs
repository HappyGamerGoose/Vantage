// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Sidebar.cs
//
// Sidebar (pane) state, expand/collapse, page routing, Escape handler,
// composer-focus styling, and the clear-history dialog.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vantage.Services;
using Vantage.Services.Agent;

namespace Vantage;

public sealed partial class MainWindow
{
    private bool _sidebarExpanded = true;

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;
        UpdateSidebarVisibility();
        SaveSidebarPreference();
    }

    private void SidebarToggleAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _sidebarExpanded = !_sidebarExpanded;
        UpdateSidebarVisibility();
        SaveSidebarPreference();
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
            SearchPanel.Visibility = Visibility.Collapsed;
            InputBox.Focus(FocusState.Programmatic);
            return;
        }

        if (SearchPanel.Visibility == Visibility.Visible)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            InputBox.Focus(FocusState.Programmatic);
            return;
        }

        if (Workspace.Visibility != Visibility.Visible)
        {
            ShowPage("chat");
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    private void ShowPage(string pageName)
    {
        Workspace.Visibility = pageName == "chat" ? Visibility.Visible : Visibility.Collapsed;
        SettingsGrid.Visibility = pageName == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersGrid.Visibility = pageName == "providers" ? Visibility.Visible : Visibility.Collapsed;

        ConversationList.SelectedItem = pageName == "chat" ? _activeConversation : null;

        if (RootGrid.Resources["AccentSoftBrush"] is Brush selected)
        {
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            SettingsButton.Background = pageName == "settings" ? selected : clear;
            SettingsButtonCollapsed.Background = pageName == "settings" ? selected : clear;
            ProvidersButton.Background = pageName == "providers" ? selected : clear;
            ProvidersButtonCollapsed.Background = pageName == "providers" ? selected : clear;
        }

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
            SidebarColumn.Width = new GridLength(292);
            TitlebarNavBackdrop.Width = 292;
            BrandLogo.Visibility = Visibility.Visible;
            BrandText.Visibility = Visibility.Visible;
            PaneToggleButton.Margin = new Thickness(244, 0, 0, 0);
            ToolTipService.SetToolTip(PaneToggleButton, "Collapse sidebar");
            PaneToggleGlyph.Glyph = "\uE76B";
            ConversationPane.Visibility = Visibility.Visible;
            CollapsedSidebarCommands.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarColumn.Width = new GridLength(64);
            TitlebarNavBackdrop.Width = 64;
            BrandLogo.Visibility = Visibility.Collapsed;
            BrandText.Visibility = Visibility.Collapsed;
            PaneToggleButton.Margin = new Thickness(16, 0, 0, 0);
            ToolTipService.SetToolTip(PaneToggleButton, "Expand sidebar");
            PaneToggleGlyph.Glyph = "\uE76C";
            ConversationPane.Visibility = Visibility.Collapsed;
            CollapsedSidebarCommands.Visibility = Visibility.Visible;
        }
    }

    private void SaveSidebarPreference()
    {
        LocalPreferences.SetBool("SidebarExpanded", _sidebarExpanded);
    }

    private void UpdateComposerFocus()
    {
        var focusedBorder = (Brush)RootGrid.Resources["StrokeBrush"];
        var composerBorder = (Brush)RootGrid.Resources["ComposerBorderBrush"];
        InputBox.GotFocus  += (_, _) => ComposerCard.BorderBrush = focusedBorder;
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
            StopResponse();
            PersistentTaskContext.DeleteAll();
            Conversations.Clear();
            FilteredConversations.Clear();
            _activeConversation = null;
            MessagesList.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
            ConversationTitleBlock.Text = "New chat";

            await PersistAsync();
            ShowPage("chat");
            InputBox.Focus(FocusState.Programmatic);
        }
    }
}
