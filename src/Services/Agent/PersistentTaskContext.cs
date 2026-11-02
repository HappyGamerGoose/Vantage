// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vantage.Services.Agent;

/// <summary>
/// Durable, text-only task memory for one conversation. It is deliberately
/// stored outside the multimodal prompt history so a long run can discard old
/// screenshots without forgetting the user's goal or unfinished work.
/// </summary>
public sealed class PersistentTaskContext
{
    private const int MaxGoalLength = 1_200;
    private const int MaxTodoItems = 12;
    private const int MaxTodoLength = 220;
    private const int MaxSummaryLength = 360;
    private static readonly Regex ExplicitStepBoundary = new(
        @"(?:\s*,?\s+\bthen\b\s+)|(?:\r?\n\s*(?:[-*]|\d+[.)])\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxFinishedWork = 30;

    private readonly string _filePath;
    private readonly string _scopeKey;
    private TaskContextSnapshot _snapshot;

    public PersistentTaskContext(string scopeKey, string? rootDirectory = null)
    {
        _scopeKey = string.IsNullOrWhiteSpace(scopeKey) ? "default" : scopeKey.Trim();
        var root = ResolveRoot(rootDirectory);
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, SafeFileName(_scopeKey) + ".json");
        _snapshot = Load();
    }

    public void BeginTask(string goal)
    {
        var normalizedGoal = Normalize(goal, MaxGoalLength);
        if (string.IsNullOrWhiteSpace(normalizedGoal)) normalizedGoal = "Complete the requested task.";

        var shouldResume = string.Equals(_snapshot.Goal, normalizedGoal, StringComparison.Ordinal)
            && _snapshot.Todos.Any(item => !item.IsCompleted);
        if (!shouldResume)
        {
            var todos = new List<TaskTodoItem>
            {
                new() { Id = "goal", Text = "Complete the requested goal.", IsCompleted = false }
            };
            var initialSteps = BuildInitialTodos(goal);
            for (var index = 0; index < initialSteps.Count; index++)
            {
                todos.Add(new TaskTodoItem
                {
                    Id = $"todo-{index + 1}",
                    Text = initialSteps[index],
                    IsCompleted = false
                });
            }

            _snapshot = new TaskContextSnapshot
            {
                ScopeKey = _scopeKey,
                Goal = normalizedGoal,
                LastActionSummary = "No action has been taken yet.",
                Todos = todos
            };
        }
        else
        {
            // Files may have been changed or deleted since the last attempt, and a
            // stale "finished" entry would make the agent skip them. Redoing a save
            // costs far less than a run that reports files that are not there.
            _snapshot.FinishedWork.Clear();
        }

        _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public string BuildPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PERSISTENT TASK STATE");
        sb.AppendLine($"Goal: {Fallback(_snapshot.Goal, "Complete the requested task.")}");
        sb.AppendLine($"Last action: {Fallback(_snapshot.LastActionSummary, "No action has been taken yet.")}");
        
        if (_snapshot.DiscoveredEntities.Count > 0)
        {
            sb.AppendLine("Discovered entities & facts:");
            foreach (var entity in _snapshot.DiscoveredEntities.TakeLast(8))
            {
                sb.Append("  * ").AppendLine(entity);
            }
        }

        if (_snapshot.FinishedWork.Count > 0)
        {
            sb.AppendLine("Finished work (confirmed; do not do it again):");
            foreach (var finished in _snapshot.FinishedWork)
            {
                sb.Append("  [✓] ").AppendLine(finished);
            }
        }

        if (_snapshot.Milestones.Count > 0)
        {
            sb.AppendLine("Accomplished milestones (do not repeat):");
            foreach (var milestone in _snapshot.Milestones.TakeLast(6))
            {
                sb.Append("  [✓] ").AppendLine(milestone);
            }
        }

        if (_snapshot.FailedAttempts.Count > 0)
        {
            sb.AppendLine("Pitfalls to avoid (previous failed actions):");
            foreach (var attempt in _snapshot.FailedAttempts.TakeLast(5))
            {
                sb.Append("  [!] ").AppendLine(attempt);
            }
        }

        sb.AppendLine("To-do list:");
        foreach (var item in _snapshot.Todos.Take(MaxTodoItems))
        {
            sb.Append("- [").Append(item.IsCompleted ? 'x' : ' ').Append("] ")
                .Append('(').Append(item.Id).Append(") ").AppendLine(item.Text);
        }
        sb.AppendLine("For a task with several steps you may include task_update as {\"add\":[],\"complete\":[],\"reopen\":[]}. Add takes new step text; complete and reopen take a to-do ID (such as todo-2) or its exact text.");
        return sb.ToString().TrimEnd();
    }

    public void RecordDiscoveredEntity(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity)) return;
        var normalized = Normalize(entity, 240);
        if (!_snapshot.DiscoveredEntities.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _snapshot.DiscoveredEntities.Add(normalized);
            while (_snapshot.DiscoveredEntities.Count > 30) _snapshot.DiscoveredEntities.RemoveAt(0);
            _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public void RecordDiscoveredEntities(IEnumerable<string> entities)
    {
        var changed = false;
        foreach (var entity in entities)
        {
            if (string.IsNullOrWhiteSpace(entity)) continue;
            var normalized = Normalize(entity, 240);
            if (!_snapshot.DiscoveredEntities.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                _snapshot.DiscoveredEntities.Add(normalized);
                changed = true;
            }
        }
        while (_snapshot.DiscoveredEntities.Count > 30)
        {
            _snapshot.DiscoveredEntities.RemoveAt(0);
            changed = true;
        }
        if (changed)
        {
            _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public void RecordMilestone(string milestone)
    {
        if (string.IsNullOrWhiteSpace(milestone)) return;
        var normalized = Normalize(milestone, 240);
        if (!_snapshot.Milestones.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _snapshot.Milestones.Add(normalized);
            while (_snapshot.Milestones.Count > 20) _snapshot.Milestones.RemoveAt(0);
            _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    /// <summary>
    /// Real progress, kept for the whole task. Ordinary milestones roll off, and a
    /// run that lost "saved Canada.txt" among its clicks went back and redid it.
    /// </summary>
    public void RecordFinishedWork(string finished)
    {
        if (string.IsNullOrWhiteSpace(finished)) return;
        var normalized = Normalize(finished, 240);
        if (_snapshot.FinishedWork.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
        _snapshot.FinishedWork.Add(normalized);
        while (_snapshot.FinishedWork.Count > MaxFinishedWork) _snapshot.FinishedWork.RemoveAt(0);
        _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public void RecordFailedAttempt(string description, string reason)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        var normalized = Normalize($"{description} -> {reason}", 240);
        if (!_snapshot.FailedAttempts.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _snapshot.FailedAttempts.Add(normalized);
            while (_snapshot.FailedAttempts.Count > 15) _snapshot.FailedAttempts.RemoveAt(0);
            _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public void SetCompactedMemorySummary(string summary)
    {
        _snapshot.CompactedMemorySummary = summary ?? string.Empty;
        _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public string Goal => _snapshot.Goal;
    public IReadOnlyList<TaskTodoItem> Todos => _snapshot.Todos;
    public IReadOnlyList<string> DiscoveredEntities => _snapshot.DiscoveredEntities;
    public IReadOnlyList<string> Milestones => _snapshot.Milestones;
    public IReadOnlyList<string> FinishedWork => _snapshot.FinishedWork;
    public IReadOnlyList<string> FailedAttempts => _snapshot.FailedAttempts;
    public string CompactedMemorySummary => _snapshot.CompactedMemorySummary;

    public void RecordAction(string actionName, JsonElement rawAction, ActionResult result)
    {
        var succeeded = result.Outcome is ActionOutcome.Success or ActionOutcome.Done;
        ApplyTaskUpdate(rawAction, succeeded);

        if (result.Outcome == ActionOutcome.Done)
        {
            foreach (var item in _snapshot.Todos) item.IsCompleted = true;
        }

        var action = DescribeAction(actionName, rawAction);
        var outcome = result.Outcome switch
        {
            ActionOutcome.Success => "succeeded",
            ActionOutcome.Done => "completed the task",
            ActionOutcome.Failed => "failed",
            ActionOutcome.FailedFatal => "stopped",
            _ => "finished"
        };
        _snapshot.LastActionSummary = Normalize($"{action} {outcome}: {result.Description}", MaxSummaryLength);
        if (!_snapshot.LastActionSummary.EndsWith(".", StringComparison.Ordinal))
            _snapshot.LastActionSummary += ".";
        _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public void RecordSystemEvent(string sentence)
    {
        _snapshot.LastActionSummary = Normalize(sentence, MaxSummaryLength);
        if (!_snapshot.LastActionSummary.EndsWith(".", StringComparison.Ordinal))
            _snapshot.LastActionSummary += ".";
        _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public static void Delete(string scopeKey, string? rootDirectory = null)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(scopeKey) ? "default" : scopeKey.Trim();
        var filePath = Path.Combine(ResolveRoot(rootDirectory), SafeFileName(normalizedKey) + ".json");
        TryDelete(filePath);
        TryDelete(filePath + ".tmp");
    }

    public static void DeleteAll(string? rootDirectory = null)
    {
        var root = ResolveRoot(rootDirectory);
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(root, "*.json")) TryDelete(filePath);
            foreach (var filePath in Directory.EnumerateFiles(root, "*.json.tmp")) TryDelete(filePath);
        }
        catch
        {
            // Chat deletion must still complete if task-state cleanup fails.
        }
    }

    private void ApplyTaskUpdate(JsonElement rawAction, bool allowCompletion)
    {
        if (!rawAction.TryGetProperty("task_update", out var update)
            || update.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var text in ReadStrings(update, "add")) AddTodo(text);
        if (allowCompletion)
        {
            foreach (var id in ReadStrings(update, "complete")) SetCompletion(id, true);
        }
        foreach (var id in ReadStrings(update, "reopen")) SetCompletion(id, false);
    }

    private void AddTodo(string text)
    {
        var normalized = Normalize(text, MaxTodoLength);
        if (string.IsNullOrWhiteSpace(normalized) || _snapshot.Todos.Count >= MaxTodoItems) return;
        if (_snapshot.Todos.Any(item => string.Equals(item.Text, normalized, StringComparison.OrdinalIgnoreCase))) return;

        var next = 1;
        while (_snapshot.Todos.Any(item => item.Id.Equals($"todo-{next}", StringComparison.OrdinalIgnoreCase))) next++;
        _snapshot.Todos.Add(new TaskTodoItem { Id = $"todo-{next}", Text = normalized });
    }

    private void SetCompletion(string id, bool complete)
    {
        // The root goal represents the whole run and is completed only by
        // the terminal `done` result in RecordAction.
        if (complete && id.Trim().Equals("goal", StringComparison.OrdinalIgnoreCase)) return;
        var todo = ResolveTodo(id);
        if (todo is not null) todo.IsCompleted = complete;
    }

    /// <summary>
    /// Models usually refer to a step by its wording ("Open Windows Settings")
    /// rather than its id ("todo-1"). Accept an id, the exact text, or text
    /// that clearly contains / is contained by exactly one step.
    /// </summary>
    internal TaskTodoItem? ResolveTodo(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) return null;

        var byId = _snapshot.Todos.FirstOrDefault(item => item.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var wanted = NormalizeKey(trimmed);
        if (wanted.Length == 0) return null;

        var exact = _snapshot.Todos.Where(item => NormalizeKey(item.Text) == wanted).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return exact.FirstOrDefault(item => !item.IsCompleted) ?? exact[0];

        // Containment needs enough text to be meaningful; "open" must not
        // complete every step that mentions opening something.
        if (wanted.Length < 8) return null;
        var partial = _snapshot.Todos.Where(item =>
        {
            var text = NormalizeKey(item.Text);
            return text.Length >= 8 && (text.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(text, StringComparison.Ordinal));
        }).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    private static string NormalizeKey(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }

    private TaskContextSnapshot Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new TaskContextSnapshot { ScopeKey = _scopeKey };
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<TaskContextSnapshot>(json) ?? new TaskContextSnapshot();
            loaded.ScopeKey = _scopeKey;
            return loaded;
        }
        catch
        {
            return new TaskContextSnapshot { ScopeKey = _scopeKey };
        }
    }

    private void Save()
    {
        try
        {
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // Durable task state is helpful, but must never stop desktop work.
        }
    }

    private static IEnumerable<string> ReadStrings(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static string DescribeAction(string actionName, JsonElement raw)
    {
        if (raw.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
            return Normalize(description.GetString() ?? actionName, 160);
        if (raw.TryGetProperty("combo", out var combo) && combo.ValueKind == JsonValueKind.String)
            return $"Pressed {combo.GetString()}";
        if (raw.TryGetProperty("executable", out var executable) && executable.ValueKind == JsonValueKind.String)
            return $"Launched {executable.GetString()}";
        return $"Ran {actionName}";
    }

    private static List<string> BuildInitialTodos(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return new List<string>();
        var parts = ExplicitStepBoundary
            .Split(goal)
            .Select(part => Normalize(part.Trim(' ', ',', ';', '.', ':'), MaxTodoLength))
            .Where(part => part.Length >= 8)
            .Take(6)
            .ToList();
        return parts.Count >= 2 ? parts : new List<string>();
    }

    private static string Normalize(string? value, int maxLength)
    {
        var collapsed = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }

    private static string SafeFileName(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static string ResolveRoot(string? rootDirectory) => rootDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vantage",
        "agent-context");

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
        catch
        {
            // Persistent memory is best-effort and must not block the UI.
        }
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed class TaskContextSnapshot
    {
        public string ScopeKey { get; set; } = "";
        public string Goal { get; set; } = "";
        public string LastActionSummary { get; set; } = "";
        public List<TaskTodoItem> Todos { get; set; } = new();
        public List<string> DiscoveredEntities { get; set; } = new();
        public List<string> Milestones { get; set; } = new();
        public List<string> FinishedWork { get; set; } = new();
        public List<string> FailedAttempts { get; set; } = new();
        public string CompactedMemorySummary { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class TaskTodoItem
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsCompleted { get; set; }
    }
}

/// <summary>What a window title shows about a file having just been saved.</summary>
internal static class SavedFileTitle
{
    private static readonly Regex FileTitle = new(@"^(?<unsaved>\*?)(?<name>[^\\/:*?""<>|]+\.\w{1,8})\s+-\s+", RegexOptions.Compiled);
    private static readonly Regex BareFileName = new(@"^[^\\/:*?""<>|]+\.\w{1,8}$", RegexOptions.Compiled);

    /// <summary>
    /// The file's name when the title went from a save dialog, or from that file
    /// with unsaved changes, to the file with none. Anything else is null, so
    /// switching to another window that holds a file does not count.
    /// </summary>
    internal static string? Detect(string? before, string? after)
    {
        if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)) return null;
        var now = FileTitle.Match(after.Trim());
        if (!now.Success || now.Groups["unsaved"].Length > 0) return null;
        var name = now.Groups["name"].Value;

        var previous = before.Trim();
        var fromDialog = IsSaveDialog(previous);
        var earlier = FileTitle.Match(previous);
        var fromUnsaved = earlier.Success && earlier.Groups["unsaved"].Length > 0
            && earlier.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase);
        return fromDialog || fromUnsaved ? name : null;
    }

    private static bool IsSaveDialog(string? title) =>
        title is not null
        && title.TrimStart().StartsWith("Save", StringComparison.OrdinalIgnoreCase)
        && !title.Contains(" - ", StringComparison.Ordinal);

    /// <summary>The text of a typing action that gives a save dialog a file name with no folder.</summary>
    internal static string? BareNameInSaveDialog(AgentAction action, string? title)
    {
        if (!IsSaveDialog(title)) return null;
        if (action.Action.Trim().ToLowerInvariant() is not ("type" or "type_text")) return null;
        var text = action.GetString("text")?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains('\\') || text.Contains('/')) return null;
        return BareFileName.IsMatch(text) ? text : null;
    }

    /// <summary>The file's name when the title shows a file with no unsaved changes.</summary>
    internal static string? SavedName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var match = FileTitle.Match(title.Trim());
        return match.Success && match.Groups["unsaved"].Length == 0 ? match.Groups["name"].Value : null;
    }

    /// <summary>
    /// The finished file a typing action would put new text into. Notepad stays
    /// on a file after saving it, so the next country's figures went into
    /// Canada.txt and were saved over it. Null when the text goes somewhere else.
    /// </summary>
    internal static string? FinishedFileTypedInto(AgentAction action, string? title, IEnumerable<string> finishedWork)
    {
        if (action.Action.Trim().ToLowerInvariant() is not ("type" or "type_text")) return null;
        if (action.GetInt("control") is not null || action.GetPoint() is not null) return null;
        if (string.IsNullOrWhiteSpace(action.GetString("text"))) return null;
        var name = SavedName(title);
        return name is not null && finishedWork.Any(done => done.Contains(name, StringComparison.OrdinalIgnoreCase))
            ? name
            : null;
    }
}
