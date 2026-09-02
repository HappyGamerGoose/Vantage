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

    var compacted = ContextWindowCompactor.Compact(messages, keepRecentNonSystemMessages: 6, keepRecentImages: 2);
    var retainedImages = messages
        .SelectMany(message => (message["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        .Count(block => block["type"]?.GetValue<string>() is "image" or "image_url");
    var windowValid = compacted.RemovedMessages == 2
        && compacted.RemovedImages == 1
        && messages.Count == 7
        && retainedImages == 2;

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
