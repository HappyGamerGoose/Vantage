// SPDX-License-Identifier: MIT
// Vantage — Services/S3/Worker.cs
//
// Ported from gui_agents/s3/agents/worker.py. The reasoning agent that
// receives the user's task + a screenshot, generates a chain-of-thought
// + JSON action, and iterates until `done` or `fail` or step limit.
//
// Key differences from the original Python port:
//   - Uses JSON actions (parsed via CommonUtils.ParseAgentAction) instead
//     of Python code that gets eval'd.
//   - Grounds descriptions via VantageACI (uses LLM-based grounding).
//   - Per-step reflection via a second LMMAgent that detects cycles /
//     off-track behavior (port of the S3 reflection flow).
//   - Flushes the latest-N image attachments from message history on long
//     runs to bound context size.
//
// Long-horizon robustness:
//   - Distinguishes daily quota (TPD, no retry) from minute quota (TPM,
//     retry a few seconds) so we don't burn minutes spinning on limits
//     that take hours to reset.
//   - Tracks the last N failed action descriptions. If the agent repeats
//     the same failing description twice, the next prompt steers it toward
//     a totally different approach (Tab/Enter to navigate, or `screenshot`
//     to reassess) so it doesn't get stuck looping.
//   - Compresses the screenshot and reduces image-history cap so
//     128k-token context windows don't overflow mid-task.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vantage.Services.Agent;

public sealed class Worker
{
    private readonly LmmAgent _generator;
    private readonly LmmAgent _reflector;
    private readonly VantageACI _aci;
    private readonly WindowsAutomationService.MonitorGeometry _monitor;
    private readonly PersistentTaskContext _taskContext;
    private readonly double _temperature;
    private readonly bool _enableReflection;
    private int _turnCount;
    private int _parseFailStreak;
    private int _emptyResponseStreak;
    private const int MaxParseFailStreak = 3;
    private const int MaxEmptyStreak = 2;
    private string _lastRawPlan = "";
    private string _lastActionOutcome = "(none yet)";

    // Recent failed descriptions, last 6 only. When the agent emits two
    // entries that match (after normalization), we know it's stuck and
    // should pivot strategy.
    private readonly Queue<string> _recentFailures = new();
    private const int StuckThreshold = 2;

    // Last few successful action descriptions, kept short. Helps the
    // model reason about what worked.
    private readonly Queue<string> _recentSuccesses = new();
    private const int RecentSuccessesKept = 4;

    // Per-turn structured feedback from the ActionVerifier. When an action
    // doesn't visibly land (no foreground/window change AND no
    // screen-diff), we surface WHY it failed and what the world looks like
    // now, so the next iteration gets targeted guidance instead of
    // repeating the same mistake. Capped to 4 to keep the prompt compact.
    private readonly Queue<string> _verificationFeedback = new();
    private const int VerificationFeedbackKept = 4;
    private const int InMemoryHistoryKept = 12;

    // Counts how many consecutive actions did not visibly land. Three in a
    // row triggers a recovery nudge — different from the stuck-detector on
    // _recentFailures, which tracks identical failed descriptions. Two
    // complementary signals: same description vs. consistent no-op.
    private int _noOpStreak;
    private int _consecutiveObservationOnlyCount;
    private int _sameObservationCount;
    private string _lastObservationKey = "";

    public List<string> WorkerHistory { get; } = new();
    public List<string> Reflections { get; } = new();

    public Worker(
        LMMEngine engine,
        VantageACI aci,
        WindowsAutomationService.MonitorGeometry monitor,
        string platform,
        string taskContextKey,
        bool enableReflection = true,
        double temperature = 0.0)
    {
        _aci = aci;
        _monitor = monitor;
        _taskContext = new PersistentTaskContext(taskContextKey);

        // Prime the OS-wide context that's stable for the run — installed
        // apps and OS version — so we don't enumerate Start Menu every
        // step. This probe reads registry + Start-Menu shortcuts once
        // (~5-30 ms) and the per-step volatile capture reuses the cache.
        WorldStateProbe.PrimeSessionCache();
        _enableReflection = enableReflection;
        _temperature = temperature;

        var workerPrompt = PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory(
            platform, monitor.LogicalWidth, monitor.LogicalHeight, skippedActions: Array.Empty<string>());

        _generator = new LmmAgent(
            engine,
            workerPrompt,
            activeTextHistoryMessages: 6,
            activeVisualHistoryTurns: 2);
        _reflector = new LmmAgent(
            engine,
            PROCEDURAL_MEMORY.REFLECTION_ON_TRAJECTORY,
            activeTextHistoryMessages: 4,
            activeVisualHistoryTurns: 2);
    }

    public void BeginTask(string instruction) => _taskContext.BeginTask(instruction);

    /// <summary>
    /// Run one iteration of the worker loop: capture screenshot, prompt
    /// generator, parse JSON action, dispatch via VantageACI.
    /// Returns the action outcome and a brief description for logging.
    /// </summary>
    public async Task<ActionResult> StepAsync(string instruction, CancellationToken ct)
    {
        // Timing breakdown for the verbose log. Wall-clock per phase; lets
        // the user (and me) attribute slow steps to the right thing (LLM
        // round-trip vs local execution vs verifier). Captured even if the
        // step throws, so a slow timeout shows up here too.
        var totalSw = Stopwatch.StartNew();
        long captureMs = 0, reflectionMs = 0, generatorMs = 0;
        long groundingMs = 0, executeMs = 0, verifyMs = 0;

        // Capture the screen at the monitor's LOGICAL-pixel frame, resized
        // to ≤1280 px longest side. Keeping it PNG (not JPEG) preserves
        // fine text / scrollbar arrows / dense data grids. Critically,
        // capturing at logical-pixel space (not physical) means the
        // grounding LLM's returned coords map 1:1 to the monitor's
        // logical-pixel frame on high-DPI displays — the previous
        // CaptureScreenPng (physical pixel capture) caused a 1.0-1.5x
        // scale mismatch where every click landed ~20-50% off-target
        // on a 1920x1200 panel at 125% DPI.
        var captureSw = Stopwatch.StartNew();
        var screenshotBytes = WindowsAutomationService.CaptureScreenPngLogical(maxLongestSide: 1280);
        _aci.AssignScreenshot(screenshotBytes);
        var b64 = Convert.ToBase64String(screenshotBytes);
        captureSw.Stop();
        captureMs = captureSw.ElapsedMilliseconds;
        // Local dimensions for verifier-only JPEG captures — these stay
        // small so the diff path stays cheap. We never send these to the
        // model, so JPEG quality here is fine.
        var (captureW, captureH) = VisionCap.Compute(_monitor.LogicalWidth, _monitor.LogicalHeight, maxDim: 1280);

        // On turn 0, inject the task description into the generator's
        // system prompt (the placeholder is replaced via string.Replace
        // because we built the prompt with {TASK_DESCRIPTION}).
        if (_turnCount == 0)
        {
            var withTask = _generator.SystemPrompt.Replace("{TASK_DESCRIPTION}", instruction);
            _generator.AddSystemPrompt(withTask);
        }

        // Per-step reflection — only when there's actual concern. Reflection
        // was firing EVERY step, doubling the per-step LLM latency even when
        // the trajectory was clearly on-track. We now only fire when there's
        // a stuck signal: two consecutive identical failed descriptions
        // (StuckThreshold), or two consecutive no-op streaks. For healthy
        // runs this saves ~10-15 s of LLM round-trip per step.
        bool needsReflection = _enableReflection && _turnCount > 0 && (
            _noOpStreak >= 2
            || (_recentFailures.Count >= StuckThreshold
                && _recentFailures.All(f => IsSameDescription(f, _recentFailures.First()))));
        string reflection = "";
        if (needsReflection)
        {
            var reflectSw = Stopwatch.StartNew();
            var lastPlan = WorkerHistory.Count > 0 ? WorkerHistory[^1] : "(no prior action)";
            _reflector.AddImageMessage(
                text: $"Last action: {lastPlan}\nReflect on whether the trajectory is on-track.",
                base64Jpegs: new[] { b64 },
                role: "user");
            try
            {
                var reflectionText = await CommonUtils.CallLlmSafeAsync(_reflector, temperature: _temperature, ct: ct);
                var (thoughts, answer) = CommonUtils.SplitThoughtsAnswer(reflectionText);
                // Detect the case label (1/2/3) from the reflection text
                var lower = (thoughts + " " + answer).ToLowerInvariant();
                if (lower.Contains("case 3") || lower.Contains("task complete") || lower.Contains("task has been successfully"))
                    reflection = "TASK_COMPLETE: " + answer;
                else if (lower.Contains("case 1") || lower.Contains("off-track") || lower.Contains("cycle"))
                    reflection = "OFF_TRACK: " + answer;
                else
                    reflection = "ON_TRACK: " + answer;
                Reflections.Add(reflection);
                TrimHistory(Reflections);
            }
            catch (Exception rex) when (rex is OperationCanceledException)
            {
                throw;
            }
            catch (Exception rex)
            {
                // Reflection failure shouldn't tank the agent — fall
                // back to ON_TRACK and continue.
                reflection = "ON_TRACK";
                CommonUtils.LogDiagnostic("worker-reflection-failed", rex.Message);
            }
            reflectSw.Stop();
            reflectionMs = reflectSw.ElapsedMilliseconds;
        }

        // Build the generator message with screenshot + reflection + notes
        var sbGen = new StringBuilder();
        sbGen.AppendLine(_turnCount == 0
            ? "The initial screen is provided. No action has been taken yet."
            : "");
        if (!string.IsNullOrEmpty(reflection))
            sbGen.AppendLine($"REFLECTION: {reflection}");

        // Per-step OS context — fresh foreground / cursor / windows / clipboard /
        // battery / browser tab / recent-files block. Local-only (5-25 ms).
        // The agent no longer needs to spend a turn calling list_apps /
        // list_processes / frontmost_app — that information is already here.
        var worldSw = Stopwatch.StartNew();
        var worldState = WorldStateProbe.Capture();
        worldSw.Stop();
        CommonUtils.LogDiagnostic("world-state-elapsed-ms",
            $"turn={_turnCount + 1} elapsed={worldSw.ElapsedMilliseconds}");
        sbGen.AppendLine(worldState.ToPromptBlock());
        sbGen.AppendLine(_taskContext.BuildPromptBlock());

        // Stuck-state recovery: if we've now hit the same failing
        // description twice in a row, force a strategy pivot.
        if (_recentFailures.Count >= StuckThreshold && _recentFailures.All(f => IsSameDescription(f, _recentFailures.First())))
        {
            sbGen.AppendLine(
                "STUCK_RECOVERY: Your last few attempts failed with the same description. " +
                "Pivot NOW. Do NOT repeat the same description. Try a totally different approach: " +
                "call `list_windows`, select exactly one returned window_id, then call `get_window_state`. " +
                "Use a returned accessibility element, or use app-local Tab/Shift+Tab/Enter/Escape only " +
                "through `press_window_key` after a fresh observation.");
        }
        else if (_emptyResponseStreak > 0)
        {
            sbGen.AppendLine(
                "EMPTY_RESPONSE_RECOVERY: Your previous response was empty (the model produced no text). " +
                "You MUST respond now with a <thoughts>…</thoughts><answer>```json{...}```</answer> block. " +
                "Pick any reasonable next step (a hotkey, a `screenshot`, a `wait`, or a `click` on something " +
                "you can clearly see in the FRESH screenshot above) and emit it. Do not write prose-only.");
        }
        else if (_parseFailStreak > 0 && !string.IsNullOrEmpty(_lastRawPlan))
        {
            sbGen.AppendLine(
                $"FORMAT_FEEDBACK: Your previous response could not be parsed as a JSON action. " +
                $"You MUST reply with a single <thoughts>…</thoughts><answer>```json{{...}}```</answer> block containing an \"action\" field. " +
                $"Previous response was: {Truncate(_lastRawPlan, 400)}");
        }
        else if (_lastActionOutcome.StartsWith("failed", StringComparison.OrdinalIgnoreCase)
                 && _turnCount > 0)
        {
            // The previous step was a Failed (e.g. grounding couldn't find
            // the description). Help the model pick a different approach.
            sbGen.AppendLine(
                $"GROUNDING_FAILED_RECOVERY: Your last action could not be executed: {_lastActionOutcome}. " +
                "Call `list_windows`, select one exact returned window, then `get_window_state`; use a " +
                "returned element index or an app-local key with `press_window_key`. Do not repeat the same guess.");
        }

        if (_aci.Notes.Count > 0)
            sbGen.AppendLine($"Current Text Buffer = [{string.Join(",", _aci.Notes.TakeLast(8))}]");

        if (_recentSuccesses.Count > 0)
            sbGen.AppendLine($"RECENT_SUCCESSES = [{string.Join(" | ", _recentSuccesses)}]");

        // Verification feedback from the previous turn's action — when the
        // system ran the action and observed no deterministic state change
        // (no foreground/window delta, no screen-diff), the verifier hands
        // back a structured reason. Surfacing it to the model here is
        // what makes "agent makes correct moves regardless of model" a
        // reality: a small model that picks the wrong target gets
        // immediate, factual feedback rather than guessing for several
        // turns in a row.
        if (_verificationFeedback.Count > 0)
            sbGen.AppendLine($"VERIFICATION_FEEDBACK = [{string.Join(" | ", _verificationFeedback)}]");

        _generator.AddImageMessage(sbGen.ToString(), new[] { b64 }, role: "user");

        // Diagnostic: dump the full prompt (image base64 redacted) BEFORE
        // the LLM call. This is what the model actually sees — when the
        // agent goes "dumb", the persisted file is the ground truth of
        // why (huge context, conflicting system guidance, no example, etc.).
        CommonUtils.LogVerbose($"generator-input turn={_turnCount + 1}",
            "PROMPT TEXT (image base64 redacted):\n" + Truncate(sbGen.ToString(), 4000) +
            $"\n[image base64 len={b64.Length}]");

        // Generate plan + next action. Daily-quota hits bubble out as a
        // soft FailedFatal so the user sees a clear message instead of
        // the agent spinning on a 24-minute back-off.
        var generatorSw = Stopwatch.StartNew();
        string plan;
        try
        {
            plan = await CommonUtils.CallLlmSafeAsync(_generator, temperature: _temperature, ct: ct);
        }
        catch (LlmRateLimitException rlex) when (rlex.Kind == RateLimitKind.DailyQuota)
        {
            _turnCount++;
            _taskContext.RecordSystemEvent("The provider daily quota stopped the prior turn before a desktop action ran");
            return new ActionResult(ActionOutcome.FailedFatal, rlex.Message);
        }
        catch (LlmRateLimitException rlex)
        {
            _turnCount++;
            _taskContext.RecordSystemEvent("The provider rate limit stopped the prior turn before a desktop action ran");
            return new ActionResult(ActionOutcome.Failed, rlex.Message);
        }
        catch (LmmProviderException pex)
        {
            _turnCount++;
            _taskContext.RecordSystemEvent("The provider rejected the prior turn before a desktop action ran");
            return new ActionResult(ActionOutcome.Failed,
                $"Provider rejected the request (HTTP {pex.HttpStatus}): {pex.Message}");
        }
        finally
        {
            generatorMs = generatorSw.ElapsedMilliseconds;
        }

        WorkerHistory.Add(plan);
        TrimHistory(WorkerHistory);
        _lastRawPlan = plan;

        // Diagnostic: dump the full raw model response. Includes the
        // <thoughts> block + JSON block — captures exactly what the model
        // reasoned through vs what it emitted, so we can see mismatches.
        CommonUtils.LogVerbose($"generator-output turn={_turnCount + 1}",
            "RAW MODEL RESPONSE:\n" + (plan ?? "<null>"));

        // Mirror the plan back into the generator's assistant turn so the
        // next iteration sees the full conversation
        _generator.AddTextMessage(plan!, role: "assistant");

        // Distinguish two failure modes so the feedback we send next turn
        // is targeted, not generic. An empty plan means the model returned
        // no text at all; an unparseable one means it returned text we
        // couldn't extract a JSON action from.
        var planEmpty = string.IsNullOrWhiteSpace(plan);
        if (planEmpty)
        {
            _emptyResponseStreak++;
            _parseFailStreak = 0;
            _turnCount++;
            _taskContext.RecordSystemEvent("The prior model response was empty; no desktop action ran");
            FlushHistory();
            CommonUtils.LogDiagnostic("worker-empty-plan",
                $"turn={_turnCount} empty_streak={_emptyResponseStreak} last_outcome={_lastActionOutcome}");

            if (_emptyResponseStreak >= MaxEmptyStreak)
                return new ActionResult(ActionOutcome.FailedFatal,
                    $"aborted after {_emptyResponseStreak} consecutive empty LLM responses. See %LOCALAPPDATA%\\Vantage\\agent-debug.log for raw provider output.");

            return new ActionResult(ActionOutcome.Failed,
                "LLM returned an empty response. Will prompt for a JSON action next turn.");
        }
        _emptyResponseStreak = 0;

        // Parse JSON action out of the response
        var jsonText = CommonUtils.ExtractLastJsonBlock(plan!);
        var action = CommonUtils.ParseAgentAction(jsonText);
        if (action is null)
        {
            _parseFailStreak++;
            _turnCount++;
            _taskContext.RecordSystemEvent("The prior model response could not be parsed; no desktop action ran");
            FlushHistory();

            // Give up after consecutive parse failures — prevents an
            // infinite loop where the worker spits back text-only
            // responses forever.
            if (_parseFailStreak >= MaxParseFailStreak)
                return new ActionResult(ActionOutcome.FailedFatal,
                    $"aborted after {_parseFailStreak} consecutive unparseable responses. Last plan excerpt: {Truncate(plan!, 200)}");

            return new ActionResult(ActionOutcome.Failed,
                $"worker response did not contain a parseable JSON action. Plan excerpt: {Truncate(plan!, 200)}");
        }
        _parseFailStreak = 0;

        if (CheckObservationLoop(action) is { } observationLoopResult)
        {
            _lastActionOutcome = $"{observationLoopResult.Outcome}: {observationLoopResult.Description}";
            _verificationFeedback.Enqueue(observationLoopResult.Description);
            while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
            _recentFailures.Enqueue(TryDescribeAction(action));
            while (_recentFailures.Count > 6) _recentFailures.Dequeue();
            _taskContext.RecordAction(action.Action, action.Raw, observationLoopResult);
            _turnCount++;
            FlushHistory();
            CommonUtils.LogDiagnostic("worker-observation-loop-rejected",
                $"turn={_turnCount} action={action.Action} consecutive={_consecutiveObservationOnlyCount} same={_sameObservationCount} outcome={observationLoopResult.Outcome}");
            return observationLoopResult;
        }

        // Dispatch via VantageACI (which grounds coordinates and calls
        // WindowsAutomationService). Before / after we snapshot the world
        // + the screen — the ActionVerifier uses those to decide whether
        // the action actually landed. For terminator actions (`done` /
        // `fail`) the diff has no useful signal AND the run is wrapping
        // up anyway, so we skip both captures entirely (saves 100-500 ms
        // per step on the long tail). `wait`, `screenshot`, observation
        // tools, pure-cursor movement, etc. are also skip-eligible: they
        // don't modify the desktop state in a way the verifier can test.
        var skipVerification =
            string.Equals(action.Action, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Action, "fail", StringComparison.OrdinalIgnoreCase)
            // `wait` doesn't change state — no useful diff to compute, and
            // there's no verification meaningful to do (the wait itself is
            // the action's only effect).
            || string.Equals(action.Action, "wait", StringComparison.OrdinalIgnoreCase)
            // `screenshot` is a no-op that already produced a fresh capture
            // at the top of the step. Verifier's diff would be 0/0.
            || string.Equals(action.Action, "screenshot", StringComparison.OrdinalIgnoreCase)
            // Pure-movement (cursor only, no click) — foreground and window
            // state don't change.
            || string.Equals(action.Action, "move_mouse", StringComparison.OrdinalIgnoreCase)
            // Observation tools — they READ state, never modify it.
            || string.Equals(action.Action, "list_apps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Action, "list_processes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Action, "frontmost_app", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Action, "displays", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Action, "cursor_position", StringComparison.OrdinalIgnoreCase)
            // Clipboard READ — doesn't mutate UI state.
            || string.Equals(action.Action, "vantage_get_clipboard", StringComparison.OrdinalIgnoreCase)
            // save_to_knowledge only writes to our local knowledge DB.
            || string.Equals(action.Action, "save_to_knowledge", StringComparison.OrdinalIgnoreCase);

        // the action actually landed. The first screenshot in this turn
        // (screenshotBytes) was captured BEFORE the reflection prompt
        // fired; we re-capture here so the diff is "immediately before
        // the action" vs "immediately after", not 5-second-stale.
        WorldSnapshot? preSnapshot = null;
        byte[]? preScreenshot = null;

        // Pre-dispatch safety net: if the model emitted a screen-target
        // action without enough information to actually land it
        // (no description AND no coordinates), the naive flow would
        // dispatch a guaranteed-no-op action — and the agent would
        // typically immediately give up with `done`. Catch those here
        // and substitute a forced screenshot so the next turn starts
        // from a fresh look at the screen instead of a dead end.
        var actionWasSanitized = false;
        var (replacement, sanitizationFeedback) = ActionSanitizer.Sanitize(action);
        if (replacement is not null && sanitizationFeedback is not null)
        {
            actionWasSanitized = true;
            CommonUtils.LogDiagnostic("worker-sanitized-action",
                $"turn={_turnCount + 1} original={action.Action} → forced screenshot " +
                $"raw=({Truncate(TryDescribeAction(action), 120)})");
            action = replacement;
            _verificationFeedback.Enqueue(sanitizationFeedback);
            while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
            _noOpStreak++;
            // The replacement screenshot is an observation-only marker.
            // Preserve the no-op signal without paying for a meaningless
            // before/after capture around the marker itself.
            skipVerification = true;
        }

        if (!skipVerification)
        {
            preSnapshot = WorldSnapshot.Capture();
            try { preScreenshot = WindowsAutomationService.CaptureScreenJpeg(captureW, quality: 65); }
            catch { /* diff falls back to world-only signal */ }
        }

        var executeSw = Stopwatch.StartNew();
        var result = await _aci.ExecuteAsync(action, ct);
        executeSw.Stop();
        executeMs = executeSw.ElapsedMilliseconds;
        // GroundAsync runs zero or more times inside ExecuteAsync; surfaces
        // its accumulated latency so per-step logs can show how much of the
        // budget the grounding LLM chewed.
        groundingMs = _aci.LastGroundingMs;
        _lastActionOutcome = $"{result.Outcome}: {result.Description}";

        // Grounding was already measured separately inside _aci.ExecuteAsync
        // when it ran, but most actions don't ground — leave at 0 then.

        // If the action's dispatcher already said it failed (grounding didn't
        // find the target, PowerShell returned non-zero, click coords out of
        // bounds, etc.), there's no useful state delta for the verifier —
        // the world didn't change. Skip the post-capture + diff entirely.
        // Saves ~150 ms per failed step (every failed grounding call would
        // otherwise chain a wasted capture + diff + verifier pass).
        bool outcomeFailed = result.Outcome == ActionOutcome.Failed
                          || result.Outcome == ActionOutcome.FailedFatal;
        bool needVerify = !skipVerification && !outcomeFailed;

        // Post-snapshot + post-screenshot for the verifier. Skipped for
        // terminator actions (no diff to compute) but still run for
        // observation tools so we can verify they returned valid data.
        WorldSnapshot? postSnapshot = null;
        byte[]? postScreenshot = null;
        if (needVerify)
        {
            postSnapshot = WorldSnapshot.Capture();
            try { postScreenshot = WindowsAutomationService.CaptureScreenJpeg(captureW, quality: 65); }
            catch { /* diff falls back to world-only signal */ }
        }
        var worldDiff = needVerify
            ? WorldDiff.Between(preSnapshot!, postSnapshot!)
            : default;
        ScreenshotDiff screenDiff = (preScreenshot is not null && postScreenshot is not null)
            ? ScreenshotDiffer.Diff(preScreenshot, postScreenshot)
            : new ScreenshotDiff(0, 0, 0, 0, "(skip — capture unavailable)", TimeSpan.Zero);

        var verifySw = Stopwatch.StartNew();
        var verdict = !needVerify
            ? new VerificationResult(true, action.Action,
                skipVerification
                    ? "terminator — skipped verification"
                    : "action dispatched with Failure result — verification not informative")
            : ActionVerifier.Verify(
                action.Action,
                action.Raw,
                preSnapshot!,
                postSnapshot!,
                worldDiff!,
                screenDiff);
        verifySw.Stop();
        verifyMs = verifySw.ElapsedMilliseconds;

        // Self-critique: capture the model's `expected_state` + `confidence`
        // and compare against actual outcome. If the model said `high`
        // confidence but the verifier says it didn't land, that's the
        // strongest possible signal of a hallucination — surface it as
        // dedicated feedback.
        var (predicted, confidence) = ExtractExpectedState(action.Raw);
        if (!verdict.Met && result.Outcome != ActionOutcome.Done && result.Outcome != ActionOutcome.FailedFatal)
        {
            // The dispatcher said "ok" but the world didn't agree. Capture
            // this as structured feedback for the next prompt + log to the
            // diagnostic file so the user can debug what the model tried
            // vs what actually happened.
            var fb = $"action '{action.Action}'=({Truncate(TryDescribeAction(action), 80)}) did not land: {verdict.Reason}";
            if (!string.IsNullOrWhiteSpace(predicted))
            {
                fb += $" Your prediction was: \"{Truncate(predicted, 96)}\".";
                // High confidence + mismatch = hallucination; lean on the model hard.
                if (string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase))
                {
                    fb += " CALIBRATION: you said high confidence but the world didn't change. " +
                          "Drop confidence to low and verify the target with a fresh screenshot before retrying.";
                    _verificationFeedback.Enqueue(fb);
                    while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
                    CommonUtils.LogDiagnostic("worker-self-critique-mismatch",
                        $"turn={_turnCount + 1} predicted={Truncate(predicted, 120)} confidence=high reason={Truncate(verdict.Reason, 160)}");
                }
                else
                {
                    _verificationFeedback.Enqueue(fb);
                    while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
                }
            }
            else
            {
                // The model didn't include expected_state — gentle nudge to start.
                _verificationFeedback.Enqueue(
                    "PROMPT_HINT: please include `expected_state` (predicted outcome) and `confidence` in every action. " +
                    "The verifier checks your prediction against actual state; mismatched predictions get fed back so you can recalibrate.");
                while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
                _verificationFeedback.Enqueue(fb);
                while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
            }
            _noOpStreak++;
            CommonUtils.LogDiagnostic("worker-verification-failed",
                $"turn={_turnCount + 1} action={action.Action} " +
                $"worldDiff=({worldDiff}) screenDiff={screenDiff.TotalChangeRatio:P3} " +
                $"reason={Truncate(verdict.Reason, 200)} " +
                $"before=({preSnapshot!.Compact()}) after=({postSnapshot!.Compact()})");
        }
        else
        {
            if (!actionWasSanitized)
            {
                // Reset on every verified success, including actions that
                // supplied expected_state. Previously those successes left
                // an old no-op streak alive and could trigger false recovery.
                _noOpStreak = 0;
            }
            if (!string.IsNullOrWhiteSpace(predicted))
            {
                CommonUtils.LogDiagnostic("worker-self-critique-match",
                    $"predicted={Truncate(predicted, 120)} confidence={confidence} action={action.Action}");
            }
        }

        // When we see 3+ consecutive no-ops, inject a stronger recovery
        // hint. The existing STUCK_RECOVERY handles identical failed
        // descriptions; this handles "varied actions that all fail to land"
        // (e.g. model is hallucinating targets).
        if (_noOpStreak >= 5)
        {
            _verificationFeedback.Enqueue(
                "RECOVERY_NUDGE: 5 consecutive actions did not visibly change state. " +
                "Call `list_windows`, choose exactly one returned window, then `get_window_state`. " +
                "Use a returned element index or stop if no safe target is available.");
            while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
        }
        if (_noOpStreak >= 10
            && result.Outcome is not ActionOutcome.Done and not ActionOutcome.FailedFatal)
        {
            result = new ActionResult(
                ActionOutcome.FailedFatal,
                "stopped after 10 consecutive actions produced no verified progress");
            _lastActionOutcome = $"{result.Outcome}: {result.Description}";
            CommonUtils.LogDiagnostic("worker-no-progress-stop",
                $"turn={_turnCount + 1} streak={_noOpStreak}");
        }

        // Grounding-loop guard: the grounding LLM just failed twice on the
        // same description. Rather than burn another full grounding round
        // trip on a hallucinated target, force the model to take a fresh
        // screenshot via a forced `screenshot` action (overrides the next
        // step's plan in the sanitizer). The user sees the agent stop
        // clicking random spots and pause to re-orient.
        if (_aci.ConsecutiveGroundFailures >= 2)
        {
            CommonUtils.LogDiagnostic("worker-grounding-loop",
                $"turn={_turnCount + 1} target=\"{_aci.LastGroundDescription}\" " +
                $"consecutive_failures={_aci.ConsecutiveGroundFailures}");
            _verificationFeedback.Enqueue(
                $"GROUNDING_LOOP: the grounding LLM returned no coordinates twice in a row for \"{_aci.LastGroundDescription}\". " +
                "The target isn't where you thought. Call `list_windows`, select one exact returned window, " +
                "then `get_window_state`; use a returned element index, or `launch_app` if the app is absent. " +
                "Stop guessing click coordinates — they keep failing.");
            while (_verificationFeedback.Count > VerificationFeedbackKept) _verificationFeedback.Dequeue();
            // Reset the counter so the next step starts fresh. The next
            // generator call will see the GROUNDING_LOOP feedback and plan
            // a screenshot-or-hotkey action instead of another failed click.
            _aci.ResetGroundFailureTracking();
        }

        // Track failures & successes for stuck-detection and history.
        var effectiveFailure = actionWasSanitized
            || ((result.Outcome == ActionOutcome.Success || result.Outcome == ActionOutcome.Done)
                && !verdict.Met);
        if (!effectiveFailure
            && (result.Outcome == ActionOutcome.Success || result.Outcome == ActionOutcome.Done))
        {
            PushRecentSuccess(TryDescribeAction(action));
            // Successful action clears the failure streak.
            _recentFailures.Clear();
        }
        else if (effectiveFailure
            || result.Outcome == ActionOutcome.Failed
            || result.Outcome == ActionOutcome.FailedFatal)
        {
            var desc = TryDescribeAction(action);
            if (!string.IsNullOrEmpty(desc))
            {
                _recentFailures.Enqueue(desc);
                while (_recentFailures.Count > 6) _recentFailures.Dequeue();
            }
        }

        var taskResult = effectiveFailure
            && result.Outcome is ActionOutcome.Success or ActionOutcome.Done
                ? new ActionResult(
                    ActionOutcome.Failed,
                    actionWasSanitized
                        ? "the planned action was replaced because it could not be executed reliably"
                        : $"verification did not confirm progress: {verdict.Reason}")
                : result;
        _taskContext.RecordAction(action.Action, action.Raw, taskResult);
        _turnCount++;
        FlushHistory();
        // Per-phase wall-clock log — the easiest way to attribute slow
        // steps to the right thing (LLM round-trip vs local execution
        // vs verifier). Logged at TRACE-style so it doesn't drown the
        // single-line agent-debug.log; show `worker-step-timing`.
        totalSw.Stop();
        CommonUtils.LogVerbose($"worker-step-timing turn={_turnCount}",
            $"total={totalSw.ElapsedMilliseconds}ms " +
            $"capture={captureMs}ms " +
            $"reflection={reflectionMs}ms " +
            $"generator={generatorMs}ms " +
            $"grounding={groundingMs}ms " +
            $"execute={executeMs}ms " +
            $"verify={verifyMs}ms " +
            $"outcome={result.Outcome}");
        return result;
    }

    private static string TryDescribeAction(AgentAction? action)
    {
        if (action is null) return "";
        var desc = action.GetString("description");
        if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim().ToLowerInvariant();
        // Fallback — use the action name + a few key params so distinct
        // keystroke combos still look distinct to the stuck detector.
        var name = action.Action;
        var keys = action.GetString("combo");
        if (!string.IsNullOrEmpty(keys)) return $"{name}|{keys}".ToLowerInvariant();
        var text = action.GetString("text");
        if (!string.IsNullOrEmpty(text)) return $"{name}|{Truncate(text, 32)}".ToLowerInvariant();
        return name.ToLowerInvariant();
    }

    private static bool IsSameDescription(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pull `expected_state` + `confidence` from the model's emitted JSON.
    /// Used by the self-critique loop to compare the model's prediction
    /// against the actual outcome — a mismatch on a high-confidence claim
    /// is the strongest hallucination signal the system can produce.
    /// </summary>
    private static (string Expected, string Confidence) ExtractExpectedState(JsonElement raw)
    {
        try
        {
            if (raw.ValueKind != JsonValueKind.Object) return ("", "");
            string expected = "";
            string confidence = "";
            if (raw.TryGetProperty("expected_state", out var es) && es.ValueKind == JsonValueKind.String)
                expected = es.GetString() ?? "";
            if (raw.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.String)
                confidence = cf.GetString() ?? "";
            return (expected, confidence);
        }
        catch
        {
            return ("", "");
        }
    }

    private static string Normalize(string s)
    {
        var t = s.ToLowerInvariant();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t) if (char.IsLetterOrDigit(c) || c == ' ' || c == '|') sb.Append(c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private void PushRecentSuccess(string desc)
    {
        if (string.IsNullOrEmpty(desc)) return;
        _recentSuccesses.Enqueue(desc);
        while (_recentSuccesses.Count > RecentSuccessesKept) _recentSuccesses.Dequeue();
    }

    private ActionResult? CheckObservationLoop(AgentAction action)
    {
        var isObservationOnly = action.Action.Equals("get_window_state", StringComparison.OrdinalIgnoreCase)
            || action.Action.Equals("list_windows", StringComparison.OrdinalIgnoreCase);
        if (!isObservationOnly)
        {
            _consecutiveObservationOnlyCount = 0;
            _sameObservationCount = 0;
            _lastObservationKey = "";
            return null;
        }

        _consecutiveObservationOnlyCount++;
        var key = action.Action + "|" + (action.GetString("window_id") ?? "");
        if (key.Equals(_lastObservationKey, StringComparison.OrdinalIgnoreCase))
            _sameObservationCount++;
        else
        {
            _lastObservationKey = key;
            _sameObservationCount = 1;
        }

        var shouldReject = _sameObservationCount > 2 || _consecutiveObservationOnlyCount > 3;
        if (!shouldReject) return null;

        var fatal = _sameObservationCount >= 5 || _consecutiveObservationOnlyCount >= 6;
        var message =
            $"OBSERVATION_LOOP: `{action.Action}` was requested repeatedly without an intervening desktop action. " +
            "The most recent window observation is still live. Use one returned element now, use a keyboard shortcut, " +
            "or execute a deterministic batch; do not observe the same unchanged window again.";
        return new ActionResult(fatal ? ActionOutcome.FailedFatal : ActionOutcome.Failed, message);
    }

    private static void TrimHistory(List<string> history)
    {
        if (history.Count > InMemoryHistoryKept)
            history.RemoveRange(0, history.Count - InMemoryHistoryKept);
    }

    private void FlushHistory() => _generator.CompactContextWindow();

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
}

/// <summary>
/// Caps screenshot dimensions so a single image stays inside the
/// vision-capable range of the supported providers. Llama 4 Scout is
/// ~1184 px per dimension; Claude and GPT-4o are comfortable at 1280.
/// Pick the bigger of width vs height, scale the other axis preserving
/// aspect ratio, never upscale.
/// </summary>
internal static class VisionCap
{
    public static (int Width, int Height) Compute(int w, int h, int maxDim)
    {
        if (w <= 0 || h <= 0) return (w, h);
        var longest = Math.Max(w, h);
        if (longest <= maxDim) return (w, h);
        var scale = (double)maxDim / longest;
        return (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }
}
