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
        private readonly ChatMessage _assistantMessage;
        private readonly AgentRunViewModel _run;

        public MainWindowAgentHooks(MainWindow self, ChatMessage assistant)
        {
            _self = self;
            _assistantMessage = assistant;
            _run = new AgentRunViewModel(a => self.DispatcherQueue.TryEnqueue(() => a()));
            // Tag the host message so the DataTemplate switches to the
            // structured view automatically.
            assistant.IsAgentRun = true;
            assistant.AgentRun = _run;
        }

        public void OnRunStarted(int displayWidth, int displayHeight)
        {
            _self.DispatcherQueue.TryEnqueue(() =>
            {
                _self.PrepareAgentWorkspace();
                _run.HeaderTitle = $"Working on your desktop ({displayWidth}×{displayHeight})";
                _run.StatusText  = "Initializing…";
                _self.MessagesList.ScrollIntoView(_assistantMessage);
            });
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
                        _self.MessagesList.ScrollIntoView(_assistantMessage);
                    }
                }
            });
        }

        public void OnRunFinished(string reason)
        {
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
                _self.MessagesList.ScrollIntoView(_assistantMessage);

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
                            if (overlapped.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                            {
                                overlapped.Restore();
                            }
                            else
                            {
                                // Force a show even if not minimized, so
                                // a fully-occluded window still receives
                                // the bring-forward attempt.
                                try { overlapped.Maximize(); overlapped.Restore(); } catch { }
                            }
                        }
                    }

                    // 3) Win32 SetForegroundWindow + BringWindowToTop +
                    //    ShowWindow(SW_RESTORE). Belt-and-suspenders on
                    //    top of the AppWindow APIs. Each call is cheap
                    //    and one of them usually succeeds.
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeInterop.ShowWindow(hwnd, NativeInterop.SW_SHOW);
                        NativeInterop.ShowWindow(hwnd, NativeInterop.SW_RESTORE);
                        NativeInterop.BringWindowToTop(hwnd);
                        NativeInterop.SetForegroundWindow(hwnd);
                    }

                    // 4) Drop focus into the composer so the user can
                    //    immediately type the next prompt.
                    try { self.InputBox.Focus(FocusState.Programmatic); } catch { }
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
                    return;
                }

                // Tiny delay between attempts — gives Win32's focus
                // arbitration a moment to settle before the next try.
                System.Threading.Thread.Sleep(80);
            }

            CommonUtils.LogDiagnostic("worker-bring-to-front-final",
                $"did-not-win-foreground after {maxAttempts} attempts; hwnd=0x" +
                (hwnd == IntPtr.Zero ? "0" : hwnd.ToString("X")));
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("worker-bring-to-front-failed",
                ex.Message);
        }
    }
}
