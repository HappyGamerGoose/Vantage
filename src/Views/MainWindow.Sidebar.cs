// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Sidebar.cs
//
// Sidebar (pane) state, expand/collapse, page routing, Escape handler,
// composer-focus styling, and the clear-history dialog.

using System;
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
    private readonly System.Diagnostics.Stopwatch _sidebarAnimStopwatch = new();
    private double _currentWidth = 292.0;
    private double _startWidth = 292.0;
    private double _targetWidth = 292.0;
    private bool _sidebarRenderingActive;
    private const double SidebarDurationMs = 260.0;
    private double _animDurationMs = SidebarDurationMs;

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

    private void UpdateSidebarVisibility(bool animate = true)
    {
        if (SidebarColumn is null) return;

        if (!animate || !AreSystemAnimationsEnabled())
        {
            ApplySidebarImmediate(_sidebarExpanded);
            return;
        }

        StartSidebarAnimation(_sidebarExpanded);
    }

    private void StartSidebarAnimation(bool expanding)
    {
        _targetWidth = expanding ? 292.0 : 64.0;
        _startWidth = _currentWidth;

        double distance = Math.Abs(_startWidth - _targetWidth);
        // If already at target width, snap directly to avoid redundant work
        if (distance < 0.5)
        {
            ApplySidebarImmediate(expanding);
            return;
        }

        // Dynamically scale duration if reversing mid-animation so speed remains natural
        _animDurationMs = Math.Clamp(SidebarDurationMs * (distance / (292.0 - 64.0)), 120.0, SidebarDurationMs);

        ToolTipService.SetToolTip(PaneToggleButton, expanding ? "Collapse sidebar" : "Expand sidebar");
        PaneToggleGlyph.Glyph = expanding ? "\uE76B" : "\uE76C";

        ConversationPane.IsHitTestVisible = expanding;
        CollapsedSidebarCommands.IsHitTestVisible = !expanding;

        _sidebarAnimStopwatch.Restart();

        if (!_sidebarRenderingActive)
        {
            CompositionTarget.Rendering += OnSidebarRendering;
            _sidebarRenderingActive = true;
        }
    }

    private void OnSidebarRendering(object? sender, object e)
    {
        double elapsed = _sidebarAnimStopwatch.Elapsed.TotalMilliseconds;
        double progress = Math.Clamp(elapsed / _animDurationMs, 0.0, 1.0);
        double ease = EaseInOutSine(progress);

        double width = _startWidth + (_targetWidth - _startWidth) * ease;
        ApplySidebarFrame(width);

        if (progress >= 1.0)
        {
            StopSidebarAnimation();
            ApplySidebarImmediate(_sidebarExpanded);
        }
    }

    private void ApplySidebarFrame(double width)
    {
        if (SidebarColumn is null) return;

        width = Math.Clamp(width, 64.0, 292.0);
        _currentWidth = width;

        SidebarColumn.Width = new GridLength(width);
        TitlebarNavBackdrop.Width = width;

        double margin = Math.Clamp(width - 48.0, 16.0, 244.0);
        PaneToggleButton.Margin = new Thickness(margin, 0, 0, 0);

        double u = Math.Clamp((width - 64.0) / (292.0 - 64.0), 0.0, 1.0);

        // Clip the sidebar content cleanly so ConversationPane never bleeds into the workspace
        if (SidebarHost is not null)
        {
            double hostHeight = SidebarHost.ActualHeight > 0 ? SidebarHost.ActualHeight : 4000.0;
            SidebarHost.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, width, hostHeight)
            };
        }

        // BrandText: fades in as the button glides across to the right
        if (u <= 0.20)
        {
            BrandText.Opacity = 0.0;
            BrandText.Visibility = Visibility.Collapsed;
        }
        else
        {
            BrandText.Visibility = Visibility.Visible;
            BrandText.Opacity = Math.Clamp((u - 0.20) / 0.60, 0.0, 1.0);
        }

        // ConversationPane: fades in smoothly as the panel widens
        if (u <= 0.05)
        {
            ConversationPane.Opacity = 0.0;
            ConversationPane.Visibility = Visibility.Collapsed;
        }
        else
        {
            ConversationPane.Visibility = Visibility.Visible;
            ConversationPane.Opacity = Math.Clamp((u - 0.05) / 0.75, 0.0, 1.0);
        }

        // CollapsedSidebarCommands: fades out as the panel widens
        if (u >= 0.50)
        {
            CollapsedSidebarCommands.Opacity = 0.0;
            CollapsedSidebarCommands.Visibility = Visibility.Collapsed;
        }
        else
        {
            CollapsedSidebarCommands.Visibility = Visibility.Visible;
            CollapsedSidebarCommands.Opacity = Math.Clamp((0.50 - u) / 0.45, 0.0, 1.0);
        }
    }

    private void ApplySidebarImmediate(bool expanded)
    {
        StopSidebarAnimation();

        _sidebarExpanded = expanded;
        double targetWidth = expanded ? 292.0 : 64.0;
        _currentWidth = targetWidth;

        if (SidebarColumn is null) return;

        SidebarColumn.Width = new GridLength(targetWidth);
        TitlebarNavBackdrop.Width = targetWidth;
        PaneToggleButton.Margin = new Thickness(expanded ? 244.0 : 16.0, 0, 0, 0);

        ToolTipService.SetToolTip(PaneToggleButton, expanded ? "Collapse sidebar" : "Expand sidebar");
        PaneToggleGlyph.Glyph = expanded ? "\uE76B" : "\uE76C";

        BrandText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        BrandText.Opacity = expanded ? 1.0 : 0.0;

        ConversationPane.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ConversationPane.Opacity = expanded ? 1.0 : 0.0;
        ConversationPane.IsHitTestVisible = expanded;

        CollapsedSidebarCommands.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        CollapsedSidebarCommands.Opacity = expanded ? 0.0 : 1.0;
        CollapsedSidebarCommands.IsHitTestVisible = !expanded;

        if (SidebarHost is not null)
        {
            SidebarHost.Clip = null;
        }
    }

    internal void StopSidebarAnimation()
    {
        if (_sidebarRenderingActive)
        {
            CompositionTarget.Rendering -= OnSidebarRendering;
            _sidebarRenderingActive = false;
        }
        _sidebarAnimStopwatch.Stop();
    }

    private static double EaseInOutSine(double t)
    {
        return -(Math.Cos(Math.PI * t) - 1.0) / 2.0;
    }

    private static bool AreSystemAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
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
