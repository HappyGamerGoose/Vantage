// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Settings.cs
//
// Settings persistence layer for MainWindow. Conversations go through
// LocalHistoryStore (JSON file); everything else (selected model, theme
// palette, theme mode, sidebar collapsed state) goes through LocalPreferences.

using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Vantage.Models;
using Vantage.Services;
using Vantage.Services.Agent;

namespace Vantage;

public sealed partial class MainWindow
{
    private void UpdateAboutVersionText()
    {
        try
        {
            var version = typeof(App).Assembly.GetName().Version
                ?? new Version(1, 5, 86);
            AboutVersionText.Text =
                $"Vantage v{version.Major}.{version.Minor}.{version.Build} - Local-first AI control panel for Windows.";
        }
        catch
        {
            AboutVersionText.Text = "Vantage - Local-first AI control panel for Windows.";
        }
    }

    private async Task PersistAsync()
    {
        CancelPendingAutoSave();
        await PersistCoreAsync();
    }

    private async Task PersistCoreAsync()
    {
        DropEmptyAssistantMessages();
        var snapshot = CreateHistorySnapshot();

        // ConfigureAwait(false) on every await inside the persist path:
        // the gate is released by a continuation that must NOT need the
        // UI thread. If the window-close handler runs while a save is
        // in flight, it blocks the UI thread inside
        // SemaphoreSlim.Wait(); without ConfigureAwait(false) the
        // gate-releasing continuation would also try to run on the
        // UI thread, deadlock, and the close-time flush would never
        // finish — silently dropping the in-progress changes.
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _historyStore.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Persistence MUST never crash the UI. Log and keep running
            // so a transient file-system error doesn't lock the user out
            // of their app. The Window.Closing handler forces a final
            // flush on shutdown, so even a mid-run failure here still
            // gets a clean retry on close.
            CommonUtils.LogDiagnostic("conversations-persist-failed", ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Synchronous variant used by Window.Closing — we have to finish
    /// writing before the process is gone, so we can't yield.
    /// Acquires the same <c>_saveGate</c> semaphore as
    /// <see cref="PersistAsync"/> so a mid-run save can't tear the
    /// file out from under us. Without the gate, an in-flight
    /// <c>PersistAsync</c> and this method can both open
    /// <c>history.tmp</c> at the same instant, interleaving two
    /// serializations into a single corrupt file before either
    /// rename fires.
    /// </summary>
    private void PersistSync()
    {
        CancelPendingAutoSave();
        DropEmptyAssistantMessages();
        var snapshot = CreateHistorySnapshot();

        _saveGate.Wait();
        try
        {
            _historyStore.SaveSync(snapshot);
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("conversations-persist-sync-failed", ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Strip assistant messages that have no <c>Text</c> before
    /// serializing. These show up when the user fires off a prompt
    /// and then immediately closes the app (or hits Stop) before
    /// the agent's <c>RunAsync</c> appends a single token. The
    /// <c>BeginAssistantDraftAsync</c> flow creates the assistant
    /// message <i>before</i> the agent runs so the streaming bubble
    /// can render immediately, but the catch / finally that would
    /// AppendText("Stopped.") or AppendText("[Agent error] …") is
    /// bypassed on a hard close — so the message sits in memory
    /// with Text="" and would round-trip into the next launch as
    /// an empty bubble the user has no context for. Removing them
    /// here is idempotent and runs in both PersistAsync and
    /// PersistSync, so the chat history never carries no-op
    /// assistant turns across the on-disk boundary.
    /// </summary>
    private void DropEmptyAssistantMessages()
    {
        foreach (var conv in Conversations)
        {
            // Iterate from the tail so RemoveAt doesn't shift the
            // indices of the entries we still need to inspect.
            for (var i = conv.Messages.Count - 1; i >= 0; i--)
            {
                var m = conv.Messages[i];
                if (m.Role != "assistant"
                    || !string.IsNullOrWhiteSpace(m.Text)
                    || !string.IsNullOrWhiteSpace(m.ImagePath)
                    || m.AgentRunSnapshot is not null)
                {
                    continue;
                }
                conv.Messages.RemoveAt(i);
            }
        }
    }

    private List<Conversation> CreateHistorySnapshot()
    {
        return Conversations.Select(conversation => new Conversation
        {
            Id = conversation.Id,
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            Messages = new ObservableCollection<ChatMessage>(
                conversation.Messages.Select(CloneMessageForPersistence)),
        }).ToList();
    }

    private static ChatMessage CloneMessageForPersistence(ChatMessage message)
    {
        return new ChatMessage
        {
            Id = message.Id,
            Role = message.Role,
            Text = message.Text,
            ImagePath = message.ImagePath,
            CreatedAt = message.CreatedAt,
            AgentRunSnapshot = CloneAgentRunSnapshot(message.AgentRunSnapshot),
        };
    }

    private static AgentRunSnapshot? CloneAgentRunSnapshot(AgentRunSnapshot? snapshot)
    {
        if (snapshot is null) return null;

        return new AgentRunSnapshot
        {
            HeaderTitle = snapshot.HeaderTitle,
            StatusText = snapshot.StatusText,
            EvidenceSummary = snapshot.EvidenceSummary,
            IsFinished = snapshot.IsFinished,
            TerminationLabel = snapshot.TerminationLabel,
            TerminationKind = snapshot.TerminationKind,
            StepsCompleted = snapshot.StepsCompleted,
            StartedAt = snapshot.StartedAt,
            FinalDurationMs = snapshot.FinalDurationMs,
            Phases = snapshot.Phases.Select(phase => new PhaseSnapshot
            {
                Index = phase.Index,
                Kind = phase.Kind,
                Counter = phase.Counter,
                Status = phase.Status,
                Title = phase.Title,
                Subtitle = phase.Subtitle,
                StartedAt = phase.StartedAt,
                FinishedAt = phase.FinishedAt,
            }).ToList(),
            Counters = snapshot.Counters.Select(counter => new CounterSnapshot
            {
                Kind = counter.Kind,
                Count = counter.Count,
            }).ToList(),
        };
    }

    /// <summary>
    /// Final flush hook for window close + Stop button + unobserved
    /// cancellation paths. Persists conversations AND providers to disk
    /// so neither one is ever lost between sessions. Synchronous so it
    /// has to complete before the process exits.
    /// </summary>
    private void FlushStateBeforeExit()
    {
        CancelPendingProviderSave();
        try { PersistSync(); } catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("flush-persist-sync-failed", ex.Message);
        }
        try { _providerStore.Save(_providers); }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("flush-provider-save-failed", ex.Message);
        }
    }

    /// <summary>
    /// Auto-save safety net for <see cref="Conversations"/>. Every
    /// add/remove/move on the collection fires a fire-and-forget
    /// <see cref="PersistAsync"/> so a code path that forgets to
    /// await one won't silently lose structural changes. The
    /// explicit <c>await PersistAsync()</c> calls at each call site
    /// are still the primary trigger; this hook is a redundant
    /// backstop that catches the "I forgot to save" case.
    /// </summary>
    private void OnConversationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Skip the initial mass-load on startup — LoadConversationsAsync
        // fires one CollectionChanged per loaded conversation, and we
        // don't want a save storm at app launch (each save opens,
        // writes, and renames the file). The trailing
        // RemoveExtraEmptyConversations also fires events we don't
        // need to persist. The only event the safety net has to
        // catch is the one that follows a user action.
        if (!_loaded) return;
        if (e.Action == NotifyCollectionChangedAction.Reset) return;
        if (sender is not System.Collections.ObjectModel.ObservableCollection<Models.Conversation>) return;

        ScheduleAutoPersist();
    }

    private void ScheduleAutoPersist()
    {
        CancelPendingAutoSave();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = PersistAfterDelayAsync(cts);
    }

    private async Task PersistAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(350, cts.Token);
            await PersistCoreAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_autoSaveCts, cts))
            {
                _autoSaveCts = null;
            }
            cts.Dispose();
        }
    }

    private void CancelPendingAutoSave()
    {
        var pending = _autoSaveCts;
        _autoSaveCts = null;
        if (pending is null) return;
        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delayed save completed between reading and cancelling it.
        }
    }

    private void LoadSettings()
    {
        // Restore the persisted palette + theme. ApplyCurrentTheme
        // also handles late-bound UI on the Settings page (Selector
        // indices sync up once the page is first shown — see
        // PalettePicker_Loaded / ThemeModeCombo_SelectionChanged).
        try
        {
            ApplyCurrentTheme();
        }
        catch
        {
            // Theme restore is best-effort; the app can run with default
            // theme if persisted state is corrupted.
        }

        _sidebarExpanded = LocalPreferences.GetBool("SidebarExpanded", true);
        UpdateSidebarVisibility();
    }

    private bool GetSetting(string key, bool defaultValue)
    {
        return LocalPreferences.GetBool(key, defaultValue);
    }

    private string GetSetting(string key, string defaultValue)
    {
        return LocalPreferences.GetString(key, defaultValue);
    }
}
