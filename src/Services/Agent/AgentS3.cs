// SPDX-License-Identifier: MIT
// Vantage — Services/S3/AgentS3.cs
//
// Top-level port of Agent-S S3 (gui_agents/s3/agents/agent_s.py).
// Wraps the Worker with a run-loop that ticks until `done`, `fail`,
// or until the user hits Esc / Stop. Long-horizon tasks continue until the
// model finishes, fails, or the user explicitly stops the run.
//
// Emits lifecycle events back through IRunHooks so the existing
// MainWindow chat surface can render progress.

using System.Text;

namespace Vantage.Services.Agent;

public sealed class AgentS3
{
    private readonly Vantage.Models.Provider _provider;
    private readonly Worker _worker;
    private readonly VantageACI _aci;
    private readonly LMMEngine _engine;
    private readonly WindowsAutomationService.MonitorGeometry _monitor;
    private readonly IRunHooks _hooks;

    public AgentS3(
        Vantage.Models.Provider provider,
        WindowsAutomationService.MonitorGeometry monitor,
        IRunHooks hooks,
        string taskContextKey,
        bool enableReflection = true,
        double temperature = 0.0,
        string platform = "windows")
    {
        _provider = provider;
        _monitor = monitor;
        _hooks = hooks;
        _engine = LMMEngine.Create(provider);
        _aci = new VantageACI(
            _engine,
            monitor,
            platform);
        _worker = new Worker(
            _engine, _aci, monitor, platform,
            taskContextKey,
            enableReflection: enableReflection,
            temperature: temperature);
    }

    /// <summary>
    /// Returns the terminal <see cref="ActionResult"/> of the run so
    /// the caller can detect <c>FailedFatal</c> (e.g. consecutive
    /// empty LLM responses) and surface a message — without this
    /// signal, the catch blocks in
    /// <c>BeginAssistantDraftAsync</c> would never fire on a
    /// graceful fatal abort, and the assistant bubble would stay
    /// empty. Returns the failing <see cref="ActionResult"/>; throws
    /// on cancellation / generic exception (caller's existing catch
    /// blocks already handle those paths and AppendText a message).
    /// </summary>
    public async Task<ActionResult> RunAsync(string instruction, CancellationToken ct)
    {
        _worker.BeginTask(instruction);
        await _hooks.OnRunStartedAsync(
            _monitor.LogicalWidth,
            _monitor.LogicalHeight,
            ct);
        // Let the dispatcher/compositor present the sanitized chat workspace
        // before Worker takes the first screenshot.
        await Task.Delay(120, ct);

        var sb = new StringBuilder();
        try
        {
            int step = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                step++;
                var result = await _worker.StepAsync(instruction, ct);

                _hooks.OnStepCompleted(step, result);

                if (result.Outcome == ActionOutcome.Done)
                {
                    sb.AppendLine(result.Description);
                    _hooks.OnRunFinished($"done in {step} steps");
                    return result;
                }
                if (result.Outcome == ActionOutcome.FailedFatal)
                {
                    sb.AppendLine($"fail: {result.Description}");
                    _hooks.OnRunFinished($"failed at step {step}: {result.Description}");
                    return result;
                }

                // Give the desktop a beat to redraw between steps
                await Task.Delay(120, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _hooks.OnRunFinished("Stopped.");
            throw;
        }
        catch (Exception ex)
        {
            _hooks.OnRunFinished($"Error: {ex.Message}");
            throw;
        }
    }
}

public interface IRunHooks
{
    Task OnRunStartedAsync(int displayWidth, int displayHeight, CancellationToken ct);
    void OnStepCompleted(int step, ActionResult result);
    void OnRunFinished(string reason);
}
