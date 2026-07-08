// SPDX-License-Identifier: MIT
// Vantage — Services/S3/AgentS3.cs
//
// Top-level port of Agent-S S3 (gui_agents/s3/agents/agent_s.py).
// Wraps the Worker with a run-loop that ticks until `done`, `fail`,
// or until the user hits Esc / Stop. No hard step cap — long-horizon
// tasks (multi-app workflows, 30+ step sequences) need to keep going
// as long as the agent makes forward progress. Cycle detection + the
// existing Stuck-recovery feedback loop cover the original safety
// concern that the 20-step cap was guarding against.
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
    // Soft cap only — emitted as a UI warning when hit. The loop continues
    // until either the task is done or the user cancels. Default 500
    // steps maps roughly to a 5-minute sustained task; large multi-app
    // workflows can blow past this without dying.
    private const int SoftStepCeiling = 500;

    public AgentS3(
        Vantage.Models.Provider provider,
        WindowsAutomationService.MonitorGeometry monitor,
        IRunHooks hooks,
        int maxSteps = 500,
        bool enableReflection = true,
        double temperature = 0.0,
        string platform = "windows")
    {
        _provider = provider;
        _monitor = monitor;
        _hooks = hooks;
        _engine = LMMEngine.Create(provider);
        _aci = new VantageACI(_engine, monitor, platform);
        _worker = new Worker(
            _engine, _aci, monitor, platform,
            maxTrajectoryLength: 4,
            enableReflection: enableReflection,
            temperature: temperature);
    }

    public async Task<string> RunAsync(string instruction, CancellationToken ct)
    {
        _hooks.OnRunStarted(_monitor.LogicalWidth, _monitor.LogicalHeight);

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
                    return sb.ToString().Trim();
                }
                if (result.Outcome == ActionOutcome.FailedFatal)
                {
                    sb.AppendLine($"fail: {result.Description}");
                    _hooks.OnRunFinished($"failed at step {step}: {result.Description}");
                    return sb.ToString().Trim();
                }

                // Soft ceiling — keep going but flag for the user. The
                // only hard stop now is cancellation, success, or fatal.
                if (step == SoftStepCeiling)
                {
                    _hooks.OnStepCompleted(step, new ActionResult(ActionOutcome.Failed,
                        $"Reached the {SoftStepCeiling}-step soft cap — continuing unless you press Stop."));
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
    void OnRunStarted(int displayWidth, int displayHeight);
    void OnStepCompleted(int step, ActionResult result);
    void OnRunFinished(string reason);
}