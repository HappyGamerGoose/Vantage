// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vantage.Services.Agent;

/// <summary>
/// Keeps a bounded, recent slice of an LLM conversation. Task continuity is
/// supplied separately by <see cref="PersistentTaskContext"/>, so old model
/// prose and screenshots can be discarded instead of accumulating forever.
/// </summary>
public static class ContextWindowCompactor
{
    private const int MaxSummaryItems = 8;
    private const int MaxSummaryItemLength = 220;

    public sealed record Result(int RemovedMessages, int RemovedImages, string Summary = "");

    public static Result Compact(
        List<JsonObject> messages,
        int keepRecentNonSystemMessages,
        int keepRecentImages,
        string? existingSummary = null)
    {
        if (messages.Count == 0) return new Result(0, 0, existingSummary ?? string.Empty);

        keepRecentNonSystemMessages = Math.Max(1, keepRecentNonSystemMessages);
        keepRecentImages = Math.Max(1, keepRecentImages);

        // Find the oldest message in the active window. The window is
        // deliberately anchored on a user observation so compaction never
        // leaves an orphaned assistant plan at its front.
        var firstRetainedIndex = messages.Count;
        var remainingMessages = keepRecentNonSystemMessages;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (IsSystem(message)) continue;
            if (remainingMessages-- > 0) firstRetainedIndex = index;
            else break;
        }

        if (firstRetainedIndex == messages.Count)
            return CompactImages(messages, 0, keepRecentImages, existingSummary ?? string.Empty);

        // A new screenshot is appended before each model call. That can make
        // the raw N-message boundary land on the previous assistant plan. In
        // that case drop the orphaned plan and start at the next observation.
        while (firstRetainedIndex < messages.Count
               && IsAssistant(messages[firstRetainedIndex]))
        {
            firstRetainedIndex++;
        }

        var removedItems = new List<string>();
        var removedMessages = 0;
        var removedImages = 0;
        for (var index = firstRetainedIndex - 1; index >= 0; index--)
        {
            if (IsSystem(messages[index])) continue;
            removedItems.Add(SummarizeMessage(messages[index]));
            removedImages += CountImages(messages[index]);
            messages.RemoveAt(index);
            removedMessages++;
        }

        // Message removal and image removal are counted independently. A
        // screenshot in a removed message is still part of the discarded
        // visual context and should be included in diagnostics.
        var summary = MergeSummary(existingSummary, removedItems);
        var imageResult = CompactImages(messages, removedMessages, keepRecentImages, summary);
        return new Result(
            imageResult.RemovedMessages,
            removedImages + imageResult.RemovedImages,
            imageResult.Summary);
    }

    private static Result CompactImages(
        List<JsonObject> messages,
        int removedMessages,
        int keepRecentImages,
        string summary)
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

            if (removedFromMessage > 0 && content.Count == 1 && content[0] is JsonObject onlyBlock
                && onlyBlock["type"]?.GetValue<string>() is "text")
            {
                var text = onlyBlock["text"]?.GetValue<string>() ?? string.Empty;
                if (!text.Contains("visual context discarded", StringComparison.OrdinalIgnoreCase))
                {
                    onlyBlock["text"] = text + "\n[Earlier visual context discarded; use the latest screen and persistent task state.]";
                }
            }
        }

        return new Result(removedMessages, removedImages, summary);
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

    private static string MergeSummary(string? existingSummary, IEnumerable<string> newItems)
    {
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            foreach (var line in existingSummary.Split('\n'))
            {
                var normalized = NormalizeSentence(line.Trim().TrimStart('-', ' '));
                if (!string.IsNullOrWhiteSpace(normalized)
                    && !normalized.StartsWith("[COMPACTED TRAJECTORY", StringComparison.OrdinalIgnoreCase)
                    && !normalized.StartsWith("Older screenshots", StringComparison.OrdinalIgnoreCase))
                    items.Add(normalized);
            }
        }

        foreach (var item in newItems.Reverse())
        {
            var normalized = NormalizeSentence(item);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            items.Add(normalized);
        }

        var distinct = items
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(MaxSummaryItems)
            .ToArray();
        if (distinct.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[COMPACTED TRAJECTORY - text only]");
        foreach (var item in distinct) sb.Append("- ").AppendLine(item);
        sb.Append("Older screenshots were discarded; use the latest screenshot and persistent task state.");
        return sb.ToString();
    }

    private static string SummarizeMessage(JsonObject message)
    {
        var role = message["role"]?.GetValue<string>() ?? "user";
        var text = ExtractText(message);
        if (string.IsNullOrWhiteSpace(text))
            return role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? "The assistant issued a visual action; its screenshot was discarded."
                : "The desktop was observed; its screenshot was discarded.";

        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadAction(text, out var actionName, out var description))
            {
                if (!string.IsNullOrWhiteSpace(description))
                    return $"Planned {NormalizeSentence(description)}";
                return $"Planned the {actionName} action.";
            }

            text = RemoveThoughts(text);
            return $"The assistant planned: {NormalizeSentence(text)}";
        }

        // The user-side message contains the live world-state block and task
        // tracker. Keeping a short observation marker is enough because the
        // current screenshot and durable task state are always re-injected.
        return "The desktop was observed before the next action; that older screenshot was discarded.";
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
                // Try the next outer object. Thought text can contain braces.
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
        return text;
    }

    private static string NormalizeSentence(string? text)
    {
        var normalized = string.Join(' ', (text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > MaxSummaryItemLength)
            normalized = normalized[..MaxSummaryItemLength] + "...";
        if (normalized.Length > 0 && normalized[^1] is not ('.' or '!' or '?'))
            normalized += ".";
        return normalized;
    }
}
