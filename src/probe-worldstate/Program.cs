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
        persistentValid = initialBlock.Contains("Planning required", StringComparison.Ordinal)
            && initialBlock.Contains("Include task_update in each action", StringComparison.Ordinal);
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

    var valid = windowValid && persistentValid;
    Console.WriteLine($"ContextWindowValid={windowValid} PersistentTaskContextValid={persistentValid} RetainedMessages={messages.Count} RetainedImages={retainedImages}");
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

    var valid = verifyOk && groundOk && blockOk && todoOk;
    Console.WriteLine($"AgentAccuracyValid={valid} VerifiedGuessedTargets={verifyOk} ControlMatching={groundOk} ControlPromptBlock={blockOk} TodoByText={todoOk}");
    Environment.ExitCode = valid ? 0 : 1;
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
