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

    var valid = windowValid && persistentValid && factsClean && historyHonest;
    Console.WriteLine($"ContextWindowValid={windowValid} PersistentTaskContextValid={persistentValid} FactsWithoutNoise={factsClean} HistoryHonest={historyHonest} RetainedMessages={messages.Count} RetainedImages={retainedImages}");
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

    var valid = oneModel && upgraded && idempotent && roundTrip && pinnedOk;
    Console.WriteLine($"ProviderModelsValid={valid} OneModel={oneModel} Idempotent={idempotent} RoundTrip={roundTrip} Pinned={pinnedOk}");
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
    var problemOk = CommonUtils.DescribeActionProblem("I would click it.").Contains("no JSON", StringComparison.Ordinal)
        && CommonUtils.DescribeActionProblem("```json\n{\"thing\":1}\n```").Contains("no `action`", StringComparison.Ordinal)
        && CommonUtils.DescribeActionProblem("```json\n{\"action\": \n```").Contains("invalid", StringComparison.Ordinal);
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
    var seen = new List<(string Model, bool Flag, int MaxTokens)>();
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
            lock (seen) seen.Add((model, flag, max));

            string reply;
            var status = 200;
            if (model == "plain")
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

    async Task<(string Text, List<(string Model, bool Flag, int MaxTokens)> Requests)> Call(string model)
    {
        lock (seen) seen.Clear();
        var engine = LMMEngine.Create(new Vantage.Models.Provider
        {
            Name = "mock", BaseUrl = prefix + "v1", ApiKey = "test", DefaultModel = model
        });
        var msg = new JsonObject { ["role"] = "user", ["content"] = "where is the button" };
        var response = await engine.GenerateAsync(
            new LmmRequest { Model = model, MaxTokens = 256, Messages = new List<JsonObject> { msg } },
            CancellationToken.None);
        List<(string, bool, int)> snapshot;
        lock (seen) snapshot = seen.ToList();
        return (response.Text, snapshot);
    }

    var r1 = await Call("reasoner");   // empty+reasoning -> learns flag, retries
    var r2 = await Call("reasoner");   // now sends the flag up front
    var s1 = await Call("strict");     // flag rejected -> falls back to a larger budget
    var s2 = await Call("strict");     // remembers the rejection
    var p1 = await Call("plain");      // non-reasoning model is untouched
    listener.Stop();

    var ok =
        r1.Text == "412 233" && r1.Requests.Count == 2 && !r1.Requests[0].Flag && r1.Requests[1].Flag
        && r2.Text == "412 233" && r2.Requests.Count == 1 && r2.Requests[0].Flag
        && s1.Text == "412 233" && s1.Requests.Count == 3 && s1.Requests[^1].MaxTokens >= 1024 && !s1.Requests[^1].Flag
        && s2.Text == "412 233" && s2.Requests.Count == 2 && s2.Requests.All(x => !x.Flag)
        && p1.Text == "1 2" && p1.Requests.Count == 1 && !p1.Requests[0].Flag;
    Console.WriteLine($"ReasoningModelHandlingValid={ok} learn={r1.Requests.Count} warm={r2.Requests.Count} strict={s1.Requests.Count}/{s2.Requests.Count} plain={p1.Requests.Count}");
    Environment.ExitCode = ok ? 0 : 1;
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
