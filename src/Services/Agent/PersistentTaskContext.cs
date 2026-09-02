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

        _snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public string BuildPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PERSISTENT TASK STATE");
        sb.AppendLine($"Goal: {Fallback(_snapshot.Goal, "Complete the requested task.")}");
        sb.AppendLine($"Last action: {Fallback(_snapshot.LastActionSummary, "No action has been taken yet.")}");
        sb.AppendLine("To-do list:");
        foreach (var item in _snapshot.Todos.Take(MaxTodoItems))
        {
            sb.Append("- [").Append(item.IsCompleted ? 'x' : ' ').Append("] ")
                .Append('(').Append(item.Id).Append(") ").AppendLine(item.Text);
        }
        if (_snapshot.Todos.Count == 1 && !_snapshot.Todos[0].IsCompleted)
            sb.AppendLine("Planning required: add 1-5 concrete remaining steps with task_update.add in your next action.");
        sb.AppendLine("Include task_update in each action as {\"add\":[],\"complete\":[],\"reopen\":[]}. Add contains new step text; complete and reopen contain existing to-do IDs. Do not complete goal directly or mark any step complete until its action succeeded.");
        return sb.ToString().TrimEnd();
    }

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
        var todo = _snapshot.Todos.FirstOrDefault(item => item.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (todo is not null) todo.IsCompleted = complete;
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
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class TaskTodoItem
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsCompleted { get; set; }
    }
}
