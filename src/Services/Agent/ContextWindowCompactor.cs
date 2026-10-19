// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ContextWindowCompactor.cs
//
// High-fidelity context window compaction system. When an agent run approaches
// context limits or encounters provider token boundaries, this system performs
// a structured, practically-lossless distillation: preserving original user directives,
// extracted entities/facts, completed milestones, negative constraints, and the
// current execution state, while pruning visual payloads and redundant turns.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vantage.Services.Agent;

public static class ContextWindowCompactor
{
    private static readonly Regex FilePathRegex = new(
        @"(?:[a-zA-Z]:\\(?:[^\\/:*?""<>|\r\n\t ]+\\)*[^\\/:*?""<>|\r\n\t ]+\.[a-zA-Z0-9]{1,8})|(?:\b[a-zA-Z]:\\[A-Za-z0-9_\-\.\\]+\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UrlRegex = new(
        @"https?://[A-Za-z0-9_\-\.~:/?#\[\]@!$&'()*+,;%=]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ErrorCodeRegex = new(
        @"\b(?:0x[0-9a-fA-F]{4,10}|HRESULT\s*[:=]?\s*[0-9a-fA-F]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProcessAppRegex = new(
        @"\b(?:[a-zA-Z0-9_\-\.]+\.exe|PID[:\s]+(\d+))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ConstraintKeywords =
    {
        "do not", "don't", "avoid", "never", "only", "must", "ensure",
        "save to", "format", "keep", "columns", "file", "path", "url", "directory",
        "exclude", "password", "credential", "overwrite"
    };

    public sealed record Result(
        int RemovedMessages,
        int RemovedImages,
        string Summary = "",
        int TokensBefore = 0,
        int TokensAfter = 0);

    /// <summary>
    /// Compacts the message history to fit within context window bounds without losing
    /// vital task intelligence: extracts entities, milestones, pitfalls, and constraints.
    /// </summary>
    public static Result Compact(
        List<JsonObject> messages,
        int keepRecentNonSystemMessages = 2,
        int keepRecentImages = 1,
        string? existingSummary = null,
        PersistentTaskContext? taskContext = null,
        string? activeGoal = null,
        bool isEmergency = false)
    {
        if (messages.Count == 0) return new Result(0, 0, existingSummary ?? string.Empty);

        var tokensBefore = ContextWindowLimits.EstimateTokens(messages, 0).TotalTokens;

        keepRecentNonSystemMessages = Math.Max(1, keepRecentNonSystemMessages);
        keepRecentImages = Math.Max(1, keepRecentImages);

        // Find the oldest message in the active window, anchored on a user observation.
        var firstRetainedIndex = messages.Count;
        var remainingMessages = keepRecentNonSystemMessages;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (IsSystem(message)) continue;
            if (remainingMessages-- > 0) firstRetainedIndex = index;
            else break;
        }

        if (firstRetainedIndex >= messages.Count)
        {
            return CompactImages(messages, 0, keepRecentImages, existingSummary ?? string.Empty, tokensBefore);
        }

        // Avoid leaving an orphaned assistant plan at the head of the retained window
        while (firstRetainedIndex < messages.Count && IsAssistant(messages[firstRetainedIndex]))
        {
            firstRetainedIndex++;
        }

        // Collect and distill the information from the turns being pruned
        var discoveredEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var milestones = new List<string>();
        var pitfalls = new List<string>();
        var userDirectives = new List<string>();
        var actionHistory = new List<string>();

        // Incorporate existing knowledge from persistent task context if available
        if (taskContext != null)
        {
            foreach (var entity in taskContext.DiscoveredEntities) discoveredEntities.Add(entity);
            foreach (var milestone in taskContext.Milestones) milestones.Add(milestone);
            foreach (var pitfall in taskContext.FailedAttempts) pitfalls.Add(pitfall);
        }

        var removedMessages = 0;
        var removedImages = 0;

        for (var index = 0; index < firstRetainedIndex; index++)
        {
            var msg = messages[index];
            if (IsSystem(msg)) continue;

            var role = msg["role"]?.GetValue<string>() ?? "user";
            var text = ExtractText(msg);

            // 1. Extract entities & facts (paths, URLs, error codes, processes)
            ExtractEntities(text, discoveredEntities);

            if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                // Capture user constraints, directives, and goals
                ExtractUserDirectives(text, userDirectives);
            }
            else if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                // Extract planned action and verification thoughts
                var (actionSummary, planDescription) = ExtractActionSummary(text);
                if (!string.IsNullOrWhiteSpace(planDescription))
                {
                    actionHistory.Add(planDescription);
                }

                // Check subsequent message or verifier outcome for milestone vs pitfall
                if (index + 1 < messages.Count)
                {
                    var nextText = ExtractText(messages[index + 1]);
                    if (nextText.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        nextText.Contains("STUCK_RECOVERY", StringComparison.OrdinalIgnoreCase) ||
                        nextText.Contains("GROUNDING_FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        var failureReason = ExtractFailureSnippet(nextText);
                        var pitfallDesc = !string.IsNullOrWhiteSpace(planDescription)
                            ? $"{planDescription} -> {failureReason}"
                            : $"Action failed -> {failureReason}";
                        if (!pitfalls.Contains(pitfallDesc, StringComparer.OrdinalIgnoreCase))
                            pitfalls.Add(pitfallDesc);
                    }
                    else if (!string.IsNullOrWhiteSpace(planDescription))
                    {
                        var milestoneDesc = planDescription;
                        if (!milestones.Contains(milestoneDesc, StringComparer.OrdinalIgnoreCase))
                            milestones.Add(milestoneDesc);
                    }
                }
            }
        }

        // Remove the pruned messages from messages list
        for (var index = firstRetainedIndex - 1; index >= 0; index--)
        {
            if (IsSystem(messages[index])) continue;
            removedImages += CountImages(messages[index]);
            messages.RemoveAt(index);
            removedMessages++;
        }

        // Persist newly discovered knowledge into the persistent task context
        if (taskContext != null)
        {
            taskContext.RecordDiscoveredEntities(discoveredEntities);
            foreach (var m in milestones) taskContext.RecordMilestone(m);
            foreach (var p in pitfalls)
            {
                var parts = p.Split("->", 2);
                taskContext.RecordFailedAttempt(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "failed");
            }
        }

        // Build the rich, high-fidelity compacted task memory summary
        var effectiveGoal = activeGoal ?? taskContext?.Goal ?? "Complete the requested task.";
        var summary = SynthesizeCompactedTaskMemory(
            effectiveGoal,
            userDirectives,
            discoveredEntities,
            milestones,
            pitfalls,
            actionHistory,
            taskContext);

        if (taskContext != null)
        {
            taskContext.SetCompactedMemorySummary(summary);
        }

        // Compact images in retained turns
        var imageResult = CompactImages(messages, removedMessages, keepRecentImages, summary, tokensBefore);

        var tokensAfter = ContextWindowLimits.EstimateTokens(messages, 0).TotalTokens;

        return new Result(
            imageResult.RemovedMessages,
            removedImages + imageResult.RemovedImages,
            summary,
            TokensBefore: tokensBefore,
            TokensAfter: tokensAfter);
    }

    private static Result CompactImages(
        List<JsonObject> messages,
        int removedMessages,
        int keepRecentImages,
        string summary,
        int tokensBefore)
    {
        var removedImages = 0;
        var imagesKept = 0;
        for (var messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
        {
            if (messages[messageIndex]["content"] is not JsonArray content) continue;
            var removedFromMessage = 0;

            for (var blockIndex = content.Count - 1; blockIndex >= 0; blockIndex--)
            {
                if (!IsImage(content[blockIndex])) continue;
                imagesKept++;
                if (imagesKept <= keepRecentImages) continue;

                content.RemoveAt(blockIndex);
                removedImages++;
                removedFromMessage++;
            }

            if (removedFromMessage > 0)
            {
                var placeholder = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "[Earlier desktop screenshot compacted for context window budget; latest screen and persistent state are preserved.]"
                };
                content.Add(placeholder);
            }
        }

        var tokensAfter = ContextWindowLimits.EstimateTokens(messages, 0).TotalTokens;
        return new Result(removedMessages, removedImages, summary, tokensBefore, tokensAfter);
    }

    private static void ExtractEntities(string text, HashSet<string> entities)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // File paths
        foreach (Match match in FilePathRegex.Matches(text))
        {
            var path = match.Value.Trim(' ', ',', ';', '"', '\'');
            if (path.Length > 3 && !path.Contains(".git", StringComparison.OrdinalIgnoreCase))
            {
                entities.Add(path);
            }
        }

        // URLs
        foreach (Match match in UrlRegex.Matches(text))
        {
            var url = match.Value.Trim(' ', ',', ';', '"', '\'', '>');
            if (url.Length > 8)
            {
                entities.Add(url);
            }
        }

        // Error codes
        foreach (Match match in ErrorCodeRegex.Matches(text))
        {
            var code = match.Value.Trim();
            if (code.Length > 2)
            {
                entities.Add(code);
            }
        }

        // Key processes
        foreach (Match match in ProcessAppRegex.Matches(text))
        {
            var proc = match.Value.Trim();
            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !proc.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
                !proc.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                entities.Add(proc);
            }
        }
    }

    private static void ExtractUserDirectives(string text, List<string> directives)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Skip standard system-injected blocks
        if (text.StartsWith("# OS CONTEXT", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("# PERSISTENT TASK STATE", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("The initial screen is provided", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sentences = text.Split(new[] { '\n', '.', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length < 10) continue;

            foreach (var keyword in ConstraintKeywords)
            {
                if (trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = NormalizeSentence(trimmed);
                    if (!directives.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    {
                        directives.Add(normalized);
                        if (directives.Count >= 8) return;
                    }
                    break;
                }
            }
        }
    }

    private static (string ActionSummary, string PlanDescription) ExtractActionSummary(string text)
    {
        if (TryReadAction(text, out var actionName, out var description))
        {
            var desc = !string.IsNullOrWhiteSpace(description)
                ? NormalizeSentence(description)
                : $"Executed {actionName}";
            return ($"Action '{actionName}'", desc);
        }

        var clean = RemoveThoughts(text);
        if (!string.IsNullOrWhiteSpace(clean))
        {
            return ("Plan", NormalizeSentence(clean));
        }

        return ("Action", "Issued desktop interaction");
    }

    private static string ExtractFailureSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "action did not achieve expected state";
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("GROUNDING_FAILED", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("VERIFICATION_FEEDBACK", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeSentence(line);
            }
        }
        return "observed no positive state change";
    }

    private static string SynthesizeCompactedTaskMemory(
        string goal,
        List<string> userDirectives,
        HashSet<string> entities,
        List<string> milestones,
        List<string> pitfalls,
        List<string> actionHistory,
        PersistentTaskContext? taskContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ==============================================================================");
        sb.AppendLine("# TASK CONTINUITY & COMPACTED MEMORY (Practically Lossless Knowledge Distillation)");
        sb.AppendLine("# Earlier turns were compacted to preserve context budget without losing vital state.");
        sb.AppendLine("# ==============================================================================");
        sb.AppendLine();

        // 1. Primary Goal & Constraints
        sb.AppendLine("## 1. PRIMARY OBJECTIVE & USER CONSTRAINTS");
        sb.AppendLine($"- Goal: {goal}");
        if (userDirectives.Count > 0)
        {
            sb.AppendLine("- User Directives & Constraints:");
            foreach (var directive in userDirectives.Take(6))
            {
                sb.Append("  * ").AppendLine(directive);
            }
        }
        sb.AppendLine();

        // 2. Discovered Entities & Facts
        if (entities.Count > 0)
        {
            sb.AppendLine("## 2. DISCOVERED ENTITIES & ACCUMULATED KNOWLEDGE BASE");
            foreach (var entity in entities.Take(16))
            {
                sb.Append("- ").AppendLine(entity);
            }
            sb.AppendLine();
        }

        // 3. Accomplished Milestones
        if (milestones.Count > 0 || actionHistory.Count > 0)
        {
            sb.AppendLine("## 3. ACCOMPLISHED MILESTONES (DO NOT REDO)");
            var combinedMilestones = milestones.Concat(actionHistory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .TakeLast(8);
            foreach (var milestone in combinedMilestones)
            {
                sb.Append("- [✓] ").AppendLine(milestone);
            }
            sb.AppendLine();
        }

        // 4. Failed Attempts & Pitfalls to Avoid
        if (pitfalls.Count > 0)
        {
            sb.AppendLine("## 4. PITFALLS TO AVOID (PREVIOUS FAILED ACTIONS - DO NOT REPEAT)");
            foreach (var pitfall in pitfalls.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(6))
            {
                sb.Append("- [!] ").AppendLine(pitfall);
            }
            sb.AppendLine();
        }

        // 5. Active Task State
        if (taskContext != null && taskContext.Todos.Count > 0)
        {
            sb.AppendLine("## 5. ACTIVE TO-DO STATUS");
            foreach (var todo in taskContext.Todos.Take(8))
            {
                sb.Append("- [").Append(todo.IsCompleted ? "x" : " ").Append("] ")
                    .Append("(").Append(todo.Id).Append(") ").AppendLine(todo.Text);
            }
            sb.AppendLine();
        }

        sb.AppendLine("# ==============================================================================");
        return sb.ToString().TrimEnd();
    }

    private static bool IsImage(JsonNode? block) =>
        block is JsonObject obj && obj["type"]?.GetValue<string>() is "image" or "image_url";

    private static bool IsSystem(JsonObject message) =>
        string.Equals(message["role"]?.GetValue<string>(), "system", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssistant(JsonObject message) =>
        string.Equals(message["role"]?.GetValue<string>(), "assistant", StringComparison.OrdinalIgnoreCase);

    private static int CountImages(JsonObject message)
    {
        if (message["content"] is not JsonArray content) return 0;
        return content.Count(IsImage);
    }

    private static bool TryReadAction(string text, out string actionName, out string description)
    {
        actionName = string.Empty;
        description = string.Empty;
        for (var start = text.Length - 1; start >= 0; start--)
        {
            if (text[start] != '{' || !TryReadBalancedObject(text, start, out var json)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("action", out var action)
                    || action.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(action.GetString()))
                    continue;
                actionName = action.GetString()!;
                if (root.TryGetProperty("description", out var desc)
                    && desc.ValueKind == JsonValueKind.String)
                    description = desc.GetString() ?? string.Empty;
                return true;
            }
            catch (JsonException)
            {
                // Try next balanced object
            }
        }
        return false;
    }

    private static bool TryReadBalancedObject(string text, int start, out string json)
    {
        json = string.Empty;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0)
            {
                json = text[start..(index + 1)];
                return true;
            }
        }
        return false;
    }

    private static string ExtractText(JsonObject message)
    {
        if (message["content"] is not JsonArray content) return string.Empty;
        var sb = new StringBuilder();
        foreach (var block in content)
        {
            if (block is not JsonObject obj
                || !string.Equals(obj["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = obj["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
        }
        return sb.ToString().Trim();
    }

    private static string RemoveThoughts(string text)
    {
        var start = text.IndexOf("<thoughts>", StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf("</thoughts>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
            text = text.Remove(start, end + "</thoughts>".Length - start);
        text = text.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase);
        return text.Trim();
    }

    private static string NormalizeSentence(string? text, int maxLength = 260)
    {
        var normalized = string.Join(' ', (text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > maxLength)
            normalized = normalized[..maxLength] + "...";
        if (normalized.Length > 0 && normalized[^1] is not ('.' or '!' or '?' or ':'))
            normalized += ".";
        return normalized;
    }
}
