// SPDX-License-Identifier: MIT
// Vantage — MainWindow.AgentRun.cs
//
// Agent-run helpers: PrepareAgentWorkspace (used by hooks before the
// first screenshot so we never leak credentials), and RunPanicMonitorAsync
// (Escape-hold abort sensor).

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Vantage.Services;
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
    private void PrepareAgentWorkspace() => ShowPage("chat");

    /// <summary>
    /// Polls the Escape key at ~20 Hz and aborts the agent when it's
    /// physically held down. This is the ONLY panic gesture — the previous
    /// "400 px mouse jitter within 250 ms" velocity sensor was removed
    /// because it self-sabotaged the run: even with a synthetic-input
    /// grace window, the very first 400 px of natural post-submit cursor
    /// motion on a 120-DPI panel (≈333 logical px) cancelled the agent
    /// before it could fire its first request. Escape is unambiguous, has
    /// no false-positive risk against normal interaction, and is the
    /// canonical abort gesture for a computer-use agent. Runs on a
    /// background thread, observes the linked token, and never throws.
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
