// SPDX-License-Identifier: MIT
// Vantage — MainWindow.AgentRun.cs
//
// Agent-run helpers: PrepareAgentWorkspace (used by hooks before the
// first screenshot so the controller stays out of its own view), and
// RunPanicMonitorAsync (explicit Escape abort sensor).

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Vantage.Services;
using Vantage.Services.Agent;
using Windows.UI.Core;
using Windows.UI.Input;

namespace Vantage;

public sealed partial class MainWindow
{
    /// <summary>
    /// Called by the agent hooks BEFORE the first screenshot so we never
    /// leak Vantage's settings, provider API key fields, or base URLs into
    /// the Anthropic image payload. Switches to the chat workspace, which
    /// contains no credentials.
    /// </summary>
    private void PrepareAgentWorkspace()
    {
        ShowPage("chat");
        MinimizeForAgentCapture();
    }

    /// <summary>
    /// Keep Vantage itself out of desktop screenshots. Besides wasting a
    /// vision turn on the controller instead of the target app, capturing
    /// the chat stream would unnecessarily send conversation history to the
    /// selected vision provider. Escape remains the always-available stop;
    /// activating Vantage from the taskbar still reveals the Stop button.
    /// </summary>
    private void MinimizeForAgentCapture()
    {
        try
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                && presenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Minimize();
            }
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("agent-minimize-failed", ex.Message);
        }
    }

    /// <summary>
    /// Escape remains an immediate manual abort. Normal keyboard and mouse
    /// activity no longer cancels the run.
    /// </summary>
    private Task RunPanicMonitorAsync(CancellationToken token)
    {
        return Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (WindowsAutomationService.IsEscapeHeld())
                    {
                        CommonUtils.LogDiagnostic("agent-manual-stop",
                            "run cancelled because Escape was pressed");
                        _responseCts?.Cancel();
                        _agentRunCts?.Cancel();
                        break;
                    }
                }
                catch
                {
                    // Ignore polling failures — never let the panic sensor crash.
                }
                try { await Task.Delay(50, token); } catch { break; }
            }
        }, token);
    }
}
