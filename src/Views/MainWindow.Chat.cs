// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Chat.cs
//
// Chat send/stream logic: key-down handling on the input box, message
// persistence, agent dispatching through AgentS3, plain fallback reply
// when no provider is configured, and the Stop/responding UI state.

using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vantage.Models;
using Vantage.Services;
using Vantage.Services.Agent;
using Windows.System;
using Windows.UI.Core;

namespace Vantage;

public sealed partial class MainWindow
{
    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCurrentMessageAsync();
    }

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        var ctrlState  = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var ctrlDown   = (ctrlState  & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var shiftDown  = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        // Modifier matrix:
        //   Enter                → send (the common case)
        //   Ctrl + Enter         → send (covers IME / a11y that ate plain Enter)
        //   Shift + Enter        → insert a newline (multiline)
        //   Alt + Enter (alone)  → insert a newline (matches Discord / Slack)
        // We always mark e.Handled so the TextBox's own newline insertion
        // can't double up.
        if (shiftDown || (!ctrlDown && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down))
        {
            // Insert a newline at the caret.
            if (sender is TextBox tb)
            {
                var caret = tb.SelectionStart;
                var text = tb.Text ?? "";
                tb.Text = text.Substring(0, caret) + "\n" + text.Substring(tb.SelectionLength > 0 ? caret + tb.SelectionLength : caret);
                tb.SelectionStart = caret + 1;
            }
            e.Handled = true;
            return;
        }

        e.Handled = true;
        await SendCurrentMessageAsync();
    }

    private async Task SendCurrentMessageAsync()
    {
        var text = InputBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            InputBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_activeConversation is null) await CreateConversationAsync();
        if (_activeConversation is null) return;

        StopResponse();

        var userMessage = new ChatMessage
        {
            Role = "user",
            Text = text,
            ImagePath = null,
            CreatedAt = DateTimeOffset.Now
        };

        _activeConversation.Messages.Add(userMessage);
        ApplyTitleFromMessage(_activeConversation, userMessage);
        _activeConversation.Touch();
        MoveConversationToTop(_activeConversation);
        RefreshSearchAndConversations();
        ActivateConversation(_activeConversation);

        InputBox.Text = string.Empty;
        MessagesList.ScrollIntoView(userMessage);
        await PersistAsync();

        _ = BeginAssistantDraftAsync(_activeConversation, userMessage);
    }

    private async Task BeginAssistantDraftAsync(Conversation conversation, ChatMessage userMessage)
    {
        // Desktop automation is the default execution path for every
        // chat submission. We fall back to a plain "saved locally" reply
        // only when no Anthropic-compatible provider has an API key.

        var (activeProvider, blockedReason) = await PickActiveProviderAsync();
        if (activeProvider is null)
        {
            await EmitPlainReplyAsync(conversation, blockedReason ?? "no provider selected");
            return;
        }

        _responseCts = new CancellationTokenSource();
        var token = _responseCts.Token;
        SetResponding(true);

        var assistantMessage = new ChatMessage { Role = "assistant", CreatedAt = DateTimeOffset.Now };
        conversation.Messages.Add(assistantMessage);
        conversation.Touch();
        ActivateConversation(conversation);

        _agentRunCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panicTask = RunPanicMonitorAsync(_agentRunCts.Token);

        try
        {
            var monitor = WindowsAutomationService.GetPrimaryMonitor();
            var agentS3 = new AgentS3(
                activeProvider,
                monitor,
                new MainWindowAgentHooks(this, assistantMessage),
                maxSteps: 500,
                enableReflection: true,
                temperature: 0.0);

            await agentS3.RunAsync(
                conversation.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Text ?? "",
                _agentRunCts.Token);
        }
        catch (OperationCanceledException)
        {
            assistantMessage.AppendText(
                string.IsNullOrWhiteSpace(assistantMessage.Text) ? "\n\nStopped." : "\n\nStopped.");
        }
        catch (Exception ex)
        {
            assistantMessage.AppendText($"\n\n[Agent error] {ex.Message}");
        }
        finally
        {
            _agentRunCts.Cancel();
            try { await panicTask; } catch { /* expected on abort */ }
            _agentRunCts.Dispose();
            _agentRunCts = null;

            conversation.Touch();
            SetResponding(false);
            _responseCts?.Dispose();
            _responseCts = null;
            RefreshSearchAndConversations();
            await PersistAsync();
        }
    }

    /// <summary>
    /// Stub reply used when no Anthropic-compatible provider has an API key.
    /// Keeps the original "saved locally" tone so the chat surface still
    /// produces a useful response when the agent loop can't be entered.
    /// </summary>
    private async Task EmitPlainReplyAsync(Conversation conversation, string text)
    {
        _responseCts = new CancellationTokenSource();
        var token = _responseCts.Token;
        SetResponding(true);

        var assistantMessage = new ChatMessage { Role = "assistant", CreatedAt = DateTimeOffset.Now };
        conversation.Messages.Add(assistantMessage);
        conversation.Touch();
        ActivateConversation(conversation);

        try
        {
            foreach (var chunk in ChunkMessage(text))
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(20, token);
                assistantMessage.AppendText(chunk);
                if (_activeConversation == conversation)
                    MessagesList.ScrollIntoView(assistantMessage);
            }
        }
        catch (OperationCanceledException)
        {
            // user hit Stop — leave the partial message as-is
        }
        finally
        {
            conversation.Touch();
            SetResponding(false);
            _responseCts?.Dispose();
            _responseCts = null;
            RefreshSearchAndConversations();
            await PersistAsync();
        }
    }

    private static IEnumerable<string> ChunkMessage(string text)
    {
        for (var i = 0; i < text.Length; i += 8)
            yield return text.Substring(i, Math.Min(8, text.Length - i));
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopResponse();

    private void StopResponse()
    {
        // Echo into the model-agnostic bring-to-front path too, so the
        // window snaps back the instant the user mashes Stop / Esc —
        // we don't have to wait for the cancellation to fully unwind
        // through AgentS3 → OnRunFinished. The OnRunFinished hook will
        // ALSO bring forward (idempotent), so this is just an early
        // signal.
        bool canceling = false;
        if (_responseCts is { IsCancellationRequested: false })
        {
            _responseCts.Cancel();
            canceling = true;
        }
        if (_agentRunCts is { IsCancellationRequested: false })
        {
            _agentRunCts.Cancel();
            canceling = true;
        }
        if (canceling) BringVantageToFront(this);
    }

    private void SetResponding(bool isResponding)
    {
        if (isResponding)
        {
            SendButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = true;
            RunStatusPill.Visibility = Visibility.Visible;
            RunStatusText.Text = "Running";
            if (RootGrid.Resources["AccentBrush"] is Brush accent)
                ComposerCard.BorderBrush = accent;
            StartRunStatusPulse();
        }
        else
        {
            SendButton.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
            StopButton.IsEnabled = false;
            RunStatusPill.Visibility = Visibility.Collapsed;
            if (RootGrid.Resources["ComposerBorderBrush"] is Brush baseBorder)
                ComposerCard.BorderBrush = baseBorder;
            StopRunStatusPulse();
        }
    }

    /// <summary>
    /// Pick which Provider drives the agent for this turn. Honours the
    /// user's composer selection first; falls back to the first candidate
    /// whose vision classifier isn't a definitive `No`; finally falls
    /// back to candidates[0]. Returns (null, blockedReason) when no
    /// provider is suitable (no active+keyed providers, or the active
    /// choice has a hard vision-cap rejection).
    /// </summary>
    private async Task<(Provider? Provider, string? BlockedReason)> PickActiveProviderAsync()
    {
        var candidates = _providers
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.ApiKey))
            .ToList();
        if (candidates.Count == 0)
        {
            return (null, "No active AI provider with an API key configured yet; please enable " +
                         "and configure a provider in the AI Providers screen to run desktop commands.");
        }

        // Honour the user's explicit composer selection when it's still enabled.
        Provider? activeProvider = null;
        if (ModelSelector?.SelectedItem is ModelChoice selectedChoice
            && candidates.Any(p => p.Id == selectedChoice.Provider.Id))
        {
            activeProvider = selectedChoice.Provider;
        }
        else
        {
            // First provider whose vision verdict isn't `No` — Unknown is
            // allowed (we'll detect rejection at runtime). Cheap heuristic
            // first, live probe only as fallback.
            foreach (var p in candidates)
            {
                var verdict = await _visionCapability.SupportsAsync(p);
                if (verdict != VisionVerdict.No)
                {
                    activeProvider = p;
                    break;
                }
            }
            activeProvider ??= candidates[0];
        }

        // Hard-reject if the active provider is known text-only. Force
        // override (Auto-detection on text-only providers is allowed but
        // explicit user override applies here).
        var verdict0 = await _visionCapability.SupportsAsync(activeProvider);
        if (verdict0 == VisionVerdict.No)
        {
            return (null,
                $"The active provider \"{activeProvider.Name}\" uses model \"{activeProvider.DefaultModel}\", " +
                "which the vision classifier rejects. Vantage's desktop-control agent needs a vision-capable " +
                "model to read your screen and drive the mouse and keyboard. Either pick a vision model " +
                "(claude-sonnet-4-5, gpt-4o, gemini-2.5-pro, llama-3.2-90b-vision, pixtral-12b), or — if you " +
                "know this endpoint accepts images — flip the **Vision override** to Force Yes on the provider card.");
        }

        return (activeProvider, null);
    }
}
