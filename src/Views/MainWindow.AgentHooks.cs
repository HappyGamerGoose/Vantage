// SPDX-License-Identifier: MIT
// Vantage — MainWindow.AgentHooks.cs
//
// IRunHooks implementation that drives the structured AgentRunViewModel
// out of an AgentStep execution. Owns the lifecycle of the visualization:
// starts a fresh run, ingests step results, finishes the run. All dispatch
// happens on the UI thread via DispatcherQueue.TryEnqueue.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vantage.Common;
using Vantage.Models;
using Vantage.Services;
using Vantage.Services.Agent;
using Windows.System;

namespace Vantage;

public sealed partial class MainWindow
{
    private sealed class MainWindowAgentHooks : IRunHooks
    {
        private readonly MainWindow _self;
        private readonly Conversation _conversation;
        private readonly ChatMessage _assistantMessage;
        private readonly AgentRunViewModel _run;

        public MainWindowAgentHooks(
            MainWindow self,
            Conversation conversation,
            ChatMessage assistant)
        {
            _self = self;
            _conversation = conversation;
            _assistantMessage = assistant;
            _run = new AgentRunViewModel(a => self.DispatcherQueue.TryEnqueue(() => a()));
            // Tag the host message so the DataTemplate switches to the
            // structured view automatically.
            assistant.IsAgentRun = true;
            assistant.AgentRun = _run;
        }

        public Task OnRunStartedAsync(
            int displayWidth,
            int displayHeight,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

            void Prepare()
            {
                _self.PrepareAgentWorkspace();
                _run.HeaderTitle = $"Working on your desktop ({displayWidth}×{displayHeight})";
                _run.StatusText  = "Initializing…";
                ScrollIntoViewIfVisible();
            }

            if (_self.DispatcherQueue.HasThreadAccess)
            {
                Prepare();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_self.DispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    completion.TrySetCanceled(ct);
                    return;
                }
                try
                {
                    Prepare();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            {
                completion.TrySetException(
                    new InvalidOperationException("UI dispatcher unavailable before screen capture."));
            }
            return completion.Task;
        }

        public void OnStepCompleted(int step, ActionResult result)
        {
            _self.DispatcherQueue.TryEnqueue(() =>
            {
                // Mark the previous running phase as resolved (most of
                // the time one OnStepCompleted = one prior action
                // finished + one new action enqueued; we surface only
                // the ones whose outcome is the agent's "executed
                // action" outcome).
                if (result.Outcome == ActionOutcome.Success
                    || result.Outcome == ActionOutcome.Done
                    || result.Outcome == ActionOutcome.Failed
                    || result.Outcome == ActionOutcome.FailedFatal)
                {
                    _run.FinishLastRunning(MapToPhaseStatus(result.Outcome));
                    if (result.Outcome == ActionOutcome.Done ||
                        result.Outcome == ActionOutcome.Success ||
                        result.Outcome == ActionOutcome.Failed ||
                        result.Outcome == ActionOutcome.FailedFatal)
                    {
                        var phase = PhaseParser.Parse(step, result);
                        _run.AddPhase(phase);
                        ScrollIntoViewIfVisible();
                    }
                }
            });
        }

        public void OnRunFinished(string reason)
        {
            // Make every completed run immediately diagnosable. The writers
            // are reopened lazily on the next run, so this keeps the normal
            // batched-write performance without hiding the final entries.
            CommonUtils.FlushLogs();
            _self.DispatcherQueue.TryEnqueue(() =>
            {
                // Final terminator — close out any in-flight phase then
                // mark the run as done. Detect success/fail from the
                // reason text since AgentS3 prefixes with "done" / "failed".
                var kind = PhaseStatus.Done;
                var kindLabel = "done";
                if (reason.StartsWith("fail", StringComparison.OrdinalIgnoreCase)
                    || reason.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                    || reason.StartsWith("halted", StringComparison.OrdinalIgnoreCase)
                    || reason.StartsWith("stopped", StringComparison.OrdinalIgnoreCase)
                    || reason.StartsWith("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    kind = PhaseStatus.Failed;
                    kindLabel = "fail";
                }
                _run.FinishLastRunning(kind);
                _run.Mutate(vm =>
                {
                    vm.IsFinished = true;
                    vm.TerminationLabel = reason;
                    vm.TerminationKind  = kindLabel;
                });
                ScrollIntoViewIfVisible();

                // Bring Vantage back to front so the user always knows
                // where the run ended. Without this the user has to alt-tab
                // back manually after every long-horizon task — and during
                // a run failure mid-task it's not obvious whether the
                // agent is still grinding or already gave up.
                //
                // Works for ALL terminal paths: Done / FailedFatal /
                // OperationCanceledException (Esc / Stop button) / generic
                // Exception — AgentS3.RunAsync funnels every exit through
                // OnRunFinished. Model-agnostic: doesn't matter which LLM
                // did the work, only that the run loop terminated.
                BringVantageToFront(_self);
            });
        }

        private static PhaseStatus MapToPhaseStatus(ActionOutcome o) => o switch
        {
            ActionOutcome.Done        => PhaseStatus.Done,
            ActionOutcome.FailedFatal => PhaseStatus.Failed,
            ActionOutcome.Failed      => PhaseStatus.Failed,
            _                         => PhaseStatus.Done,
        };

        private void ScrollIntoViewIfVisible()
        {
            if (ReferenceEquals(_self._activeConversation, _conversation))
            {
                _self.MessagesList.ScrollIntoView(_assistantMessage);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Bring-Vantage-to-front helpers (model-agnostic)
    // ════════════════════════════════════════════════════════════════════
    //
    // The agent runs LLM-driven sends against the user's desktop. While
    // it's running, the user might switch to another app to do something
    // else. When the run ends (success / failure / cancellation /
    // exception), Vantage must reliably come back to the foreground so
    // the user knows the work is done. Without this, the agent can sit
    // "running" behind a browser window with no visible progress and no
    // audible signal.
    //
    // Single Activate() is unreliable on Windows because:
    //   1. The foreground-attraction timer on user32 blocks the request
    //      if our process wasn't recently in the foreground. For
    //      long-running tasks (10+ minutes) this almost certainly failed.
    //   2. Minimize → un-minimize chain doesn't always restore focus.
    //   3. The dispatcher queue might run on a thread that isn't allowed
    //      to call SetForegroundWindow.
    //
    // We layer 4 strategies and retry the whole sequence a few times
    // with small sleeps between, so any single failure doesn't suppress
    // the rest from succeeding.

    // All Win32 P/Invoke declarations are now in Common/NativeInterop.
    // Locally we just use them through that single source of truth.

    /// <summary>
    /// Pull Vantage back to the foreground using a layered, retrying
    /// strategy. Model-agnostic — works whether the run terminated from
    /// a successful JSON action, a fatal failure, an Esc cancellation,
    /// or an unhandled exception. Safe to call from the UI thread or the
    /// dispatcher queue.
    ///
    /// Window state policy: when a task finishes, the window is
    /// <b>maximized</b>, not "Maximize then Restore". The previous
    /// `Maximize(); Restore();` pattern was a polite "show me briefly"
    /// gesture that — because `Restore()` reverts the maximized state
    /// to whatever was there before — left the user staring at a
    /// 1180x760 window every time the agent finished a step. The user
    /// explicitly asked for fully-maximized on completion, so we now
    /// end in <c>OverlappedPresenterState.Maximized</c> no matter what
    /// the pre-task state was. If the user snapped the window to a
    /// side before the task, the completion will pull it back to full
    /// screen — that's the explicit ask.
    /// </summary>
    public static void BringVantageToFront(MainWindow self, int maxAttempts = 4)
    {
        if (self is null) return;
        try
        {
            // Run on UI thread synchronously — SetForegroundWindow must
            // be called by the foreground thread, which is the user's
            // desktop session by definition; we ARE on the dispatcher
            // queue thread, but the framework cross-marshals.
            if (self.DispatcherQueue is { } dq
                && !dq.HasThreadAccess)
            {
                dq.TryEnqueue(() => BringVantageToFront(self, maxAttempts));
                return;
            }

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(self);
            CommonUtils.LogDiagnostic("worker-bring-to-front-start",
                $"hwnd=0x{(hwnd == IntPtr.Zero ? "0" : hwnd.ToString("X"))} attempts={maxAttempts}");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // 1) WinUI Activate — pulls XamlRoot + window back to
                    //    top of Z-order. Doesn't always restore focus
                    //    from a minimized state, but it's cheap.
                    self.Activate();

                    // 2) AppWindow MoveInZOrderAtTop + Show + Restore.
                    //    Designed to overcome the foreground-attraction
                    //    timer because AppWindow is part of the same
                    //    process and gets the more permissive path.
                    if (self.AppWindow is { } aw)
                    {
                        aw.MoveInZOrderAtTop();
                        aw.Show();
                        if (aw.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlapped)
                        {
                            // Bring back to full screen on task end
                            // regardless of the pre-task window state.
                            // The previous Maximize+Restore pair ended
                            // in Restored (normal) state and was
                            // shrinking the window out from under the
                            // user. Maximize is idempotent: if we're
                            // already there, this is a no-op.
                            try { overlapped.Maximize(); } catch { }
                        }
                    }

                    // 3) Win32 belt-and-suspenders on top of the
                    //    AppWindow APIs. Each call is cheap and one of
                    //    them usually succeeds. We deliberately do NOT
                    //    pass SW_RESTORE here — the previous version
                    //    did, and SW_RESTORE on a maximized window
                    //    actively reverts to normal / Restored state,
                    //    shrinking Vantage out from under the user the
                    //    moment a task ended. SW_SHOW is the right
                    //    primitive for "make sure the window is
                    //    visible": it's a no-op for a normal or
                    //    maximized window, and for a minimized one it
                    //    brings it back in its previous size (which
                    //    Windows tracks for us).
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeInterop.ShowWindow(hwnd, NativeInterop.SW_SHOW);
                        NativeInterop.BringWindowToTop(hwnd);
                        NativeInterop.SetForegroundWindow(hwnd);
                    }

                    // 4) Drop focus into the composer so the user can
                    //    immediately type the next prompt.
                    try { self.InputBox.Focus(FocusState.Keyboard); } catch { }
                }
                catch (Exception attemptEx)
                {
                    CommonUtils.LogDiagnostic("worker-bring-to-front-attempt-failed",
                        $"attempt={attempt} error={attemptEx.Message}");
                }

                // Verify we won the foreground race. If we did, skip the
                // remaining attempts (and their delays).
                if (hwnd != IntPtr.Zero && NativeInterop.GetForegroundWindow() == hwnd)
                {
                    CommonUtils.LogDiagnostic("worker-bring-to-front-success",
                        $"attempt={attempt}");
                    QueueDelayedComposerFocus(self);
                    return;
                }

                // Tiny delay between attempts — gives Win32's focus
                // arbitration a moment to settle before the next try.
                System.Threading.Thread.Sleep(80);
            }

            CommonUtils.LogDiagnostic("worker-bring-to-front-final",
                $"did-not-win-foreground after {maxAttempts} attempts; hwnd=0x" +
                (hwnd == IntPtr.Zero ? "0" : hwnd.ToString("X")));
            QueueDelayedComposerFocus(self);
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("worker-bring-to-front-failed",
                ex.Message);
        }
    }

    private static void QueueDelayedComposerFocus(MainWindow self)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(180);
            self.DispatcherQueue.TryEnqueue(() =>
            {
                try { self.InputBox.Focus(FocusState.Keyboard); } catch { }
            });
        });
    }
}
