// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace Vantage.Services.Agent;

/// <summary>
/// Keeps a bounded, recent slice of an LLM conversation. Task continuity is
/// supplied separately by <see cref="PersistentTaskContext"/>, so old model
/// prose and screenshots can be discarded instead of accumulating forever.
/// </summary>
public static class ContextWindowCompactor
{
    public sealed record Result(int RemovedMessages, int RemovedImages);

    public static Result Compact(
        List<JsonObject> messages,
        int keepRecentNonSystemMessages,
        int keepRecentImages)
    {
        if (messages.Count == 0) return new Result(0, 0);

        keepRecentNonSystemMessages = Math.Max(1, keepRecentNonSystemMessages);
        keepRecentImages = Math.Max(1, keepRecentImages);

        var retained = new HashSet<JsonObject>();
        var remainingMessages = keepRecentNonSystemMessages;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var role = message["role"]?.GetValue<string>() ?? "user";
            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                retained.Add(message);
                continue;
            }

            if (remainingMessages-- > 0)
                retained.Add(message);
        }

        var removedMessages = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (retained.Contains(messages[index])) continue;
            messages.RemoveAt(index);
            removedMessages++;
        }

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

        return new Result(removedMessages, removedImages);
    }

    private static bool IsImage(JsonNode? block) =>
        block is JsonObject obj && obj["type"]?.GetValue<string>() is "image" or "image_url";
}
