// SPDX-License-Identifier: MIT
// One-off probe runner — calls WorldStateProbe.Capture() and dumps
// both the raw record and the ToPromptBlock() string the agent
// actually sees. Run with `dotnet run --project probe-worldstate`.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vantage.Services;
using Vantage.Services.Agent;

// Tests talk to mock servers, whose learned settings must not reach the real file.
LlmCapabilities.StorePath = null;
// Nor may what they log reach the user's _diag.log.
CommonUtils.LogDirectory = Path.Combine(Path.GetTempPath(), "vantage-probe-logs");

if (args.Contains("--input-self-test", StringComparer.OrdinalIgnoreCase))
{
    var valid = WindowsAppManager.ValidateNativeInputLayout();
    Console.WriteLine($"SendInputLayoutValid={valid} PointerSize={IntPtr.Size}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--context-self-test", StringComparer.OrdinalIgnoreCase))
{
    var messages = new List<JsonObject>
    {
        new() { ["role"] = "system", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "system" } } }
    };
    for (var turn = 1; turn <= 4; turn++)
    {
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = $"turn {turn}" },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:image/png;base64,{turn}" } }
            }
        });
        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = $"action {turn}" } }
        });
    }

    // Inject entities and directives into messages to test practically-lossless extraction
    messages[1]["content"]!.AsArray()[0]!["text"] = "Open C:\\Users\\Akshith\\Documents\\report.xlsx and navigate to https://github.com/vantage. Ensure PID: 4920 is verified and error 0x80070005 avoided.";

    var compacted = ContextWindowCompactor.Compact(messages, keepRecentNonSystemMessages: 6, keepRecentImages: 2);
    var retainedImages = messages
        .SelectMany(message => (message["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        .Count(block => block["type"]?.GetValue<string>() is "image" or "image_url");

    var limitsValid = ContextWindowLimits.GetModelContextWindow("claude-3-5-sonnet") == 200_000
        && ContextWindowLimits.GetModelContextWindow("gpt-4o") == 128_000
        && ContextWindowLimits.GetModelContextWindow("llama-3.3-70b", "https://api.groq.com/openai/v1") == 24_000
        && ContextWindowLimits.IsContextLimitExceeded(413, null, null)
        && ContextWindowLimits.IsContextLimitExceeded(400, "{\"code\":\"context_length_exceeded\"}", null)
        && ContextWindowLimits.IsContextLimitExceeded(400, "prompt is too long: 205000 tokens", null);

    var windowValid = limitsValid
        && compacted.RemovedMessages == 2
        && compacted.RemovedImages == 2
        && messages.Count == 7
        && retainedImages == 2
        && compacted.Summary.Contains("action 1", StringComparison.OrdinalIgnoreCase)
        && compacted.Summary.Contains("C:\\Users\\Akshith\\Documents\\report.xlsx", StringComparison.OrdinalIgnoreCase)
        && compacted.Summary.Contains("https://github.com/vantage", StringComparison.OrdinalIgnoreCase)
        && compacted.Summary.Contains("0x80070005", StringComparison.OrdinalIgnoreCase);

    var directory = Path.Combine(Path.GetTempPath(), "vantage-context-test-" + Guid.NewGuid().ToString("N"));
    var persistentValid = false;
    try
    {
        var tracker = new PersistentTaskContext("conversation", directory);
        tracker.BeginTask("Prepare a release");
        var initialBlock = tracker.BuildPromptBlock();
        persistentValid = initialBlock.Contains("you may include task_update", StringComparison.Ordinal);
        using var action = JsonDocument.Parse("""
            { "description": "Open the release editor", "task_update": { "add": ["Draft release notes"], "complete": ["goal"] } }
            """);
        tracker.RecordAction("click", action.RootElement, new ActionResult(ActionOutcome.Success, "editor opened"));
        using var failedAction = JsonDocument.Parse("""
            { "description": "Publish the release", "task_update": { "complete": ["todo-1"] } }
            """);
        tracker.RecordAction("click", failedAction.RootElement, new ActionResult(ActionOutcome.Failed, "publish button unavailable"));

        var reloaded = new PersistentTaskContext("conversation", directory);
        var promptBlock = reloaded.BuildPromptBlock();
        persistentValid = persistentValid
            && promptBlock.Contains("Goal: Prepare a release", StringComparison.Ordinal)
            && promptBlock.Contains("[ ] (goal)", StringComparison.Ordinal)
            && promptBlock.Contains("[ ] (todo-1) Draft release notes", StringComparison.Ordinal)
            && promptBlock.Contains("Last action: Publish the release failed: publish button unavailable.", StringComparison.Ordinal);

        using var doneAction = JsonDocument.Parse("""{ "action": "done" }""");
        reloaded.RecordAction("done", doneAction.RootElement, new ActionResult(ActionOutcome.Done, "release prepared"));
        var completedBlock = new PersistentTaskContext("conversation", directory).BuildPromptBlock();
        persistentValid = persistentValid
            && completedBlock.Contains("[x] (goal)", StringComparison.Ordinal)
            && completedBlock.Contains("[x] (todo-1)", StringComparison.Ordinal);

        PersistentTaskContext.Delete("conversation", directory);
        persistentValid = persistentValid
            && !File.Exists(Path.Combine(directory, "conversation.json"));

        var multiStep = new PersistentTaskContext("multi-step", directory);
        multiStep.BeginTask("Open Edge, then navigate to the repository, then send the result");
        var multiStepBlock = multiStep.BuildPromptBlock();
        persistentValid = persistentValid
            && multiStepBlock.Contains("[ ] (todo-1) Open Edge", StringComparison.Ordinal)
            && multiStepBlock.Contains("[ ] (todo-2) navigate to the repository", StringComparison.Ordinal)
            && multiStepBlock.Contains("[ ] (todo-3) send the result", StringComparison.Ordinal);
        PersistentTaskContext.Delete("multi-step", directory);
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    // Per-turn state must not leak into remembered facts; command output must.
    var noisy = new List<JsonObject>
    {
        new() { ["role"] = "system", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "system" } } },
        new() { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] =
            "WORLD_STATE = { running_apps=[charmap.exe], hwnd=0x4049C }\nFOREGROUND_CONTROLS (window):\n  [1] \"dfrgui.exe\" button @top-left\n# PERSISTENT TASK STATE\nDiscovered entities & facts:\n  * cleanmgr.exe\nFor a task with several steps you may include task_update.\nCurrent Text Buffer = [out: C:\\logs\\result.txt]" } } },
        new() { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "plan" } } },
        new() { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "next" } } },
        new() { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "plan" } } },
        new() { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "latest" } } },
    };
    var noisySummary = ContextWindowCompactor.Compact(noisy, keepRecentNonSystemMessages: 1, keepRecentImages: 1).Summary;
    var factsClean = noisySummary.Contains("C:\\logs\\result.txt", StringComparison.OrdinalIgnoreCase)
        && !noisySummary.Contains("charmap.exe", StringComparison.OrdinalIgnoreCase)
        && !noisySummary.Contains("dfrgui.exe", StringComparison.OrdinalIgnoreCase)
        && !noisySummary.Contains("0x4049C", StringComparison.OrdinalIgnoreCase);

    // Compaction must not turn plans into history: the reply that never ran
    // is no milestone, and "failed" in the next message is no recorded failure.
    var honestDir = Path.Combine(Path.GetTempPath(), "vantage-compaction-" + Guid.NewGuid().ToString("N"));
    bool historyHonest;
    try
    {
        var honestContext = new PersistentTaskContext("honest", honestDir);
        honestContext.BeginTask("Create a folder");
        honestContext.RecordMilestone("Opened the real thing");
        JsonObject Say(string role, string text) => new() { ["role"] = role, ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } } };
        var neverRan = new List<JsonObject>
        {
            Say("system", "system"),
            Say("user", "first"),
            Say("assistant", "<answer>```json\n{\"action\":\"run_powershell\",\"command\":\"x\"}\n</answer>"),
            Say("user", "Pitfalls to avoid (previous failed actions):"),
            Say("assistant", "<answer>```json\n{\"action\":\"wait\",\"seconds\":1}\n```</answer>"),
            Say("user", "latest"),
        };
        var honestSummary = ContextWindowCompactor.Compact(neverRan, keepRecentNonSystemMessages: 1, keepRecentImages: 1, taskContext: honestContext).Summary;
        var milestoneSection = honestSummary.Split("## EARLIER PLANS")[0];
        historyHonest = honestContext.Milestones.All(m => !m.Contains("run_powershell", StringComparison.OrdinalIgnoreCase) && !m.Contains("Executed", StringComparison.OrdinalIgnoreCase))
            && !milestoneSection.Contains("Executed", StringComparison.OrdinalIgnoreCase)
            && milestoneSection.Contains("Opened the real thing", StringComparison.Ordinal)
            && honestSummary.Contains("EARLIER PLANS", StringComparison.Ordinal)
            && honestContext.FailedAttempts.Count == 0;
    }
    finally
    {
        try { Directory.Delete(honestDir, recursive: true); } catch { }
    }

    // Earlier observations shrink to their window and outcome; the newest stays whole.
    JsonObject Observation(string window, string lastAction) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] =
            "\nWORLD_STATE = { os=\"Windows 11\", fg=\"Notepad/pid=8320/hwnd=0x1502\" }\n" +
            $"FOREGROUND_CONTROLS (window \"{window}\"; click one exactly with {{\"action\":\"click\",\"control\":N}}, valid this turn only):\n" +
            "  [0] \"OK\" button @middle-center\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"  [{i}] \"Item {i}\" list-item @top-left\n")) +
            "# PERSISTENT TASK STATE\nGoal: Save the files\n" + $"Last action: {lastAction}\n" +
            "To-do list:\n- [ ] (goal) Complete the requested goal.\nFor a task with several steps you may include task_update.\n" +
            "TEXT_READ (exact text from read_text, by page):\n  From \"Canada - Wikipedia\":\n    - " + new string('x', 6000) + "\n" +
            "VERIFICATION_FEEDBACK = [the click changed nothing]" } },
    };
    JsonObject Reply(string text) => new() { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } } };
    var turns = new List<JsonObject>
    {
        new() { ["role"] = "system", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "system" } } },
        Observation("Save as", "Save button in the Save As dialog succeeded"),
        Reply("{\"action\":\"click\",\"control\":0}"),
        Observation("Save as", "OK button in the path error dialog succeeded"),
    };
    var wholeTokens = ContextWindowLimits.EstimateTokens(new[] { turns[1] }, 0).TotalTokens;
    var foldedCount = ContextWindowCompactor.FoldEarlierObservations(turns);
    var earlier = turns[1]["content"]![0]!["text"]!.GetValue<string>();
    var newest = turns[3]["content"]![0]!["text"]!.GetValue<string>();
    var foldedTokens = ContextWindowLimits.EstimateTokens(new[] { turns[1] }, 0).TotalTokens;
    var foldOk = foldedCount == 1
        && ContextWindowCompactor.FoldEarlierObservations(turns) == 0
        && earlier.StartsWith(ContextWindowCompactor.FoldedObservationMarker, StringComparison.Ordinal)
        && earlier.Contains("Window: Save as", StringComparison.Ordinal)
        && earlier.Contains("Last action: Save button in the Save As dialog succeeded", StringComparison.Ordinal)
        && earlier.Contains("VERIFICATION_FEEDBACK = [the click changed nothing]", StringComparison.Ordinal)
        && !earlier.Contains("WORLD_STATE", StringComparison.Ordinal)
        && !earlier.Contains("Item 7", StringComparison.Ordinal)
        && !earlier.Contains("TEXT_READ", StringComparison.Ordinal)
        && newest.Contains("WORLD_STATE", StringComparison.Ordinal) && newest.Contains("TEXT_READ", StringComparison.Ordinal)
        && foldedTokens < 100 && wholeTokens > 1500;

    // An agent run compacts at its own budget even when the model's window is far larger.
    var budgetOk = ContextWindowLimits.EstimateTokens(turns, 2048, "claude-sonnet-5-5", null, 40_000).CompactionThreshold == 40_000
        && ContextWindowLimits.EstimateTokens(turns, 2048, "claude-sonnet-5-5").CompactionThreshold == 96_000
        && ContextWindowLimits.EstimateTokens(turns, 2048, "nvidia/nemotron-3.5-super-vl-120b-a12b", null, 40_000).CompactionThreshold == 22_528;

    var valid = windowValid && persistentValid && factsClean && historyHonest && foldOk && budgetOk;
    Console.WriteLine($"ContextWindowValid={windowValid} PersistentTaskContextValid={persistentValid} FactsWithoutNoise={factsClean} HistoryHonest={historyHonest} EarlierTurnsFolded={foldOk} RunBudget={budgetOk} RetainedMessages={messages.Count} RetainedImages={retainedImages}");
    Console.WriteLine($"Measured: one observation {wholeTokens} tokens whole, {foldedTokens} folded");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--launch-match-self-test", StringComparer.OrdinalIgnoreCase))
{
    var cases = new[]
    {
        WindowsAppManager.WindowMatchesLaunchTarget(
            new WindowsAppManager.WindowInfo(IntPtr.Zero, 0, "Untitled - Notepad", "Notepad"),
            "notepad"),
        WindowsAppManager.WindowMatchesLaunchTarget(
            new WindowsAppManager.WindowInfo(IntPtr.Zero, 0, "Calculator", "ApplicationFrameWindow"),
            "calc"),
        WindowsAppManager.WindowMatchesLaunchTarget(
            new WindowsAppManager.WindowInfo(IntPtr.Zero, 0, "Settings", "ApplicationFrameWindow"),
            "ms-settings:"),
        ComputerUseSession.IsScopedAction(" CLICK_WINDOW_XY "),
        !WindowsAppManager.WindowMatchesLaunchTarget(
            new WindowsAppManager.WindowInfo(IntPtr.Zero, 0, "Downloads - File Explorer", "CabinetWClass"),
            "notepad"),
    };
    var valid = cases.All(value => value);
    Console.WriteLine($"LaunchTargetMatchingValid={valid} Cases={cases.Length}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--model-name-self-test", StringComparer.OrdinalIgnoreCase))
{
    var cases = new Dictionary<string, string>
    {
        ["meta/muse-glimmer-30b"] = "Muse Glimmer",
        ["anthropic/claude-sonnet-4-5"] = "Claude Sonnet",
        ["qwen/qwen3-32b-instruct"] = "Qwen",
        ["openai/gpt-4.1-mini"] = "GPT Mini",
    };
    var valid = cases.All(pair => ModelDisplayNameService.GetFriendlyName(pair.Key) == pair.Value);
    Console.WriteLine($"ModelDisplayNamesValid={valid} Cases={cases.Count}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--provider-models-self-test", StringComparer.OrdinalIgnoreCase))
{
    // A providers.json written before multi-model support: one DefaultModel
    // and a provider-wide ForceYes. It must load as a one-model provider
    // that behaves identically.
    const string legacy = """
        [{ "Id": "abc", "Kind": 1, "Name": "NVIDIA", "BaseUrl": "https://integrate.api.nvidia.com/v1",
           "ApiKey": "dpapi:v1:xxxx", "DefaultModel": "nvidia/nemotron-3.5-super-vl-120b-a12b",
           "IsEnabled": true, "Status": 1, "VisionOverride": 1 }]
        """;
    var loaded = System.Text.Json.JsonSerializer.Deserialize<List<Vantage.Models.Provider>>(legacy)!;
    var p = loaded[0];
    var upgraded = p.NormalizeModels(ModelDisplayNameService.GetFriendlyName);
    var oneModel = p.Models.Count == 1
        && p.Models[0].Id == "nvidia/nemotron-3.5-super-vl-120b-a12b"
        && p.Models[0].DisplayName == "Nemotron Super VL"
        && p.Models[0].VisionOverride == Vantage.Models.VisionOverride.ForceYes
        && p.VisionOverride == Vantage.Models.VisionOverride.Auto
        && p.DefaultModel == p.Models[0].Id
        && p.ApiKey == "dpapi:v1:xxxx";
    var idempotent = !p.NormalizeModels(ModelDisplayNameService.GetFriendlyName);

    // Round trip with several models, a blank row and a duplicate.
    p.Models.Add(new Vantage.Models.ProviderModel { Id = "meta/llama-3.3-70b", DisplayName = "Llama (fast)" });
    p.Models.Add(new Vantage.Models.ProviderModel { Id = "  " });
    p.Models.Add(new Vantage.Models.ProviderModel { Id = "meta/llama-3.3-70b" });
    var cleaned = p.NormalizeModels();
    var json = System.Text.Json.JsonSerializer.Serialize(loaded, Vantage.Common.JsonDefaults.Persist);
    var back = System.Text.Json.JsonSerializer.Deserialize<List<Vantage.Models.Provider>>(json, Vantage.Common.JsonDefaults.Persist)![0];
    var roundTrip = cleaned && back.Models.Count == 2
        && back.Models[1].Label == "Llama (fast)"
        && back.Models[0].Label == "Nemotron Super VL";

    // A pinned copy drives the engine/vision cache: it must carry the model
    // and its own override, and a blank label must fall back to the id.
    var pinned = back.ForModel(back.Models[1]);
    var pinnedOk = pinned.DefaultModel == "meta/llama-3.3-70b"
        && pinned.Id == back.Id
        && pinned.VisionOverride == Vantage.Models.VisionOverride.Auto
        && back.ForModel(back.Models[0]).VisionOverride == Vantage.Models.VisionOverride.ForceYes
        && new Vantage.Models.ProviderModel { Id = "gpt-4o" }.Label == "gpt-4o";

    // Anthropic accepts the address with or without /v1, never sends an empty
    // text block, and counts the cached part of the prompt as input.
    var emptyReply = new JsonArray
    {
        new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "" } } },
        new JsonObject { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "next" } } },
    };
    AnthropicEngine.FillEmptyText(emptyReply);
    using var usage = JsonDocument.Parse("""{"input_tokens":120,"cache_creation_input_tokens":0,"cache_read_input_tokens":9000,"output_tokens":80}""");
    var anthropicOk = AnthropicEngine.ApiRoot("https://api.anthropic.com/v1/") == "https://api.anthropic.com"
        && AnthropicEngine.ApiRoot("https://api.anthropic.com") == "https://api.anthropic.com"
        && emptyReply[0]!["content"]![0]!["text"]!.GetValue<string>() == "(empty)"
        && emptyReply[1]!["content"]![0]!["text"]!.GetValue<string>() == "next"
        && AnthropicEngine.InputTokensUsed(usage.RootElement) == 9120
        && AnthropicEngine.InputTokensUsed(default) == 0;

    // Inception's reply to a screenshot, word for word: its message is a list,
    // which once reached the user as a bare "HTTP 400".
    const string noImages = """{"error":{"message":[{"type":"value_error","loc":["body"],"msg":"Value error, Image inputs are not supported for this model","ctx":{"error":"Image inputs are not supported for this model"}}],"type":"invalid_request_error","param":null,"code":"invalid_request_error"}}""";
    var described = LmmErrorClassifier.Describe(noImages, "mercury-3") ?? "";
    var errorsOk = LmmErrorClassifier.ExtractMessage(noImages) == "Image inputs are not supported for this model"
        && LmmErrorClassifier.RejectsImages(noImages)
        && described.StartsWith("mercury-3 does not accept images.", StringComparison.Ordinal)
        && described.Contains("turn Act off", StringComparison.Ordinal)
        && LmmErrorClassifier.ExtractMessage("""{"detail":[{"msg":"field required"},{"msg":"bad model"}]}""") == "field required; bad model"
        && LmmErrorClassifier.ExtractMessage("""{"error":{"message":"Invalid API key"}}""") == "Invalid API key"
        && LmmErrorClassifier.Describe("""{"error":{"message":"Invalid API key"}}""", "m") == "Invalid API key"
        && !LmmErrorClassifier.RejectsImages("""{"error":{"message":"Image too large: size exceeds 5 MB"}}""")
        && !LmmErrorClassifier.RejectsImages("""{"error":{"message":"Unsupported parameter: temperature"}}""")
        // Gemini's OpenAI-compatible endpoint sends its error inside a list.
        && LmmErrorClassifier.ExtractMessage("""[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]""") == "API key not valid. Please pass a valid API key."
        && LmmErrorClassifier.ExtractMessage("[]") is null
        && LmmErrorClassifier.ExtractMessage("not json") is null;

    var valid = oneModel && upgraded && idempotent && roundTrip && pinnedOk && anthropicOk && errorsOk;
    Console.WriteLine($"ProviderModelsValid={valid} OneModel={oneModel} Idempotent={idempotent} RoundTrip={roundTrip} Pinned={pinnedOk} Anthropic={anthropicOk} Errors={errorsOk}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--powershell-self-test", StringComparer.OrdinalIgnoreCase))
{
    var quick = await WindowsAppManager.RunPowerShellAsync(
        "Write-Output vantage-self-test",
        timeoutMs: 5_000);
    var internalTimeout = await WindowsAppManager.RunPowerShellAsync(
        "Start-Sleep -Seconds 10",
        timeoutMs: 1_000);

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    var sw = Stopwatch.StartNew();
    var canceled = false;
    try
    {
        await WindowsAppManager.RunPowerShellAsync(
            "Start-Sleep -Seconds 10",
            timeoutMs: 30_000,
            cts.Token);
    }
    catch (OperationCanceledException)
    {
        canceled = true;
    }
    sw.Stop();

    var valid = quick.ExitCode == 0
        && quick.StdOut.Contains("vantage-self-test", StringComparison.Ordinal)
        && internalTimeout.ExitCode == -1
        && internalTimeout.StdErr.Contains("[timeout after 1000ms]", StringComparison.Ordinal)
        && canceled
        && sw.Elapsed < TimeSpan.FromSeconds(5);
    Console.WriteLine($"PowerShellExecutionValid={valid} CancellationObserved={canceled} CancelElapsedMs={sw.ElapsedMilliseconds}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--computer-use-self-test", StringComparer.OrdinalIgnoreCase))
{
    var session = new ComputerUseSession();
    var listing = session.ListWindows();
    var candidate = WindowsAppManager.ListVisibleWindows().FirstOrDefault(window =>
        listing.Contains(ComputerUseSession.FormatWindowId(window), StringComparison.Ordinal));
    var tokenValid = candidate is not null
        && ComputerUseSession.TryParseWindowId(
            ComputerUseSession.FormatWindowId(candidate),
            out var parsedHandle,
            out var parsedPid)
        && parsedHandle == candidate.Handle
        && parsedPid == candidate.Pid;
    var observation = candidate is null
        ? new ActionResult(ActionOutcome.Failed, "no safe visible window")
        : session.ObserveWindow(ComputerUseSession.FormatWindowId(candidate));
    var observationValid = observation.Outcome == ActionOutcome.Success
        && observation.Description.Contains("observation_id=obs-", StringComparison.Ordinal)
        && !observation.Description.Contains("Accessibility unavailable:", StringComparison.OrdinalIgnoreCase);
    var observationId = observationValid
        ? observation.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .First(part => part.StartsWith("observation_id=", StringComparison.Ordinal))["observation_id=".Length..]
        : string.Empty;
    var firstUse = candidate is null
        ? new ActionResult(ActionOutcome.Failed, "no candidate")
        : await session.ClickElementAsync(
            ComputerUseSession.FormatWindowId(candidate), observationId, -1, "left", 1, CancellationToken.None);
    var reused = candidate is null
        ? new ActionResult(ActionOutcome.Failed, "no candidate")
        : await session.ClickElementAsync(
            ComputerUseSession.FormatWindowId(candidate), observationId, -1, "left", 1, CancellationToken.None);
    var singleUseValid = firstUse.Outcome == ActionOutcome.Failed
        && reused.Outcome == ActionOutcome.Failed
        && reused.Description.Contains("no live observation", StringComparison.OrdinalIgnoreCase);
    var valid = tokenValid && observationValid && singleUseValid;
    Console.WriteLine($"ComputerUseSessionValid={valid} TokenRoundTrip={tokenValid} AccessibilityObservation={observationValid} SingleUseObservation={singleUseValid}");
    if (!observationValid)
        Console.WriteLine(observation.Description);
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--flow-self-test", StringComparer.OrdinalIgnoreCase))
{
    // Window-title matching tolerates paraphrase but not unrelated windows.
    var matchCases = new (string Target, string Title, string Process, bool Expected)[]
    {
        ("Vantage - Vantage - Antigravity", "Antigravity — Vantage - Vantage - Antigravity", "Antigravity", true),
        ("Microsoft Edge", "New tab and 4 more pages - Personal - Microsoft​ Edge", "msedge", true),
        ("edge", "Anything", "msedge", true),
        ("Google Chrome", "GitHub - Microsoft Edge", "msedge", false),
        ("Notepad", "Untitled - Notepad", "notepad", true),
        ("Settings", "Microsoft Store", "ApplicationFrameHost", false),
    };
    var matchOk = matchCases.All(c => VantageACI.TargetMatches(c.Target, c.Title, c.Process) == c.Expected);

    AgentAction Step(string action) => CommonUtils.ParseAgentAction($"{{\"action\":\"{action}\"}}")!;
    var okBatch = VantageACI.ValidateObservationUse(new[] { Step("click_element"), Step("wait") }) is null
        && VantageACI.ValidateObservationUse(new[] { Step("click_xy"), Step("type_text"), Step("press_key") }) is null;
    var twoWindowSteps = VantageACI.ValidateObservationUse(new[] { Step("click_element"), Step("press_window_key") });
    var legacyBeforeWindow = VantageACI.ValidateObservationUse(new[] { Step("click_xy"), Step("type_window_text") });
    var batchOk = okBatch && twoWindowSteps is not null && legacyBeforeWindow is not null;

    var valid = matchOk && batchOk;
    Console.WriteLine($"FlowValid={valid} TitleMatching={matchOk} BatchObservationRules={batchOk}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--agent-accuracy-self-test", StringComparer.OrdinalIgnoreCase))
{
    // Fix 1: only deterministic actions may skip verification; guessed targets are always checked.
    AgentAction Parse(string json) => CommonUtils.ParseAgentAction(json)!;
    var verified = new[]
    {
        "{\"action\":\"click\",\"description\":\"the Windows Update option\"}",
        "{\"action\":\"click\",\"x\":80,\"y\":829}",
        "{\"action\":\"click_xy\",\"x\":80,\"y\":829}",
        "{\"action\":\"drag\",\"start_description\":\"a\",\"end_description\":\"b\"}",
        "{\"action\":\"scroll\",\"description\":\"the list\",\"clicks\":3}",
        "{\"action\":\"type\",\"description\":\"the search box\",\"text\":\"hi\"}",
        "{\"action\":\"click_window_xy\",\"x\":4,\"y\":5}",
        "{\"action\":\"batch\",\"steps\":[{\"action\":\"key\",\"keys\":[\"win\"]},{\"action\":\"click\",\"description\":\"x\"}]}",
    };
    var fast = new[]
    {
        "{\"action\":\"key\",\"keys\":[\"win\",\"i\"]}",
        "{\"action\":\"type\",\"text\":\"hello\"}",
        "{\"action\":\"launch_app\",\"name\":\"notepad\"}",
        "{\"action\":\"run_powershell\",\"script\":\"Get-Date\"}",
        "{\"action\":\"batch\",\"steps\":[{\"action\":\"key\",\"keys\":[\"win\",\"i\"]},{\"action\":\"wait\",\"seconds\":1}]}",
    };
    var verifyOk = verified.All(j => !FastPathPolicy.IsEligible(Parse(j)))
        && fast.All(j => FastPathPolicy.IsEligible(Parse(j)));

    // Fixes 2 and 3: accessibility-first grounding picks the named control and refuses to guess.
    UiControl Ctl(string name, string role, int x, int y, bool enabled = true) =>
        new(name, role, enabled, false, x, y, 160, 40);
    var settings = new List<UiControl>
    {
        Ctl("Home", "list-item", 40, 160),
        Ctl("Windows Update", "list-item", 40, 800),          // left sidebar
        Ctl("Windows Update", "button", 1400, 120),            // top-right card
        Ctl("Bluetooth & devices", "list-item", 40, 300),
        Ctl("Check for updates", "button", 700, 400),
        Ctl("Search", "edit", 300, 20),
        Ctl("Delete", "button", 900, 500, enabled: false),
    };
    var sidebar = UiControlGrounding.Match("the Windows Update option at the bottom of the left sidebar in Settings", settings);
    var card = UiControlGrounding.Match("the Windows Update card in the top right", settings);
    var ambiguous = UiControlGrounding.Match("Windows Update", settings);
    var longest = UiControlGrounding.Match("the Check for updates button", settings);
    var disabled = UiControlGrounding.Match("the Delete button", settings);
    var missing = UiControlGrounding.Match("the Network & internet option", settings);
    var searchBox = UiControlGrounding.Match("the Search box at the top", settings);
    var groundOk = sidebar is { Role: "list-item" } && sidebar.Left == 40 && sidebar.Top == 800
        && card is { Role: "button" } && card.Left == 1400
        && ambiguous is null
        && longest?.Name == "Check for updates"
        && disabled is null
        && missing is null
        && searchBox?.Role == "edit";
    var block = UiControlGrounding.BuildPromptBlock(settings, "Settings", 1536, 960, 1.25);
    var blockOk = block.Contains("\"Windows Update\" list-item @", StringComparison.Ordinal)
        && !block.Contains("Delete", StringComparison.Ordinal);

    // Fix 4: task_update accepts step wording as well as ids.
    var taskRoot = Path.Combine(Path.GetTempPath(), "vantage-accuracy-" + Guid.NewGuid().ToString("N"));
    bool todoOk;
    try
    {
        var ctx = new PersistentTaskContext("accuracy-test", taskRoot);
        ctx.BeginTask("Open settings and check for windows updates.");
        var add = CommonUtils.ParseAgentAction(
            "{\"action\":\"wait\",\"task_update\":{\"add\":[\"Open Windows Settings\",\"Navigate to Windows Update section\",\"Click Check for updates\"],\"complete\":[],\"reopen\":[]}}")!;
        ctx.RecordAction("wait", add.Raw, new ActionResult(ActionOutcome.Success, "ok"));
        var byText = CommonUtils.ParseAgentAction(
            "{\"action\":\"wait\",\"task_update\":{\"add\":[],\"complete\":[\"open windows settings\"],\"reopen\":[]}}")!;
        ctx.RecordAction("wait", byText.Raw, new ActionResult(ActionOutcome.Success, "ok"));
        var byId = ctx.Todos.First(t => t.Text.Equals("Click Check for updates", StringComparison.OrdinalIgnoreCase)).Id;
        var idUpdate = CommonUtils.ParseAgentAction(
            $"{{\"action\":\"wait\",\"task_update\":{{\"add\":[],\"complete\":[\"{byId}\"],\"reopen\":[]}}}}")!;
        ctx.RecordAction("wait", idUpdate.Raw, new ActionResult(ActionOutcome.Success, "ok"));
        var failedUpdate = CommonUtils.ParseAgentAction(
            "{\"action\":\"wait\",\"task_update\":{\"add\":[],\"complete\":[\"Open\"],\"reopen\":[]}}")!;
        ctx.RecordAction("wait", failedUpdate.Raw, new ActionResult(ActionOutcome.Success, "ok"));
        todoOk = ctx.Todos.Single(t => t.Text == "Open Windows Settings").IsCompleted
            && ctx.Todos.Single(t => t.Text == "Click Check for updates").IsCompleted
            && !ctx.Todos.Single(t => t.Text == "Navigate to Windows Update section").IsCompleted
            && ctx.ResolveTodo("Open") is null;
    }
    finally
    {
        try { Directory.Delete(taskRoot, recursive: true); } catch { }
    }

    // Regressions from a real run: a page name in the location clause is not the target.
    var realRun = new List<UiControl>
    {
        Ctl("Windows Update", "list-item", 40, 800),
        Ctl("Privacy & security", "list-item", 40, 700),
        Ctl("Search box, Find a setting", "edit", 300, 20),
    };
    var contextOnly = UiControlGrounding.Match("the Check for updates button at the top-right of the Windows Update page", realRun);
    var targetFirst = UiControlGrounding.Match("the Windows Update list-item in the left sidebar of Settings", realRun);
    var namePrefix = UiControlGrounding.Match("the Search box", realRun);
    var preposition = UiControlGrounding.Match("the Save in folder button", new List<UiControl> { Ctl("Save in folder", "button", 10, 10) });
    var contextOk = contextOnly is null && targetFirst?.Name == "Windows Update"
        && namePrefix?.Role == "edit" && preposition is not null;

    // Numbered list: chrome is hidden, numbers stay valid indexes into the snapshot.
    var withChrome = new List<UiControl> { Ctl("Minimise Settings", "button", 900, 0), Ctl("Close", "button", 950, 0), Ctl("Close tab", "button", 100, 0), Ctl("Home", "list-item", 40, 160) };
    var numbered = UiControlGrounding.BuildPromptBlock(withChrome, "Settings", 1536, 960, 1.25);
    var numberedOk = numbered.Contains("[3] \"Home\"", StringComparison.Ordinal)
        && numbered.Contains("[2] \"Close tab\"", StringComparison.Ordinal)
        && !numbered.Contains("Minimise", StringComparison.Ordinal)
        && !numbered.Contains("\"Close\"", StringComparison.Ordinal);
    var underPoint = UiControlGrounding.ControlAt(50, 170, settings);
    var snapOk = underPoint?.Name == "Home" && UiControlGrounding.ControlAt(5000, 5000, settings) is null;

    // Coordinate calibration, including the answer seen in the real run: a
    // control at image (116,451) answered as (60,622) is a 0-1000 grid.
    var calibrationOk =
        CoordinateCalibration.Choose(60, 622, 116, 451, 1152, 720) == CoordinateConvention.Permille
        && CoordinateCalibration.Choose(118, 455, 116, 451, 1152, 720) == CoordinateConvention.ImagePixels
        && CoordinateCalibration.Choose(900, 950, 1037, 684, 1152, 720) == CoordinateConvention.Permille
        && CoordinateCalibration.ToImage(CoordinateConvention.Permille, 500, 500, 1000, 600) == (500d, 300d);

    // A done after state-changing steps needs a look; after waits it does not.
    AgentAction Named(string name) => CommonUtils.ParseAgentAction($"{{\"action\":\"{name}\"}}")!;
    var doneOk = BatchPolicy.DoneNeedsAFreshLook(new[] { Named("key"), Named("wait") })
        && !BatchPolicy.DoneNeedsAFreshLook(new[] { Named("wait") })
        && !BatchPolicy.DoneNeedsAFreshLook(Array.Empty<AgentAction>());
    var controlNotSanitized = ActionSanitizer.Sanitize(Parse("{\"action\":\"click\",\"control\":4}")).Replacement is null
        && ActionSanitizer.Sanitize(Parse("{\"action\":\"click\"}")).Replacement is not null;

    // Fact ledger, replaying both failed runs. Misread and invented values are
    // refused, and asking again or writing a note does not get them through.
    var noSources = Array.Empty<string?>();
    var misread = Parse("{\"action\":\"type_text\",\"text\":\"India: 1,417,736,308\\nBrazil: 212,111,955\"}");
    var ledger = new FactLedger();
    var heldFirst = ledger.BlockedValues(misread, noSources);
    var heldAgain = ledger.BlockedValues(misread, noSources);
    ledger.RecordNotes(Parse("{\"action\":\"wait\",\"seconds\":1,\"note\":\"Brazil 212,111,955, India 1,417,736,308\"}"));
    var heldAfterNote = ledger.BlockedValues(misread, noSources);
    var refusedOk = heldFirst.Count == 2 && heldAgain.Count == 2 && heldAfterNote.Count == 2;

    // The flag is refused while the window has readable text.
    var flagged = Parse("{\"action\":\"type_text\",\"text\":\"Brazil: 212,111,955\",\"unconfirmed\":true}");
    var flagRefused = ledger.BlockedValues(flagged, noSources).Count == 1 && ledger.Unconfirmed().Count == 0;

    // Text actually read is a source, exactly: the true value passes, a near miss does not.
    var read = new FactLedger();
    read.RecordRead(new[] { "Population • 2026 estimate 214,211,951[5] (7th) • 2022 census 203,080,756[6]", "Population • 2026 estimate 1.48 billion" });
    var trueValues = read.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"India: 1.48 billion\\nBrazil: 214,211,951\"}"), noSources);
    var nearMiss = read.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Brazil: 214,211,953\"}"), noSources);
    var roundedValue = read.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Brazil: 214,000,000\"}"), noSources);
    var readOk = trueValues.Count == 0 && nearMiss.Count == 1 && roundedValue.Count == 1
        && read.BuildPromptBlock().Contains("TEXT_READ", StringComparison.Ordinal)
        && read.BuildPromptBlock().Contains("214,211,951", StringComparison.Ordinal);

    // A window with no text: only then can a value go through, and it is reported.
    var blind = new FactLedger();
    blind.RecordReadingUnavailable();
    var blindUnflagged = blind.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Score 48213\"}"), noSources);
    var blindFlagged = blind.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Score 48213\",\"unconfirmed\":true}"), noSources);
    var blindOk = blindUnflagged.Count == 1 && blindFlagged.Count == 0 && blind.Unconfirmed().SequenceEqual(new[] { "48213" });
    blind.RecordRead(new[] { "Score 48,213" });
    blindOk = blindOk && blind.Unconfirmed().Count == 0;

    // Other real sources count; ordinary typing, URLs and years are never refused.
    var sourced = new FactLedger();
    var fromGoal = sourced.BlockedValues(Parse("{\"action\":\"type\",\"text\":\"Order 48213\"}"), new string?[] { "Enter order 48213 in the form" });
    var fromOutput = sourced.BlockedValues(Parse("{\"action\":\"type\",\"text\":\"Size 90,210\"}"), new string?[] { "pwsh_exit=0 out: 90210" });
    var plain = sourced.BlockedValues(Parse("{\"action\":\"type\",\"text\":\"toxic rock gaming\"}"), noSources);
    var address = sourced.BlockedValues(Parse("{\"action\":\"type\",\"text\":\"https://example.com/item/123456\",\"enter\":true}"), noSources);
    var shortNumber = sourced.BlockedValues(Parse("{\"action\":\"type\",\"text\":\"page 12\"}"), noSources);
    var yearOnly = sourced.BlockedValues(Parse("{\"action\":\"type\",\"text\":\"report for 2026\"}"), noSources);
    var inBatch = sourced.BlockedValues(Parse("{\"action\":\"batch\",\"steps\":[{\"action\":\"key\",\"keys\":[\"ctrl\",\"t\"]},{\"action\":\"type_text\",\"text\":\"555123\"}]}"), noSources);
    var sourceOk = fromGoal.Count == 0 && fromOutput.Count == 0 && plain.Count == 0 && address.Count == 0 && shortNumber.Count == 0 && yearOnly.Count == 0 && inBatch.Count == 1;

    // Finding text: every match with context, whitespace collapsed, no match is empty.
    var page = "Area\n  Total 8,510,346 km2\nPopulation \uFFFC\n  2026 estimate 214,211,951[5]\nPopulation density 23.8";
    var foundText = PageTextReader.ExtractExcerpts(page, new[] { "2026 estimate" });
    var noMatch = PageTextReader.ExtractExcerpts(page, new[] { "Elevation" });
    var noFilter = PageTextReader.ExtractExcerpts(page, Array.Empty<string>());
    var manyHits = PageTextReader.ExtractExcerpts(string.Join(' ', Enumerable.Repeat("Population " + new string('x', 400), 9)), new[] { "population" });
    var excerptOk = foundText.Count == 1 && foundText[0].Contains("214,211,951", StringComparison.Ordinal) && !foundText[0].Contains('\n')
        && noMatch.Count == 0 && noFilter.Count == 1 && manyHits.Count == 4
        && PageTextReader.ExtractExcerpts("   ", new[] { "a" }).Count == 0;
    var readAction = SettlePolicy.NoChangeBudgetMs(Parse("{\"action\":\"read_text\",\"find\":\"x\"}")) == 0;

    var ledgerOk = refusedOk && flagRefused && readOk && blindOk && sourceOk && excerptOk && readAction;
    // Replies, starting with the exact one that stopped a real run three times.
    AgentAction? FromReply(string reply) => CommonUtils.ParseAgentAction(CommonUtils.ExtractLastJsonBlock(reply));
    var strayTags = "<thoughts>\nCreate the folder.\n</thoughts>\n<answer>\n```json\n{\n  \"action\": \"run_powershell\",\n  \"command\": \"New-Item -ItemType Directory -Path \\\"$env:USERPROFILE\\\\Desktop\\\\Vantage Hard Test\\\" -Force\"\n}\n</answer>\n</json>\n```";
    var strayAction = FromReply(strayTags);
    var noFenceClose = FromReply("<answer>\n```json\n{\"action\":\"wait\",\"seconds\":1}\n</answer>");
    var noFence = FromReply("<thoughts>go</thoughts><answer>{\"action\":\"done\"}</answer>");
    var quotedExample = FromReply("<thoughts>Earlier I sent {\"action\":\"screenshot\"} and the format is <answer>```json{...}```</answer>.</thoughts><answer>\n```json\n{\"action\":\"click\",\"control\":4}\n```\n</answer>");
    var nestedBatch = FromReply("```json\n{\"action\":\"batch\",\"steps\":[{\"action\":\"key\",\"keys\":[\"ctrl\",\"t\"]},{\"action\":\"wait\",\"seconds\":1}]}\n</answer>\n```");
    var braceInText = FromReply("<answer>\n```json\n{\"action\":\"type_text\",\"text\":\"a } \\\" { b\"}\nstray\n```");
    var trailingComma = FromReply("```json\n{\"action\":\"done\",}\n```");
    var noAction = FromReply("I would click the button.");
    var notAnAction = FromReply("```json\n{\"thing\":1}\n```");
    var replyOk = strayAction?.Action == "run_powershell"
        && strayAction.GetString("command")!.Contains("Vantage Hard Test", StringComparison.Ordinal)
        && noFenceClose?.Action == "wait" && noFence?.Action == "done"
        && quotedExample?.Action == "click" && quotedExample.GetInt("control") == 4
        && nestedBatch?.Action == "batch" && nestedBatch.GetActionList("steps").Count == 2
        && braceInText?.GetString("text") == "a } \" { b"
        && trailingComma?.Action == "done" && noAction is null && notAnAction is null;
    // A Cohere reply was a long plan with a PowerShell example and no action,
    // and it was reported as invalid JSON because of the '$' in the example.
    const string planWithScript = "We'll do:\n\n```\n$uri = \"https://en.wikipedia.org/wiki/Canada\"\nif ($text -match 'Capital') {\n    $capital = $matches[1]\n}\n```\n\nBut the HTML structure may be different.";
    var problemOk = CommonUtils.DescribeActionProblem("I would click it.").Contains("no JSON", StringComparison.Ordinal)
        && CommonUtils.DescribeActionProblem("```json\n{\"thing\":1}\n```").Contains("no `action`", StringComparison.Ordinal)
        && CommonUtils.DescribeActionProblem("```json\n{\"action\": \n```").Contains("invalid", StringComparison.Ordinal)
        && CommonUtils.DescribeActionProblem(planWithScript) == "it has no `action` field"
        && CommonUtils.DescribeActionProblem("```powershell\nif ($a) { $b }\n```\n```json\n{\"action\": \n```").Contains("invalid", StringComparison.Ordinal)
        && FromReply(planWithScript) is null;
    // Floating-point noise from a real run counts as the short number; real differences do not.
    var sums = new FactLedger();
    var noisyOutput = new string?[] { "pwsh_exit=0 out: 237.860000000001" };
    var noiseOk = sums.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Difference: 237.86 m\"}"), noisyOutput).Count == 0
        && sums.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Difference: 237.87 m\"}"), noisyOutput).Count == 1
        && sums.BlockedValues(Parse("{\"action\":\"type_text\",\"text\":\"Difference: 238 m\"}"), noisyOutput).Count == 1
        && sums.BlockedValues(Parse("{\"action\":\"vantage_set_clipboard\",\"text\":\"Difference: 237.860 m\"}"), noisyOutput).Count == 0
        && FactLedger.WithoutFloatingPointNoise("237.859999999999") == "237.86"
        && FactLedger.WithoutFloatingPointNoise("500.000000000001") == "500"
        && FactLedger.WithoutFloatingPointNoise("3.14159265358") is null
        && FactLedger.WithoutFloatingPointNoise("237.86") is null;

    // Clipboard: the sequence from the real run is refused, the run's own content is not.
    var paste = Parse("{\"action\":\"press_key\",\"combo\":\"ctrl+v\"}");
    var clipOk = ClipboardPolicy.Evaluate(paste, false).PastesForeignContent
        && !ClipboardPolicy.Evaluate(paste, true).PastesForeignContent
        && ClipboardPolicy.Evaluate(Parse("{\"action\":\"key\",\"keys\":[\"ctrl\",\"v\"]}"), false).PastesForeignContent
        && ClipboardPolicy.Evaluate(Parse("{\"action\":\"vantage_set_clipboard\",\"text\":\"a\"}"), false).OursAfter
        && ClipboardPolicy.Evaluate(Parse("{\"action\":\"key\",\"keys\":[\"ctrl\",\"c\"]}"), false).OursAfter
        && ClipboardPolicy.Evaluate(Parse("{\"action\":\"press_key\",\"combo\":\"Control + X\"}"), false).OursAfter
        && ClipboardPolicy.Evaluate(Parse("{\"action\":\"run_powershell\",\"command\":\"Set-Clipboard x\"}"), false).OursAfter
        && !ClipboardPolicy.Evaluate(Parse("{\"action\":\"type_text\",\"text\":\"ctrl+c\"}"), false).OursAfter
        && !ClipboardPolicy.Evaluate(Parse("{\"action\":\"batch\",\"steps\":[{\"action\":\"vantage_set_clipboard\",\"text\":\"a\"},{\"action\":\"key\",\"keys\":[\"ctrl\",\"v\"]}]}"), false).PastesForeignContent
        && ClipboardPolicy.Evaluate(Parse("{\"action\":\"batch\",\"steps\":[{\"action\":\"key\",\"keys\":[\"ctrl\",\"v\"]},{\"action\":\"vantage_set_clipboard\",\"text\":\"a\"}]}"), false).PastesForeignContent
        && ClipboardPolicy.GoalAllowsUserClipboard("Paste my clipboard into Notepad") && !ClipboardPolicy.GoalAllowsUserClipboard("Write the numbers in Notepad");

    // Calculator's expression line was a real change the old threshold missed.
    var thresholdOk = new FrameDelta(0.00022, 0).IsChange && !new FrameDelta(0.00003, 0.002).IsChange
        && ScreenSampler.ToScreenshotDiff(new FrameDelta(0.00022, 0)).IsSignificant(0.006);

    // Tabs, replaying the run where a link replaced a tab and a user tab was overwritten.
    var tabControls = new List<UiControl>
    {
        Ctl("Mount Everest", "hyperlink", 500, 400),
        Ctl("Mount Everest - Wikipedia - Memory usage - 200 MB", "tab-item", 300, 10),
        Ctl("K2 - Wikipedia - Memory usage - 182 MB", "tab-item", 500, 10),
        Ctl("Save", "hyperlink", 100, 300),
    };
    var linkOnly = tabControls.Where(c => c.Role == "hyperlink").ToList();
    var tabMatchOk = UiControlGrounding.Match("the Mount Everest - Wikipedia tab at the top of the browser", tabControls)?.Role == "tab-item"
        && UiControlGrounding.Match("the K2 - Wikipedia tab at the top of the browser", tabControls)?.Left == 500
        && UiControlGrounding.Match("the Mount Everest - Wikipedia tab at the top of the browser", linkOnly) is null
        && UiControlGrounding.Match("the Mount Everest link", tabControls)?.Role == "hyperlink"
        && UiControlGrounding.Match("the Save button", tabControls)?.Role == "hyperlink";
    var manyControls = Enumerable.Range(0, 80).Select(i => Ctl($"Link {i}", "hyperlink", 10, i)).Append(Ctl("K2 - Wikipedia", "tab-item", 500, 10)).ToList();
    var listed = UiControlGrounding.BuildPromptBlock(manyControls, "Edge", 1536, 960, 1.25);
    var tabsListedOk = listed.Contains("[80] \"K2 - Wikipedia\" tab-item", StringComparison.Ordinal)
        && listed.IndexOf("K2 - Wikipedia", StringComparison.Ordinal) < listed.IndexOf("Link 0", StringComparison.Ordinal);

    // Every address bar use gets a new tab, including on a tab reached by Ctrl+Tab.
    var tracker = new BrowserTabTracker();
    var edgeBacklogs = new ForegroundWindowInfo("Backlogs - Claude and 2 more pages - Microsoft Edge", "msedge");
    var everyUse = tracker.ShouldOpenNewTab(edgeBacklogs, "Look up K2") && tracker.ShouldOpenNewTab(edgeBacklogs, "Look up K2");
    tracker.OnNewTabOpened();
    var noDouble = !tracker.ShouldOpenNewTab(edgeBacklogs, "Look up K2");
    tracker.OnAddressUsed();
    var againAfter = tracker.ShouldOpenNewTab(edgeBacklogs, "Look up K2");
    var trackerOk = everyUse && noDouble && againAfter
        && !tracker.ShouldOpenNewTab(new ForegroundWindowInfo("New tab - Microsoft Edge", "msedge"), "x")
        && !tracker.ShouldOpenNewTab(edgeBacklogs, "Reload this tab")
        && !tracker.ShouldOpenNewTab(new ForegroundWindowInfo("Report - Notepad", "notepad"), "x");

    // Writing a file through the shell is checked; doing a sum is not.
    var shell = new FactLedger();
    var shellWrite = Parse("{\"action\":\"run_powershell\",\"command\":\"Set-Content -Path \\\"C:\\\\Users\\\\Me\\\\mountains.txt\\\" -Value @(\\u0027Difference: 237.86 m\\u0027)\"}");
    var shellOk = shell.BlockedValues(shellWrite, noSources).SequenceEqual(new[] { "237.86" })
        && shell.BlockedValues(shellWrite, new string?[] { "Display is 237.86" }).Count == 0
        && shell.BlockedValues(Parse("{\"action\":\"run_powershell\",\"command\":\"8848.86 - 8611\"}"), noSources).Count == 0
        && shell.BlockedValues(Parse("{\"action\":\"run_powershell\",\"command\":\"Get-ChildItem 2>$null | Select -First 500\"}"), noSources).Count == 0
        && shell.BlockedValues(Parse("{\"action\":\"run_powershell\",\"command\":\"echo 555123 > out.txt\"}"), noSources).Count == 1
        && shell.BlockedValues(Parse("{\"action\":\"run_powershell\",\"command\":\"New-Item -ItemType Directory -Path \\\"C:\\\\Test 500\\\" -Force\"}"), noSources).Count == 0;

    ledgerOk = ledgerOk && replyOk && problemOk && noiseOk && clipOk && thresholdOk && tabMatchOk && tabsListedOk && trackerOk && shellOk;

    var ruleOk = PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory("windows", 1536, 960, Array.Empty<string>()).Contains("clicks\\\": -5", StringComparison.Ordinal)
        || PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory("windows", 1536, 960, Array.Empty<string>()).Contains("\"clicks\": -5", StringComparison.Ordinal);

    var valid = verifyOk && groundOk && blockOk && todoOk && contextOk && numberedOk && snapOk && calibrationOk && doneOk && controlNotSanitized && ledgerOk && ruleOk;
    Console.WriteLine($"AgentAccuracyValid={valid} VerifiedGuessedTargets={verifyOk} ControlMatching={groundOk} ControlPromptBlock={blockOk} TodoByText={todoOk} " +
                      $"ContextClause={contextOk} NumberedList={numberedOk} SnapToControl={snapOk} Calibration={calibrationOk} DoneNeedsLook={doneOk} ControlAccepted={controlNotSanitized} " +
                      $"TabMatch={tabMatchOk} TabsListed={tabsListedOk} TabTracker={trackerOk} ShellWrites={shellOk} FloatNoise={noiseOk} Clipboard={clipOk} SmallChange={thresholdOk} Replies={replyOk} ReplyProblem={problemOk} Refused={refusedOk} FlagRefused={flagRefused} ReadIsSource={readOk} NoTextWindow={blindOk} Sources={sourceOk} Excerpts={excerptOk} ReadAction={readAction} FactLedger={ledgerOk} ScrollExample={ruleOk}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--agent-speed-self-test", StringComparer.OrdinalIgnoreCase))
{
    // Settle: real no-op pays its budget, a change waits for stillness, an
    // endless animation hits the cap, and a tiny redraw is not a change.
    var change = new FrameDelta(0.05, 0.2);
    var still = new FrameDelta(0, 0);
    var noOp = new SettleTracker(900);
    var noOpOk = !noOp.IsDone(still, still, 200) && !noOp.IsDone(still, still, 800) && noOp.IsDone(still, still, 900) && !noOp.Changed;

    var loads = new SettleTracker(900);
    var loadsOk = !loads.IsDone(change, change, 100) && !loads.IsDone(change, change, 200)
        && !loads.IsDone(change, still, 300) && loads.IsDone(change, still, 430) && loads.Changed;

    var spinner = new SettleTracker(900);
    var spinnerOk = !spinner.IsDone(change, change, 2000) && spinner.IsDone(change, change, 2500);

    var hover = new FrameDelta(0.00003, 0.002);
    var toggle = new FrameDelta(0.0002, 0.03);
    var thresholdOk = !hover.IsChange && !hover.IsMotion && toggle.IsChange
        && ScreenSampler.ToScreenshotDiff(toggle).IsSignificant(0.015) && !ScreenSampler.ToScreenshotDiff(hover).IsSignificant(0.003);

    AgentAction Parse(string json) => CommonUtils.ParseAgentAction(json)!;
    var policyOk = SettlePolicy.NoChangeBudgetMs(Parse("{\"action\":\"done\"}")) == 0
        && SettlePolicy.NoChangeBudgetMs(Parse("{\"action\":\"get_window_state\"}")) == 0
        && SettlePolicy.NoChangeBudgetMs(Parse("{\"action\":\"click\",\"control\":1}")) == 900
        && SettlePolicy.NoChangeBudgetMs(Parse("{\"action\":\"launch_app\",\"executable\":\"x\"}")) == 1500;

    // Pixel comparison: identical frames, one changed area, and the local reading around the pointer.
    ScreenFrame Frame(Action<byte[]> paint)
    {
        var gray = new byte[960 * 600];
        paint(gray);
        return new ScreenFrame(gray, 960, 600, 1536, 960);
    }
    var blank = Frame(_ => { });
    var dot = Frame(g => { for (var y = 300; y < 310; y++) for (var x = 480; x < 500; x++) g[(y * 960) + x] = 255; });
    var same = ScreenSampler.Compare(blank, blank, 100, 100);
    var near = ScreenSampler.Compare(blank, dot, 780, 488);
    var far = ScreenSampler.Compare(blank, dot, 100, 100);
    var speck = Frame(g => { for (var y = 300; y < 303; y++) for (var x = 480; x < 483; x++) g[(y * 960) + x] = 255; });
    var inWindow = ScreenSampler.Compare(blank, dot, null, null, new ScreenRegion(700, 400, 900, 600));
    var elsewhere = ScreenSampler.Compare(blank, dot, null, null, new ScreenRegion(0, 0, 400, 300));
    var compareOk = same.Global == 0 && same.Local == 0 && near.Local > 0.015 && near.IsChange && far.Local == 0
        && !ScreenSampler.Compare(blank, speck).IsChange
        && inWindow.IsChange && Math.Abs(inWindow.Global - far.Global) < 0.000001
        && elsewhere.Global == 0 && !elsewhere.IsChange;

    // Real capture of the live screen, with timing: must be cheap enough to poll.
    var captureWatch = Stopwatch.StartNew();
    ScreenFrame? live = null;
    for (var i = 0; i < 10; i++) live = ScreenSampler.Capture();
    var captureMs = captureWatch.ElapsedMilliseconds / 10;
    var compareWatch = Stopwatch.StartNew();
    if (live is not null) ScreenSampler.Compare(live, live, 500, 500);
    var compareMs = compareWatch.ElapsedMilliseconds;
    var liveOk = live is { Width: > 0, Height: > 0 };

    // Hedging: fast call sends one; slow call sends two and the fast answer
    // wins; a failed hedge doesn't sink a primary; a healthy delay stays long.
    var fastCalls = 0;
    var fast = await HedgedRequest.RunAsync(async token => { fastCalls++; await Task.Delay(5, token); return "a"; }, TimeSpan.FromMilliseconds(500), CancellationToken.None);
    var slowCalls = 0;
    var slow = await HedgedRequest.RunAsync(async token =>
    {
        var call = Interlocked.Increment(ref slowCalls);
        await Task.Delay(call == 1 ? 3000 : 5, token);
        return $"call{call}";
    }, TimeSpan.FromMilliseconds(30), CancellationToken.None);
    var failCalls = 0;
    var survivor = await HedgedRequest.RunAsync(async token =>
    {
        var call = Interlocked.Increment(ref failCalls);
        if (call == 2) throw new InvalidOperationException("hedge failed");
        await Task.Delay(120, token);
        return "primary";
    }, TimeSpan.FromMilliseconds(20), CancellationToken.None);
    var bothFail = false;
    try
    {
        await HedgedRequest.RunAsync<string>(async token => { await Task.Delay(1, token); throw new InvalidOperationException("x"); }, TimeSpan.FromMilliseconds(500), CancellationToken.None);
    }
    catch (InvalidOperationException) { bothFail = true; }
    var noHistory = LlmLatency.HedgeDelay("speed-test|none") is null;
    for (var i = 0; i < 3; i++) LlmLatency.Record("speed-test|m", 2000);
    var learned = LlmLatency.HedgeDelay("speed-test|m") == TimeSpan.FromSeconds(6);
    LlmLatency.NoteRateLimited("speed-test|m");
    var throttled = LlmLatency.HedgeDelay("speed-test|m") is null;
    var hedgeOk = fast == "a" && fastCalls == 1 && slow == "call2" && slowCalls == 2 && survivor == "primary" && bothFail
        && noHistory && learned && throttled
        && LlmLatency.DelayForMedian(500) == TimeSpan.FromSeconds(6) && LlmLatency.DelayForMedian(60_000) == TimeSpan.FromSeconds(25);

    // Provider parameter adaptation, using the error text from the logs.
    var parameterOk =
        OpenAICompatEngine.ClassifyParameterRejection("Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.") == OpenAICompatEngine.ParameterRejection.MaxTokens
        && OpenAICompatEngine.ClassifyParameterRejection("Unsupported value: 'temperature' does not support 0 with this model. Only the default (1) value is supported.") == OpenAICompatEngine.ParameterRejection.Temperature
        && OpenAICompatEngine.ClassifyParameterRejection("Missing required query parameter: api-version") == OpenAICompatEngine.ParameterRejection.None;

    // Per-turn state shrinks after the first turn.
    WorldStateProbe.PrimeSessionCache();
    var world = WorldStateProbe.Capture();
    var first = world.ToPromptBlock(includeInventory: true);
    var later = world.ToPromptBlock(includeInventory: false);
    var worldOk = later.Length <= first.Length && !later.Contains("installed_apps", StringComparison.Ordinal) && !later.Contains("battery", StringComparison.Ordinal);

    var systemPrompt = PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory("windows", 1536, 960, Array.Empty<string>());
    var promptOk = systemPrompt.Contains("\"control\":N", StringComparison.Ordinal)
        && systemPrompt.Contains("ms-settings:windowsupdate", StringComparison.Ordinal)
        && !systemPrompt.Contains("Wait at least 3 seconds", StringComparison.Ordinal)
        && !systemPrompt.Contains("terminate with `done`", StringComparison.Ordinal);

    // Page loads, replaying the failed run: typing + Enter draws at once and then
    // sits still while the page fetches. Stillness alone must not end the wait.
    var submit = Parse("{\"action\":\"type\",\"description\":\"Address and search bar\",\"text\":\"youtube.com\",\"enter\":true}");
    var plan = SettlePolicy.Plan(submit, browserInFront: true);
    var waitsForPage = new SettleTracker(plan);
    var pageOk = !waitsForPage.IsDone(change, change, 40)
        && !waitsForPage.IsDone(change, still, 400)
        && !waitsForPage.IsDone(change, still, 1200)
        && !waitsForPage.IsDone(change, change, 1500, titleChanged: true)
        && !waitsForPage.IsDone(change, still, 1800, titleChanged: true)
        && waitsForPage.IsDone(change, still, 2120, titleChanged: true);
    var neverLoads = new SettleTracker(plan);
    var capOk = !neverLoads.IsDone(change, change, 40) && !neverLoads.IsDone(change, still, 2400) && neverLoads.IsDone(change, still, 2500);
    var videoPage = new SettleTracker(plan);
    var videoOk = !videoPage.IsDone(change, change, 3000, titleChanged: true) && videoPage.IsDone(change, change, 4000, titleChanged: true);
    var outsideBrowser = SettlePolicy.Plan(submit, browserInFront: false);
    var planOk = plan.AwaitTitleMs > 0 && outsideBrowser.AwaitTitleMs == 0
        && SettlePolicy.Plan(Parse("{\"action\":\"press_key\",\"combo\":\"Enter\"}"), true).AwaitTitleMs > 0
        && SettlePolicy.Plan(Parse("{\"action\":\"type\",\"text\":\"hi\"}"), true).AwaitTitleMs == 0
        && SettlePolicy.Plan(Parse("{\"action\":\"batch\",\"steps\":[{\"action\":\"key\",\"keys\":[\"ctrl\",\"t\"]},{\"action\":\"type\",\"text\":\"a\",\"enter\":true}]}"), true).AwaitTitleMs > 0
        && SettlePolicy.Plan(Parse("{\"action\":\"launch_app\",\"executable\":\"https://example.com\"}"), false).AwaitTitleMs > 0
        && SettlePolicy.Plan(Parse("{\"action\":\"click\",\"control\":3}"), true).QuietMs > SettlePolicy.Plan(Parse("{\"action\":\"click\",\"control\":3}"), false).QuietMs;

    // A done is held back only if the screen really moved after the look.
    var doneOk = DonePolicy.ScreenMovedSinceLook(new FrameDelta(0.3, 0)) && !DonePolicy.ScreenMovedSinceLook(new FrameDelta(0.001, 0.5));

    // Tabs: address bar use opens a new tab unless the task is about the open one.
    var tabOk = BrowserPolicy.IsBrowserProcess("msedge") && !BrowserPolicy.IsBrowserProcess("notepad")
        && BrowserPolicy.IsAddressBar("Address and search bar") && BrowserPolicy.IsAddressBar("the address bar at the top of the browser")
        && !BrowserPolicy.IsAddressBar("the Search box") && !BrowserPolicy.IsAddressBar("the shipping address field")
        && BrowserPolicy.GoalAllowsCurrentTab("Reload this tab") && !BrowserPolicy.GoalAllowsCurrentTab("Search for toxic rock gaming on youtube using Microsoft Edge.")
        && BrowserPolicy.IsBlankTab("New tab and 4 more pages - Personal - Microsoft Edge") && !BrowserPolicy.IsBlankTab("India - DeepSeek and 1 more page")
        && BrowserPolicy.IsAddressBarShortcut("ctrl+l") && BrowserPolicy.IsAddressBarShortcut("Alt+D") && BrowserPolicy.IsAddressBarShortcut("F6") && !BrowserPolicy.IsAddressBarShortcut("ctrl+a")
        && BrowserPolicy.IsNewTabShortcut("Ctrl + T") && !BrowserPolicy.IsNewTabShortcut("ctrl+shift+t");
    var combobox = UiControlGrounding.Match("Search combobox at top-center of YouTube page", new List<UiControl>
    {
        new("Search", "combobox", true, false, 500, 100, 400, 40),
        new("Search", "button", true, false, 900, 100, 60, 40),
    });
    var comboOk = combobox?.Role == "combobox";

    // Learned settings survive a restart, and a damaged file is harmless.
    var storeDir = Path.Combine(Path.GetTempPath(), "vantage-capabilities-" + Guid.NewGuid().ToString("N"));
    bool storeOk;
    try
    {
        LlmCapabilities.StorePath = Path.Combine(storeDir, "llm-capabilities.json");
        var saved = new LlmCapabilities();
        saved.SuppressThinking["https://x|m"] = true;
        saved.UseMaxCompletionTokens.Add("https://y|n");
        saved.Save();
        var restored = LlmCapabilities.Load();
        File.WriteAllText(LlmCapabilities.StorePath, "{ not json");
        storeOk = restored.SuppressThinking.TryGetValue("https://x|m", out var thinking) && thinking
            && restored.UseMaxCompletionTokens.Contains("https://y|n")
            && LlmCapabilities.Load().SuppressThinking.Count == 0;
    }
    finally
    {
        LlmCapabilities.StorePath = null;
        try { Directory.Delete(storeDir, recursive: true); } catch { }
    }

    var hedgeOutcome = "";
    var reported = await HedgedRequest.RunAsync(async token =>
    {
        await Task.Delay(Volatile.Read(ref hedgeOutcome) == "" ? 60 : 60, token);
        return "x";
    }, TimeSpan.FromMilliseconds(5), CancellationToken.None, null, outcome => hedgeOutcome = outcome);
    var reportOk = reported == "x" && hedgeOutcome.Contains("answered first", StringComparison.Ordinal);

    var valid = noOpOk && loadsOk && spinnerOk && thresholdOk && policyOk && compareOk && liveOk && hedgeOk && parameterOk && worldOk && promptOk
        && pageOk && capOk && videoOk && planOk && doneOk && tabOk && comboOk && storeOk && reportOk;
    Console.WriteLine($"AgentSpeedValid={valid} NoOp={noOpOk} Loads={loadsOk} Spinner={spinnerOk} Thresholds={thresholdOk} Policy={policyOk} Compare={compareOk} LiveCapture={liveOk} " +
                      $"Hedging={hedgeOk} Parameters={parameterOk} WorldState={worldOk} Prompt={promptOk} PageLoad={pageOk} LoadCap={capOk} VideoCap={videoOk} Plans={planOk} DoneMoved={doneOk} Tabs={tabOk} Combobox={comboOk} Remembered={storeOk} HedgeReported={reportOk}");
    Console.WriteLine($"Measured: capture={captureMs}ms per frame at {live?.Width}x{live?.Height}, compare={compareMs}ms, world state first turn={first.Length} chars, later={later.Length} chars, system prompt={systemPrompt.Length} chars");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args.Contains("--agent-loop-self-test", StringComparer.OrdinalIgnoreCase))
{
    AgentAction Act(string json) => CommonUtils.ParseAgentAction(json)!;
    UiControl Named(string name, string role) => new(name, role, true, false, 10, 10, 100, 30);

    // A control number whose name is not what the description says is refused.
    const string desktop = "Desktop in the left navigation pane of Save As dialog";
    var contradictOk = UiControlGrounding.DescriptionContradicts(desktop, Named("Save as type:", "combobox"))
        && !UiControlGrounding.DescriptionContradicts(desktop, Named("Desktop", "tree-item"))
        && !UiControlGrounding.DescriptionContradicts(null, Named("Save as type:", "combobox"))
        && !UiControlGrounding.DescriptionContradicts("the Australia tab", Named("Australia - Wikipedia", "tab-item"))
        && !UiControlGrounding.DescriptionContradicts("the address bar", Named("Address and search bar", "edit"))
        && !UiControlGrounding.DescriptionContradicts("the File name field", Named("File name:", "edit"))
        && !UiControlGrounding.DescriptionContradicts("the first result", Named("Mount Everest", "hyperlink"))
        && !UiControlGrounding.DescriptionContradicts("the Downloads folder", Named("Download", "tree-item"))
        && !UiControlGrounding.DescriptionContradicts("Save as type dropdown", Named("Save as type:", "combobox"));

    // The same action is refused at 5 and stopped at 10, including alternating loops.
    var same = new RepeatGuard();
    var counts = Enumerable.Range(0, 10).Select(_ => same.Register(Act("{\"action\":\"click\",\"control\":31,\"description\":\"Desktop\"}"))).ToList();
    var sameOk = counts.SequenceEqual(Enumerable.Range(1, 10));

    var alternating = new RepeatGuard();
    var alternated = 0;
    for (var i = 0; i < 10; i++) alternated = alternating.Register(Act(i % 2 == 0 ? "{\"action\":\"key\",\"combo\":\"a\"}" : "{\"action\":\"key\",\"combo\":\"b\"}"));

    var varied = new RepeatGuard();
    var variedMost = Enumerable.Range(0, 20).Select(i => varied.Register(Act($"{{\"action\":\"type_text\",\"text\":\"row {i}\"}}"))).Max();

    var expectations = new RepeatGuard();
    expectations.Register(Act("{\"action\":\"click\",\"control\":1,\"expected_state\":\"opens\"}"));
    var expectationsCount = expectations.Register(Act("{\"action\":\"click\",\"control\":1,\"expected_state\":\"opens now\"}"));

    var exempt = new RepeatGuard();
    var exemptMost = Enumerable.Range(0, 20).Select(_ => exempt.Register(Act("{\"action\":\"scroll\",\"clicks\":-5}"))).Max();

    // Replays the Save As loop: Save, then OK on the error, worded differently
    // each round. It is refused by the tenth click and stopped by the twentieth.
    var saveAs = Enumerable.Range(0, 32).Select(i => i == 31 ? Named("Save", "button") : Named($"Item {i}", "list-item")).ToList();
    var errorBox = new List<UiControl> { Named("OK", "button") };
    var okWordings = new[]
    {
        "{\"action\":\"click\",\"control\":0,\"description\":\"OK button in the path error dialog\",\"intent\":\"dismiss the path does not exist error so I can create the folders first\"}",
        "{\"action\":\"click\",\"control\":0}",
        "{\"action\":\"click\",\"control\":0,\"description\":\"OK button in the error dialog\",\"intent\":\"dismiss the path error dialog so I can create the folder structure first\"}",
    };
    var saveLoop = new RepeatGuard();
    var loopCounts = new List<int>();
    for (var round = 0; round < 10; round++)
    {
        var into = round % 2 == 0 ? "to" : "into";
        loopCounts.Add(saveLoop.Register(Act($"{{\"action\":\"click\",\"control\":31,\"description\":\"Save button in the Save As dialog\",\"intent\":\"save Canada.txt {into} the raw folder\"}}"), saveAs));
        loopCounts.Add(saveLoop.Register(Act(okWordings[round % 3]), errorBox));
    }
    var saveLoopOk = loopCounts[9] >= RepeatGuard.RefuseAt && loopCounts[19] >= RepeatGuard.StopAt;

    // The same number on another screen is another control.
    var renumbered = new RepeatGuard();
    renumbered.Register(Act("{\"action\":\"click\",\"control\":0}"), errorBox);
    var otherControl = renumbered.Register(Act("{\"action\":\"click\",\"control\":0}"), new List<UiControl> { Named("Cancel", "button") });

    // Steps of a batch are compared by what they do, not how they are worded.
    var batches = new RepeatGuard();
    batches.Register(Act("{\"action\":\"batch\",\"actions\":[{\"action\":\"click\",\"control\":31,\"description\":\"Save\"},{\"action\":\"key\",\"combo\":\"enter\",\"intent\":\"confirm\"}]}"), saveAs);
    var batchCount = batches.Register(Act("{\"action\":\"batch\",\"actions\":[{\"action\":\"click\",\"control\":31,\"description\":\"the Save button\"},{\"action\":\"key\",\"combo\":\"enter\",\"intent\":\"confirm it\"}]}"), saveAs);
    // A plain click on a text box is recognised, and the model is told the one action to send next.
    var dialog = new List<UiControl> { Named("File name:", "combobox"), Named("File name:", "edit"), Named("Save", "button") };
    var field = UiControlGrounding.TextFieldClicked(Act("{\"action\":\"click\",\"control\":1,\"expected_state\":\"The file name field will be focused\"}"), dialog);
    var saveHint = field is null ? "" : UiControlGrounding.TypeIntoField(field, 1, "Save as");
    var noteHint = field is null ? "" : UiControlGrounding.TypeIntoField(field, 1, "Untitled - Notepad");
    var fieldOk = field?.Name == "File name:"
        && UiControlGrounding.TextFieldClicked(Act("{\"action\":\"double_click\",\"control\":1}"), dialog) is null
        && UiControlGrounding.TextFieldClicked(Act("{\"action\":\"click\",\"control\":1,\"button\":\"right\"}"), dialog) is null
        && UiControlGrounding.TextFieldClicked(Act("{\"action\":\"click\",\"control\":2}"), dialog) is null
        && UiControlGrounding.TextFieldClicked(Act("{\"action\":\"click\",\"control\":9}"), dialog) is null
        && saveHint.Contains("{\"action\":\"type\",\"control\":1,\"text\":\"<the file's whole path>\",\"overwrite\":true}", StringComparison.Ordinal)
        && noteHint.Contains("\"text\":\"<the text>\"", StringComparison.Ordinal)
        && PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory("windows", 1536, 960, Array.Empty<string>())
            .Contains("never click a field first", StringComparison.Ordinal);

    var repeatOk = sameOk && alternated == 5 && variedMost == 1 && expectationsCount == 2 && exemptMost == 0
        && saveLoopOk && otherControl == 1 && batchCount == 2 && fieldOk;

    // Asking again returns the next matches, then says when they run out.
    var matches = string.Join(' ', Enumerable.Range(1, 9).Select(i => $"Area {i} " + new string('x', 400)));
    var progress = new Dictionary<string, HashSet<string>>();
    var pages = new List<(int Count, bool More)>();
    for (var i = 0; i < 4; i++)
    {
        var excerpts = PageTextReader.ExtractExcerpts(matches, new[] { "Area" }, progress, out var more);
        pages.Add((excerpts.Count, more));
    }
    var restarted = PageTextReader.ExtractExcerpts(matches, new[] { "Area" }, progress, out _);
    // Calculator keeps its title while its display changes. Gemini's second read
    // of "Display" came back empty, so four of five results were never read.
    var calcProgress = new Dictionary<string, HashSet<string>>();
    var firstSum = PageTextReader.ExtractExcerpts("Calculator Expression is 41798407 ÷ 9984670= Display is 4.186258233872527 Standard Calculator mode", new[] { "Display" }, calcProgress, out _);
    var secondSum = PageTextReader.ExtractExcerpts("Calculator Expression is 27801023 ÷ 7688287= Display is 3.616022002849797 Standard Calculator mode", new[] { "Display" }, calcProgress, out _);
    var sameAgain = PageTextReader.ExtractExcerpts("Calculator Expression is 27801023 ÷ 7688287= Display is 3.616022002849797 Standard Calculator mode", new[] { "Display" }, calcProgress, out _);
    var pagingOk = pages.SequenceEqual(new[] { (4, true), (4, true), (1, false), (0, false) })
        && restarted.Count == 4 && restarted[0].Contains("Area 1 ", StringComparison.Ordinal)
        && firstSum.Count == 1 && firstSum[0].Contains("4.186258233872527", StringComparison.Ordinal)
        && secondSum.Count == 1 && secondSum[0].Contains("3.616022002849797", StringComparison.Ordinal)
        && sameAgain.Count == 0;

    var loopPrompt = PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory("windows", 1536, 960, Array.Empty<string>());
    var loopRuleOk = loopPrompt.Contains("Save As", StringComparison.Ordinal) && loopPrompt.Contains("next matches", StringComparison.Ordinal);

    // Replays the five-country run: the first page's figures outlast 292 passages
    // from other pages, stay visible, and an invented figure is still refused.
    var longRead = new FactLedger();
    longRead.RecordRead(new[] { "Area • Total 9,984,670 km2", "Population • 2026 Q2 estimate 41,798,407" }, "Canada - Wikipedia and 5 more pages - Personal - Microsoft​ Edge");
    var countries = new[] { "Australia", "Egypt", "Argentina", "Japan" };
    for (var i = 0; i < 292; i++)
        longRead.RecordRead(new[] { $"Area passage {100000 + i}" }, $"{countries[i % 4]} - Wikipedia - Personal - Microsoft​ Edge");
    var longPrompt = longRead.BuildPromptBlock();
    var keptValues = longRead.BlockedValues(Act("{\"action\":\"type_text\",\"text\":\"Area: 9,984,670 km²\\nPopulation: 41,798,407\"}"), Array.Empty<string?>()).Count == 0;
    var inventedHeld = longRead.BlockedValues(Act("{\"action\":\"type_text\",\"text\":\"Area: 7,692,024 km²\"}"), Array.Empty<string?>()).Count == 1;
    var labelled = longPrompt.Contains("From \"Canada - Wikipedia\":", StringComparison.Ordinal)
        && longPrompt.Contains("41,798,407", StringComparison.Ordinal)
        && longPrompt.Contains("From \"Japan - Wikipedia\":", StringComparison.Ordinal);

    // Only passages not read before count as new, however long ago the first read was.
    var readAgain = new FactLedger();
    var firstRead = readAgain.RecordRead(new[] { "Capital Ottawa" }, "Canada");
    var secondRead = readAgain.RecordRead(new[] { "Capital Ottawa" }, "Canada");
    var ledgerKeepsOk = keptValues && inventedHeld && labelled && firstRead == 1 && secondRead == 0
        && FactLedger.PageLabel("Canada - Wikipedia - Microsoft Edge") == "Canada - Wikipedia"
        && FactLedger.PageLabel("Canada.txt - Notepad") == "Canada.txt - Notepad";

    // Paging that keeps finding new text does not add up to a refusal.
    var paged = new RepeatGuard();
    var pagedMost = 0;
    for (var i = 0; i < 12; i++)
    {
        pagedMost = Math.Max(pagedMost, paged.Register(Act("{\"action\":\"read_text\",\"find\":\"Population\"}")));
        paged.Clear();
    }
    var pagedOk = pagedMost == 1;

    // A save is recognised from the titles the real run went through, and nothing else is.
    var savedOk = SavedFileTitle.Detect("Save as", "Canada.txt - Notepad") == "Canada.txt"
        && SavedFileTitle.Detect("*Canada.txt - Notepad", "Canada.txt - Notepad") == "Canada.txt"
        && SavedFileTitle.Detect("Save as", "Untitled - Notepad") is null
        && SavedFileTitle.Detect("Save as", "*Canada.txt - Notepad") is null
        && SavedFileTitle.Detect("*Report.docx - Word", "Canada.txt - Notepad") is null
        && SavedFileTitle.Detect("Canada - Wikipedia", "Canada.txt - Notepad") is null
        && SavedFileTitle.Detect("Canada.txt - Notepad", "Canada.txt - Notepad") is null;

    // The second country's text is held back while the window still holds the first country's file.
    var finishedList = new[] { "Saved Canada.txt (the window title confirms it)" };
    var intoFinished = SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"type_text\",\"text\":\"Capital: Canberra\",\"target_title\":\"Canada.txt - Notepad\"}"), "Canada.txt - Notepad", finishedList) == "Canada.txt"
        && SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"type_text\",\"text\":\"Capital: Canberra\",\"overwrite\":true}"), "Canada.txt - Notepad", finishedList) == "Canada.txt"
        && SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"type_text\",\"text\":\"Capital: Canberra\"}"), "Untitled - Notepad", finishedList) is null
        && SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"type_text\",\"text\":\"Capital: Canberra\"}"), "*Canada.txt - Notepad", finishedList) is null
        && SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"type_text\",\"text\":\"x\"}"), "Notes.txt - Notepad", finishedList) is null
        && SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"type\",\"control\":3,\"text\":\"Canada.txt\"}"), "Canada.txt - Notepad", finishedList) is null
        && SavedFileTitle.FinishedFileTypedInto(Act("{\"action\":\"press_key\",\"combo\":\"ctrl+n\"}"), "Canada.txt - Notepad", finishedList) is null
        && SavedFileTitle.SavedName("Canada.txt - Notepad") == "Canada.txt" && SavedFileTitle.SavedName("*Canada.txt - Notepad") is null && SavedFileTitle.SavedName("Save as") is null;
    var notepadRule = loopPrompt.Contains("ctrl+n", StringComparison.Ordinal) && loopPrompt.Contains("Ctrl+Shift+S", StringComparison.Ordinal);
    savedOk = savedOk && intoFinished && notepadRule;

    // The Calculator has to be the one that gave the answer, when the task names it.
    var calculatorOk = RequiredToolUse.CalculatorRequired("Use the Windows Calculator app to work out each density")
        && !RequiredToolUse.CalculatorRequired("Work out each density and save it")
        && RequiredToolUse.IsCalculatorWindow("Calculator")
        && !RequiredToolUse.IsCalculatorWindow("Canada.txt - Notepad")
        && loopPrompt.Contains("Display", StringComparison.Ordinal) && loopPrompt.Contains("until an answer has been read from Calculator", StringComparison.Ordinal);

    // When the task names Notepad, PowerShell may not write the files for it.
    AgentAction Pwsh(string command) => CommonUtils.ParseAgentAction(
        JsonSerializer.Serialize(new { action = "run_powershell", command }))!;
    // Gemini 3.8 Flash's command, word for word up to where it was cut off.
    const string geminiWrite = "$raw = 'C:\\Users\\Akshith\\Desktop\\Vantage Final Test\\raw'\n$report = 'C:\\Users\\Akshith\\Desktop\\Vantage Final Test\\report'\n\nSet-Content -Path \"$raw\\Canada.txt\" -Value @(\"Capital: Ottawa\", \"Area: 9,984,670 km²\", \"Population: 41,798,407\") -Encoding utf8";
    var writes = new[]
    {
        geminiWrite,
        "\"Capital: Ottawa\" | Out-File \"C:\\x\\raw\\Canada.txt\"",
        "\"Capital: Ottawa\" > \"C:\\x\\raw\\Canada.txt\"",
        "$lines >> $path",
        "[IO.File]::WriteAllLines($p, $lines)",
        "[System.IO.File]::AppendAllText($p, 'x')",
        "New-Item -Path \"$raw\\Canada.txt\" -ItemType File -Value \"Capital: Ottawa\"",
        "$w = New-Object System.IO.StreamWriter($p); $w.WriteLine('x')",
        "add-content $p 'Population: 1'",
    };
    var allowed = new[]
    {
        "New-Item -ItemType Directory -Path \"$base\\raw\",\"$base\\report\" -Force | Out-Null",
        "Compress-Archive -Path \"$base\\raw\" -DestinationPath \"$base\\report\\raw.zip\" -Force",
        "Get-Content \"C:\\x\\raw\\Canada.txt\"",
        "[math]::Round(41798407/9984670, 1)",
        "Stop-Process -Name notepad 2>$null; Get-Process calc* 2>&1 > $null",
        "Get-ChildItem | Where-Object { $_.Length -gt 0 }",
        "Write-Output \"Canada -> 4.2 > 3\"",
        "New-ItemProperty -Path HKCU:\\Software\\X -Name A -Value 1",
    };
    var batchWrite = CommonUtils.ParseAgentAction(JsonSerializer.Serialize(new
    {
        action = "batch",
        steps = new object[]
        {
            new { action = "launch_app", target = "notepad" },
            new { action = "run_powershell", command = "Set-Content C:\\x\\a.txt 'x'" },
        },
    }))!;
    var notepadOk = RequiredToolUse.NotepadRequired("For each country, use Notepad to save a file in the raw folder")
        && RequiredToolUse.NotepadRequired("In Notepad, type the list and save it as list.txt")
        && RequiredToolUse.NotepadRequired("Open Notepad to write a note")
        && !RequiredToolUse.NotepadRequired("Close Notepad, Calculator and Settings")
        && !RequiredToolUse.NotepadRequired("Save the list as list.txt")
        && writes.All(command => RequiredToolUse.WritesFileWithPowerShell(Pwsh(command)))
        && allowed.All(command => !RequiredToolUse.WritesFileWithPowerShell(Pwsh(command)))
        && RequiredToolUse.WritesFileWithPowerShell(batchWrite)
        && !RequiredToolUse.WritesFileWithPowerShell(CommonUtils.ParseAgentAction("{\"action\":\"type_text\",\"text\":\"a > b.txt\"}")!)
        && loopPrompt.Contains("29. When the task says to use Notepad", StringComparison.Ordinal);

    // Paths with spaces: whole, or not at all. New-Item's output once gave the
    // model C:\Users\Akshith\Desktop\Vantage as a fact on every turn.
    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    ContextWindowCompactor.ExtractEntities(
        "Current Text Buffer = [pwsh_exit=0\nout:\n\n    Directory: C:\\Users\\Akshith\\Desktop\\Vantage Final Test\n\nMode                 LastWriteTime         Length Name\n----                 -------------         ------ ----\nd----          02-11-2026    14:39                raw\n]\n" +
        "Saved C:\\Users\\Akshith\\Desktop\\Vantage Final Test\\raw\\Canada.txt and C:\\Temp\\notes.txt. Opened C:\\Users\\Akshith\\Desktop\\Vantage Final Test\\report\n" +
        "Look in C:\\Program Files\\App\\app.exe, then C:\\Windows.",
        found);
    var entitiesOk = found.Contains("C:\\Users\\Akshith\\Desktop\\Vantage Final Test")
        && found.Contains("C:\\Users\\Akshith\\Desktop\\Vantage Final Test\\raw\\Canada.txt")
        && found.Contains("C:\\Users\\Akshith\\Desktop\\Vantage Final Test\\report")
        && found.Contains("C:\\Temp\\notes.txt")
        && found.Contains("C:\\Program Files\\App\\app.exe")
        && found.Contains("C:\\Windows")
        && !found.Contains("C:\\Users\\Akshith\\Desktop\\Vantage");

    // The chat box ends lines with a bare \r. Its paragraphs are the steps.
    const string finalTest = "On my Desktop, create a folder called Vantage Final Test containing two folders named raw and report.\r\rIn Microsoft Edge, open the Wikipedia articles for Canada, Australia, Egypt, Argentina and Japan.\r\rFor each country, use Notepad to save a file in the raw folder, with exactly these three lines:\rCapital: <capital>\rArea: <area> km²\rPopulation: <population>\r\rUse the Windows Calculator app to work out each country's population density.\r\rOpen Settings, go to System, then About, and find the Windows version. Do not change any settings.\r\rIn the report folder, create summary.txt. After those lines, add a blank line, then these two lines:\rTotal area: <sum> km²\r\rCompress the raw folder into a file called raw.zip inside the report folder. Close Notepad, Calculator and Settings, then open the report folder in File Explorer and leave it open.";
    var steps = PersistentTaskContext.BuildInitialTodos(finalTest);
    var manySteps = PersistentTaskContext.BuildInitialTodos(string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"Do step number {i} now")));
    var todosOk = steps.Count == 7
        && steps[0].StartsWith("On my Desktop", StringComparison.Ordinal)
        && steps[2].Contains("Capital: <capital> Area: <area> km² Population: <population>", StringComparison.Ordinal)
        && steps[4] == "Open Settings, go to System, then About, and find the Windows version. Do not change any settings"
        && steps[6].EndsWith("open the report folder in File Explorer and leave it open", StringComparison.Ordinal)
        && manySteps.Count == 8 && manySteps[7] == "Do step number 8 now Do step number 9 now Do step number 10 now"
        && PersistentTaskContext.BuildInitialTodos("Open Edge, then navigate to the repository, then send the result").Count == 3
        && PersistentTaskContext.BuildInitialTodos("Line one of a task\r- first item here\r- second item here").Count == 3;

    // Finished work stays in the prompt however many milestones follow it.
    var contextRoot = Path.Combine(Path.GetTempPath(), "vantage-probe-" + Guid.NewGuid().ToString("N"));
    var finishedOk = false;
    try
    {
        var finishedContext = new PersistentTaskContext("probe", contextRoot);
        finishedContext.BeginTask("Save five files");
        finishedContext.RecordFinishedWork("Saved Canada.txt (the window title confirms it)");
        for (var i = 0; i < 40; i++) finishedContext.RecordMilestone($"step {i}");
        var finishedPrompt = finishedContext.BuildPromptBlock();
        var reloaded = new PersistentTaskContext("probe", contextRoot);
        finishedOk = finishedPrompt.Contains("Saved Canada.txt", StringComparison.Ordinal)
            && !finishedPrompt.Contains("step 0", StringComparison.Ordinal)
            && reloaded.FinishedWork.Count == 1;
    }
    finally
    {
        try { Directory.Delete(contextRoot, true); } catch { }
    }

    // No excerpt starts or ends inside a number, wherever the number falls.
    var wholeNumbers = true;
    for (var pad = 0; pad < 80 && wholeNumbers; pad++)
    {
        var before = $"Area 2,780,085[7] {new string('y', pad)} Population 5";
        var leading = PageTextReader.ExtractExcerpts(before, new[] { "Population" })[0];
        var after = $"Population 5 {new string('y', 170 + pad)} 46,735,004 km2 and more text here";
        var trailing = PageTextReader.ExtractExcerpts(after, new[] { "Population" })[0];
        wholeNumbers = (!leading.Contains("80,085", StringComparison.Ordinal) || leading.Contains("2,780,085", StringComparison.Ordinal))
            && (!FactLedger.ExtractValues(trailing).Keys.Any(key => key != "46735004" && "46735004".Contains(key, StringComparison.Ordinal)));
    }

    // A second attempt at the same goal does not trust the first attempt's files.
    var resumeRoot = Path.Combine(Path.GetTempPath(), "vantage-probe-" + Guid.NewGuid().ToString("N"));
    var resumeOk = false;
    try
    {
        var resumeContext = new PersistentTaskContext("probe", resumeRoot);
        resumeContext.BeginTask("Save five files");
        resumeContext.RecordFinishedWork("Saved Canada.txt (the window title confirms it)");
        resumeContext.BeginTask("Save five files");
        resumeOk = resumeContext.FinishedWork.Count == 0;
    }
    finally
    {
        try { Directory.Delete(resumeRoot, true); } catch { }
    }

    var osOk = WorldStateProbe.OsName("Windows 10 Pro", "26300") == "Windows 11 Pro"
        && WorldStateProbe.OsName("Windows 10 Pro", "19045") == "Windows 10 Pro"
        && WorldStateProbe.OsName("Windows 10 Pro", null) == "Windows 10 Pro";

    // An answer given to the user is held to the same standard as typed text.
    var answers = new FactLedger();
    answers.RecordRead(new[] { "Population 41,798,407" }, "Canada");
    var answerOk = answers.BlockedValues(Act("{\"action\":\"done\",\"answer\":\"Canada has 41,798,407 people\"}"), Array.Empty<string?>()).Count == 0
        && answers.BlockedValues(Act("{\"action\":\"done\",\"answer\":\"Canada has 41,465,298 people\"}"), Array.Empty<string?>()).Count == 1
        && answers.BlockedValues(Act("{\"action\":\"done\"}"), Array.Empty<string?>()).Count == 0
        && loopPrompt.Contains("`answer` field", StringComparison.Ordinal);

    // A bare file name in a save dialog is caught; a full path or another window is not.
    var bareOk = SavedFileTitle.BareNameInSaveDialog(Act("{\"action\":\"type\",\"control\":3,\"text\":\"Canada.txt\"}"), "Save as") == "Canada.txt"
        && SavedFileTitle.BareNameInSaveDialog(Act("{\"action\":\"type\",\"text\":\"C:\\\\Users\\\\Name\\\\Canada.txt\"}"), "Save as") is null
        && SavedFileTitle.BareNameInSaveDialog(Act("{\"action\":\"type_text\",\"text\":\"Canada.txt\"}"), "Canada.txt - Notepad") is null
        && SavedFileTitle.BareNameInSaveDialog(Act("{\"action\":\"type_text\",\"text\":\"some words\"}"), "Save as") is null;

    // A log over the limit moves aside once; one under it stays where it is.
    var logFolder = Path.Combine(Path.GetTempPath(), "vantage-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(logFolder);
    var logFile = Path.Combine(logFolder, "test.log");
    File.WriteAllText(logFile, "0123456789");
    CommonUtils.RotateIfLarge(logFile, 100);
    var keptSmall = File.Exists(logFile) && !File.Exists(logFile + ".old");
    CommonUtils.RotateIfLarge(logFile, 5);
    var movedLarge = !File.Exists(logFile) && File.Exists(logFile + ".old");
    File.WriteAllText(logFile, "abcdefghij");
    CommonUtils.RotateIfLarge(logFile, 5);
    var rotateOk = keptSmall && movedLarge && File.ReadAllText(logFile + ".old") == "abcdefghij";
    try { Directory.Delete(logFolder, true); } catch { }

    var windowsOk = ContextWindowLimits.GetModelContextWindow("gpt-4.1") == 1_000_000
        && ContextWindowLimits.GetModelContextWindow("gpt-5") > 100_000
        && ContextWindowLimits.GetModelContextWindow("gpt-4") == 8_192;

    // Every action the agent is told about has somewhere to go.
    var promptActions = System.Text.RegularExpressions.Regex.Matches(loopPrompt, @"^## (\w+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
        .Select(match => match.Groups[1].Value).ToList();
    var aciSource = FindSource("Services", "Agent", "VantageACI.cs");
    var missingActions = aciSource is null
        ? new List<string>()
        : promptActions.Where(name => !aciSource.Contains($"\"{name}\"", StringComparison.Ordinal)).ToList();
    var docsOk = promptActions.Count > 20 && aciSource is not null && missingActions.Count == 0
        && !loopPrompt.Contains("snap_window", StringComparison.Ordinal);

    // Command output outlasts the short list the action layer keeps.
    var outputs = new FactLedger();
    outputs.RememberOutput(new[] { "pwsh_exit=0 out: 325.3" });
    outputs.RememberOutput(Enumerable.Range(0, 40).Select(i => $"pwsh_exit=0 out: {1000 + i}"));
    var outputOk = outputs.BlockedValues(Act("{\"action\":\"type_text\",\"text\":\"Japan 325.3\"}"), Array.Empty<string?>()).Count == 0
        && outputs.BlockedValues(Act("{\"action\":\"type_text\",\"text\":\"Japan 325.9\"}"), Array.Empty<string?>()).Count == 1;

    // A batch cannot walk past the checks on typing by putting it in a first step.
    var batched = Act("{\"action\":\"batch\",\"steps\":[{\"action\":\"type_text\",\"text\":\"Capital: Canberra\"},{\"action\":\"key\",\"keys\":[\"ctrl\",\"s\"]}]}");
    var newDocument = Act("{\"action\":\"batch\",\"steps\":[{\"action\":\"press_key\",\"combo\":\"ctrl+n\"},{\"action\":\"type_text\",\"text\":\"Capital: Canberra\"}]}");
    var batchOk = SavedFileTitle.FinishedFileTypedInto(batched, "Canada.txt - Notepad", finishedList) == "Canada.txt"
        && SavedFileTitle.FinishedFileTypedInto(newDocument, "Canada.txt - Notepad", finishedList) is null
        && SavedFileTitle.BareNameInSaveDialog(Act("{\"action\":\"batch\",\"steps\":[{\"action\":\"type\",\"text\":\"Canada.txt\"}]}"), "Save as") == "Canada.txt";

    // Follow-up context keeps the latest turns, trimmed, and skips empty ones.
    var earlierText = EarlierConversation.Build(Enumerable.Range(1, 9)
        .Select(i => (Role: i % 2 == 1 ? "user" : "assistant", Text: (string?)(i == 8 ? " " : $"turn {i}")))
        .Append((Role: "assistant", Text: (string?)new string('z', 900))));
    var earlierOk = !earlierText.Contains("turn 1\r", StringComparison.Ordinal) && !earlierText.Contains("turn 2", StringComparison.Ordinal)
        && earlierText.Contains("User: turn 9", StringComparison.Ordinal) && earlierText.Contains("Vantage: zzz", StringComparison.Ordinal)
        && !earlierText.Contains(new string('z', 500), StringComparison.Ordinal)
        && EarlierConversation.Build(Array.Empty<(string, string?)>()).Length == 0;

    var loopOk = contradictOk && repeatOk && pagingOk && loopRuleOk && ledgerKeepsOk && pagedOk && savedOk && finishedOk && calculatorOk && notepadOk && entitiesOk && todosOk && wholeNumbers && resumeOk && osOk && answerOk && bareOk && rotateOk && windowsOk && docsOk && outputOk && batchOk && earlierOk;
    Console.WriteLine($"agent-loop self-test: contradict={contradictOk} repeat={repeatOk} (same={sameOk} alternated={alternated} varied={variedMost} expectations={expectationsCount} exempt={exemptMost}) paging={pagingOk} rule={loopRuleOk} ledgerKeeps={ledgerKeepsOk} (kept={keptValues} invented={inventedHeld} labelled={labelled} first={firstRead} second={secondRead}) paged={pagedOk} saved={savedOk} finished={finishedOk} calculator={calculatorOk} notepad={notepadOk} entities={entitiesOk} todos={todosOk} wholeNumbers={wholeNumbers} resume={resumeOk} os={osOk} answer={answerOk} bare={bareOk} rotate={rotateOk} windows={windowsOk} docs={docsOk} (missing={string.Join(',', missingActions)}) output={outputOk} batch={batchOk} earlier={earlierOk}");
    Environment.ExitCode = loopOk ? 0 : 1;
    return;

    // Walks up from the running probe to the repository's source tree.
    static string? FindSource(params string[] parts)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(new[] { folder.FullName, "src" }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}

if (args.Contains("--agent-turn-handles", StringComparer.OrdinalIgnoreCase))
{
    // The reads an agent turn makes, against a real window. Read-only. Usage:
    // --agent-turn-handles <process name> [rounds]. Handles should level off.
    var turnArgs = args.SkipWhile(a => !a.Equals("--agent-turn-handles", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
    var owner = System.Diagnostics.Process.GetProcessesByName(turnArgs.ElementAtOrDefault(0) ?? "msedge").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
    if (owner is null)
    {
        Console.WriteLine("no window found");
        Environment.ExitCode = 1;
        return;
    }
    var turnRounds = int.TryParse(turnArgs.ElementAtOrDefault(1), out var parsedRounds) ? parsedRounds : 200;
    var self = System.Diagnostics.Process.GetCurrentProcess();
    for (var round = 1; round <= turnRounds; round++)
    {
        WorldStateProbe.Capture();
        await UiControlGrounding.SnapshotAsync(owner.MainWindowHandle, TimeSpan.FromSeconds(2));
        await PageTextReader.ReadAsync(owner.MainWindowHandle, TimeSpan.FromSeconds(3));
        ScreenSampler.Capture();
        ForegroundWindowProbe.Read();
        if (round % 25 != 0) continue;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        self.Refresh();
        Console.WriteLine($"round={round} handles={self.HandleCount} privateMB={self.PrivateMemorySize64 / (1024 * 1024)}");
    }
    return;
}

if (args.Contains("--handle-growth", StringComparer.OrdinalIgnoreCase))
{
    // Calls what the agent calls every turn and watches the process's handle
    // count, which should stay flat however long a run goes on.
    var process = System.Diagnostics.Process.GetCurrentProcess();
    process.Refresh();
    var startHandles = process.HandleCount;
    var appsSeen = WindowsAppManager.ListRunningApps().Count;
    Console.WriteLine($"start handles={startHandles} apps={appsSeen}");
    var growthRounds = 30_000;
    for (var round = 1; round <= growthRounds; round++)
    {
        WindowsAppManager.ListRunningApps();
        WindowsAppManager.GetFrontmostApp();
        if (round % 250 != 0) continue;
        process.Refresh();
        Console.WriteLine($"round={round} handles={process.HandleCount}");
        if (process.HandleCount < startHandles + 500 && round >= 5000) break;
    }
    process.Refresh();
    var flat = process.HandleCount < startHandles + 500;
    Console.WriteLine($"end handles={process.HandleCount} flat={flat}");
    Environment.ExitCode = flat ? 0 : 1;
    return;
}

if (args.Contains("--screen-noise", StringComparer.OrdinalIgnoreCase))
{
    // How much an untouched screen changes between frames, for the change threshold.
    var noiseSamples = new List<double>();
    var previousFrame = ScreenSampler.Capture();
    for (var i = 0; i < 40; i++)
    {
        await Task.Delay(250);
        var currentFrame = ScreenSampler.Capture();
        if (previousFrame is not null && currentFrame is not null) noiseSamples.Add(ScreenSampler.Compare(previousFrame, currentFrame).Global);
        previousFrame = currentFrame;
    }
    noiseSamples.Sort();
    Console.WriteLine($"samples={noiseSamples.Count} median={noiseSamples[noiseSamples.Count / 2]:P4} p90={noiseSamples[(int)(noiseSamples.Count * 0.9)]:P4} max={noiseSamples[^1]:P4}");
    return;
}

if (args.Contains("--calculator-live", StringComparer.OrdinalIgnoreCase))
{
    // Types a sum into Calculator the way the agent would, then reads the answer.
    // Refuses to type unless Calculator really has focus.
    var calculator = WindowsAppManager.ListVisibleWindows().FirstOrDefault(w => w.Title.Equals("Calculator", StringComparison.OrdinalIgnoreCase));
    if (calculator is null)
    {
        Process.Start(new ProcessStartInfo("calc") { UseShellExecute = true });
        await Task.Delay(2000);
        calculator = WindowsAppManager.ListVisibleWindows().FirstOrDefault(w => w.Title.Equals("Calculator", StringComparison.OrdinalIgnoreCase));
    }
    if (calculator is null || !WindowsAppManager.FocusWindow(calculator) || ForegroundWindowProbe.Read().Title != "Calculator")
    {
        Console.WriteLine("Calculator did not take focus; nothing was typed");
        Environment.ExitCode = 1;
        return;
    }
    WindowsAppManager.PressKey("escape");
    await Task.Delay(150);
    var region = ForegroundWindowProbe.ReadRegion();

    // How much the window changes on its own while nothing is being done.
    var quietFrames = new List<double>();
    var quietPrevious = ScreenSampler.Capture();
    for (var i = 0; i < 12; i++)
    {
        await Task.Delay(200);
        var quietFrame = ScreenSampler.Capture();
        if (quietPrevious is not null && quietFrame is not null) quietFrames.Add(ScreenSampler.Compare(quietPrevious, quietFrame, null, null, region).Global);
        quietPrevious = quietFrame;
    }
    Console.WriteLine($"window at rest: max={quietFrames.Max():P4}");

    await WindowsAppManager.TypeTextAsync("8848.86", 0, CancellationToken.None);
    await Task.Delay(300);
    var beforeMinus = ScreenSampler.Capture();
    await WindowsAppManager.TypeTextAsync("-", 0, CancellationToken.None);
    await Task.Delay(300);
    var afterMinus = ScreenSampler.Capture();
    var minusDelta = ScreenSampler.Compare(beforeMinus!, afterMinus!, null, null, region);
    Console.WriteLine($"minus changed {minusDelta.Global:P4} of screen, IsChange={minusDelta.IsChange}");

    var typedChars = await WindowsAppManager.TypeTextAsync("8611=", 0, CancellationToken.None);
    await Task.Delay(400);
    var shown = await PageTextReader.ReadAsync(calculator.Handle, TimeSpan.FromSeconds(3));
    Console.WriteLine($"typed={typedChars} answer_found={shown.Contains("237.86", StringComparison.Ordinal)} text: " + string.Join(" | ", shown.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6)));
    return;
}

if (args.Contains("--read-text-live", StringComparer.OrdinalIgnoreCase))
{
    // Usage: --read-text-live <part of a window title> <search word>. Read-only.
    var titlePart = args.Length > 1 ? args[1] : "";
    var search = args.Length > 2 ? new[] { args[2] } : Array.Empty<string>();
    var target = WindowsAppManager.ListVisibleWindows().FirstOrDefault(w => w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
        Console.WriteLine($"no window containing \"{titlePart}\"");
        Environment.ExitCode = 1;
        return;
    }
    var timer = Stopwatch.StartNew();
    var pageText = await PageTextReader.ReadAsync(target.Handle, TimeSpan.FromSeconds(3));
    var hits = PageTextReader.ExtractExcerpts(pageText, search);
    Console.WriteLine($"{timer.ElapsedMilliseconds}ms chars={pageText.Length} matches={hits.Count} :: {target.Title}");
    foreach (var hit in hits.Take(2)) Console.WriteLine("  " + (hit.Length > 300 ? hit[..300] : hit));
    return;
}

if (args.Contains("--controls-timing", StringComparer.OrdinalIgnoreCase))
{
    // How long a full read takes with no time limit, for tuning that limit.
    foreach (var window in WindowsAppManager.ListVisibleWindows().Take(3))
    {
        var unlimited = Stopwatch.StartNew();
        var read = await UiControlGrounding.SnapshotAsync(window.Handle, TimeSpan.FromSeconds(30));
        var tabs = read.Where(c => c.Role == "tab-item").Select(c => c.Name).ToList();
        var askedTab = args.Length > 1 ? UiControlGrounding.Match($"the {args[1]} tab at the top of the browser", read) : null;
        Console.WriteLine($"{unlimited.ElapsedMilliseconds,6}ms controls={read.Count,3} tabs={tabs.Count} match={askedTab?.Role}:{askedTab?.Name} :: {window.Title}");
        foreach (var tab in tabs.Take(8)) Console.WriteLine("    tab: " + tab);
    }
    return;
}

if (args.Contains("--controls-live", StringComparer.OrdinalIgnoreCase))
{
    // Reads the real accessibility tree of each visible window and resolves
    // one of its own control names back to that control.
    var windows = WindowsAppManager.ListVisibleWindows().Take(6).ToList();
    var anyResolved = false;
    foreach (var window in windows)
    {
        var sw = Stopwatch.StartNew();
        var found = await UiControlGrounding.SnapshotAsync(window.Handle, TimeSpan.FromMilliseconds(700));
        sw.Stop();
        var sample = found.FirstOrDefault(c => c.Enabled && !c.Offscreen);
        var hit = sample is null ? null : UiControlGrounding.Match($"the {sample.Name}", found);
        var resolved = sample is not null && hit is not null && hit.Name == sample.Name;
        anyResolved |= resolved;
        Console.WriteLine($"{sw.ElapsedMilliseconds,5}ms controls={found.Count,3} sample=\"{sample?.Name}\" role={sample?.Role} resolved={resolved} :: {window.Title}");
    }
    Environment.ExitCode = anyResolved ? 0 : 1;
    return;
}

if (args.Contains("--reasoning-self-test", StringComparer.OrdinalIgnoreCase))
{
    // Mock OpenAI-compatible server that behaves like the hidden-reasoning
    // models seen in the logs: thinking consumes the token budget and leaves
    // `content` null unless the request disables thinking or allows more room.
    var port = 18000 + Random.Shared.Next(1000);
    var prefix = $"http://127.0.0.1:{port}/";
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add(prefix);
    listener.Start();
    var seen = new List<(string Model, bool Flag, int MaxTokens, bool Effort)>();
    // Gemini's usage for an answer that thought first: thinking is only in total_tokens.
    const string hiddenAnswer = "{\"choices\":[{\"message\":{\"content\":\"<answer>```json\\n{\\\"action\\\":\\\"wait\\\"}\\n```</answer>\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":30,\"total_tokens\":330}}";
    const string hiddenCutOff = "{\"choices\":[{\"message\":{\"content\":\"<answer>\\n```json\\n{\\n  \\\"action\\\": \\\"run_powershell\\\",\\n  \\\"command\\\": \\\"$areas\"},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":40,\"total_tokens\":2148}}";
    var serverTask = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { break; }
            string raw;
            using (var reader = new StreamReader(ctx.Request.InputStream)) raw = await reader.ReadToEndAsync();
            var req = JsonNode.Parse(raw)!.AsObject();
            var model = req["model"]!.GetValue<string>();
            var flag = req["chat_template_kwargs"] is JsonObject k && k["enable_thinking"]?.GetValue<bool>() == false;
            var max = req["max_tokens"]!.GetValue<int>();
            var effort = req["reasoning_effort"]?.GetValue<string>() == "low";
            lock (seen) seen.Add((model, flag, max, effort));

            string reply;
            var status = 200;
            if (model == "hidden")
                reply = effort ? hiddenAnswer : hiddenCutOff;
            else if (model == "hidden-strict" && req["reasoning_effort"] is not null)
            {
                status = 400;
                reply = "[{\"error\":{\"code\":400,\"message\":\"Invalid JSON payload received. Unknown name \\\"reasoning_effort\\\": Cannot find field.\",\"status\":\"INVALID_ARGUMENT\"}}]";
            }
            else if (model == "hidden-strict")
                reply = max >= 4096 ? hiddenAnswer : hiddenCutOff;
            else if (model == "plain")
                reply = "{\"choices\":[{\"message\":{\"content\":\"1 2\"},\"finish_reason\":\"stop\"}]}";
            else if (model == "strict" && flag)
            {
                status = 400;
                reply = "{\"message\":\"extra fields not permitted: chat_template_kwargs\"}";
            }
            else if ((model == "reasoner" && flag) || (model == "strict" && max >= 1024))
                reply = "{\"choices\":[{\"message\":{\"content\":\"412 233\"},\"finish_reason\":\"stop\"}]}";
            else
                reply = "{\"choices\":[{\"message\":{\"content\":null,\"reasoning_content\":\"hmm let me think\"},\"finish_reason\":\"length\"}]}";

            var bytes = Encoding.UTF8.GetBytes(reply);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });

    async Task<(string Text, List<(string Model, bool Flag, int MaxTokens, bool Effort)> Requests, LmmResponse Response)> Call(string model, int maxTokens = 256)
    {
        lock (seen) seen.Clear();
        var engine = LMMEngine.Create(new Vantage.Models.Provider
        {
            Name = "mock", BaseUrl = prefix + "v1", ApiKey = "test", DefaultModel = model
        });
        var msg = new JsonObject { ["role"] = "user", ["content"] = "where is the button" };
        var response = await engine.GenerateAsync(
            new LmmRequest { Model = model, MaxTokens = maxTokens, Messages = new List<JsonObject> { msg } },
            CancellationToken.None);
        List<(string, bool, int, bool)> snapshot;
        lock (seen) snapshot = seen.ToList();
        return (response.Text, snapshot, response);
    }

    var r1 = await Call("reasoner");   // empty+reasoning -> learns flag, retries
    var r2 = await Call("reasoner");   // now sends the flag up front
    var s1 = await Call("strict");     // flag rejected -> falls back to a larger budget
    var s2 = await Call("strict");     // remembers the rejection
    var p1 = await Call("plain");      // non-reasoning model is untouched
    // Gemini-style: thinking shows only in total_tokens and cut the action off.
    var h1 = await Call("hidden", 2048);        // cut off -> learns, retries with room and low effort
    var h2 = await Call("hidden", 2048);        // sends room and low effort up front
    var hs1 = await Call("hidden-strict", 2048); // effort rejected -> room alone
    var hs2 = await Call("hidden-strict", 2048); // remembers the rejection
    listener.Stop();

    var room = 2048 + OpenAICompatEngine.HiddenThinkingAllowance;
    var ok =
        r1.Text == "412 233" && r1.Requests.Count == 2 && !r1.Requests[0].Flag && r1.Requests[1].Flag
        && r2.Text == "412 233" && r2.Requests.Count == 1 && r2.Requests[0].Flag
        && s1.Text == "412 233" && s1.Requests.Count == 3 && s1.Requests[^1].MaxTokens >= 1024 && !s1.Requests[^1].Flag
        && s2.Text == "412 233" && s2.Requests.Count == 2 && s2.Requests.All(x => !x.Flag)
        && p1.Text == "1 2" && p1.Requests.Count == 1 && !p1.Requests[0].Flag && !p1.Requests[0].Effort
        && r1.Requests.Concat(s1.Requests).All(x => !x.Effort);
    var hiddenOk =
        h1.Text.Contains("\"wait\"") && !h1.Response.CutOff && h1.Requests.Count == 2
        && h1.Requests[0] is { MaxTokens: 2048, Effort: false } && h1.Requests[1].MaxTokens == room && h1.Requests[1].Effort
        && h2.Text.Contains("\"wait\"") && h2.Requests.Count == 1 && h2.Requests[0].MaxTokens == room && h2.Requests[0].Effort
        && hs1.Text.Contains("\"wait\"") && hs1.Requests.Count == 3 && hs1.Requests[1].Effort
        && hs1.Requests[2] is { Effort: false } && hs1.Requests[2].MaxTokens == room
        && hs2.Text.Contains("\"wait\"") && hs2.Requests.Count == 1 && hs2.Requests[0] is { Effort: false } && hs2.Requests[0].MaxTokens == room;

    // Usage numbers: Gemini's from the 1.6.19 log, OpenAI's o-series, and a model that does not think.
    int Thinking(string usage) => OpenAICompatEngine.HiddenThinkingTokens(JsonDocument.Parse(usage).RootElement);
    var usageOk =
        Thinking("{\"completion_tokens\":0,\"prompt_tokens\":8,\"total_tokens\":13}") == 5
        && Thinking("{\"prompt_tokens\":10,\"completion_tokens\":500,\"total_tokens\":510,\"completion_tokens_details\":{\"reasoning_tokens\":448}}") == 448
        && Thinking("{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}") == 0
        && Thinking("{\"prompt_tokens\":10,\"completion_tokens\":5}") == 0
        && Thinking("{\"prompt_tokens\":null,\"completion_tokens\":5,\"total_tokens\":null}") == 0;

    Console.WriteLine($"ReasoningModelHandlingValid={ok} learn={r1.Requests.Count} warm={r2.Requests.Count} strict={s1.Requests.Count}/{s2.Requests.Count} plain={p1.Requests.Count}");
    Console.WriteLine($"HiddenThinkingHandlingValid={hiddenOk} learn={h1.Requests.Count} warm={h2.Requests.Count} strict={hs1.Requests.Count}/{hs2.Requests.Count}");
    Console.WriteLine($"HiddenThinkingUsageValid={usageOk}");
    Environment.ExitCode = ok && hiddenOk && usageOk ? 0 : 1;
    return;
}

if (args.Contains("--computer-use-prompt-self-test", StringComparer.OrdinalIgnoreCase))
{
    var prompt = PROCEDURAL_MEMORY.ConstructSimpleWorkerProceduralMemory(
        "windows", 1920, 1080, Array.Empty<string>());
    var required = new[]
    {
        "## batch", "small deterministic UI steps", "## list_windows",
        "## get_window_state", "Never call get_window_state repeatedly", "## click_element",
        "## type_window_text", "## press_window_key", "## run_powershell",
        "## click_xy", "## press_key", "Windows-key shortcuts are available",
    };
    var forbidden = new[]
    {
        "safety gate", "requires an action-time approval",
        "physical user input interrupts the run",
    };
    var requiredValid = required.All(value => prompt.Contains(value, StringComparison.Ordinal));
    var forbiddenValid = forbidden.All(value => !prompt.Contains(value, StringComparison.OrdinalIgnoreCase));
    var valid = requiredValid && forbiddenValid;
    Console.WriteLine($"ComputerUsePromptValid={valid} RequiredTools={requiredValid} ForbiddenToolsAbsent={forbiddenValid}");
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

WorldStateProbe.PrimeSessionCache();
var probe = WorldStateProbe.Capture();

Console.WriteLine("--- WorldStateProbe.ToPromptBlock() ---");
Console.WriteLine(probe.ToPromptBlock());
Console.WriteLine();
Console.WriteLine("--- Raw record (selected fields) ---");
Console.WriteLine($"Foreground: {probe.ForegroundProcess} / pid={probe.ForegroundPid} / hwnd=0x{probe.ForegroundHwnd:X}");
Console.WriteLine($"ForegroundTitle: {probe.ForegroundTitle}");
Console.WriteLine($"Monitors: {probe.DisplayCount} ({probe.DisplaySummary})");
Console.WriteLine($"Cursor: ({probe.CursorX}, {probe.CursorY})");
Console.WriteLine($"Time: {probe.LocalTimeIso} {probe.TimeZoneId}");
Console.WriteLine($"Keyboard: layout={probe.KeyboardLayout} caps={probe.CapsLockOn} num={probe.NumLockOn} scroll={probe.ScrollLockOn}");
Console.WriteLine($"Installed apps: {probe.InstalledAppCount}");
foreach (var a in probe.InstalledApps.Take(15))
{
    Console.WriteLine($"  - {a.Name}  ->  {a.ExecutablePath ?? "<no .lnk target>"}");
}
Console.WriteLine($"Running apps: {probe.RunningApps.Count}");
foreach (var a in probe.RunningApps)
{
    Console.WriteLine($"  - {a.ProcessName}  pid={a.Pid}  fg={a.IsForeground}  haswin={a.HasVisibleWindow}  started={a.StartedAt:yyyy-MM-ddTHH:mm:ss}  path={a.ExecutablePath}");
}
Console.WriteLine($"Recent apps: [{string.Join(", ", probe.RecentApps)}]");
Console.WriteLine($"Capture wall time: {probe.ElapsedMs} ms");

// Debug: print the raw full path QueryFullProcessImageNameW returns
// for the foreground process so we can see whether the path is being
// truncated to "C" by the API, by the StringBuilder, or by our code.
Console.WriteLine();
Console.WriteLine("--- Debug: raw exe path for foreground process ---");
try
{
    var pid = (uint)probe.ForegroundPid;
    var hProc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (hProc != IntPtr.Zero)
    {
        try
        {
            // Compare: pass StringBuilder (the way existing code does)
            // vs. a raw IntPtr + Marshal.PtrToStringUni.
            var sb = new StringBuilder(1024);
            var sbSize = (uint)sb.Capacity;
            var ok1 = Native.QueryFullProcessImageNameW(hProc, 0, sb, ref sbSize);
            Console.WriteLine($"  [StringBuilder path] returned={ok1}, sb.Length={sb.Length}, size-out={sbSize}, text=\"{sb}\"");

            var ptr = Marshal.AllocHGlobal(1024);
            try
            {
                var ptrSize = (uint)1024;
                var ok2 = Native.QueryFullProcessImageNameWPtr(hProc, 0, ptr, ref ptrSize);
                var strFromPtr = Marshal.PtrToStringUni(ptr, (int)ptrSize);
                Console.WriteLine($"  [IntPtr path]      returned={ok2}, size-out={ptrSize}, text=\"{strFromPtr}\"");
            }
            finally { Marshal.FreeHGlobal(ptr); }

            // Also try via Process.MainModule.FileName (managed API)
            try
            {
                using var p = Process.GetProcessById((int)pid);
                Console.WriteLine($"  [Process API]      FileName=\"{p.MainModule?.FileName}\" Name=\"{p.ProcessName}\"");
            }
            catch (Exception ex) { Console.WriteLine($"  Process API failed: {ex.Message}"); }
        }
        finally { Native.CloseHandle(hProc); }
    }
    else
    {
        Console.WriteLine($"  OpenProcess failed: {Marshal.GetLastWin32Error()}");
    }
}
catch (Exception ex) { Console.WriteLine($"  Debug failed: {ex.Message}"); }

internal static class Native
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint flags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameWPtr(IntPtr hProcess, uint flags, IntPtr lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);
}
