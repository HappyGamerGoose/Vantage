// SPDX-License-Identifier: MIT
// Vantage — Services/S3/LMMAgent.cs
//
// Ported from gui_agents/s3/core/mllm.py. Owns the message history for an
// LLM-backed agent and handles multimodal content (text + image_url).
// Supports both Anthropic-native and OpenAI-compat message shapes; the
// underlying LMMEngine picks the right transport.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vantage.Services.Agent;

public sealed class LmmAgent
{
    private const long DefaultImageHistoryBudgetBytes = 25L * 1024 * 1024;
    private const int ActiveVisualHistoryTurns = 6;

    private readonly LMMEngine _engine;
    public string SystemPrompt { get; private set; } = "You are a helpful assistant.";
    public List<JsonObject> Messages { get; private set; } = new();
    public bool UseThinking { get; set; }

    public LmmAgent(LMMEngine engine, string systemPrompt = "You are a helpful assistant.")
    {
        _engine = engine;
        AddSystemPrompt(systemPrompt);
    }

    public void Reset() => Messages = new List<JsonObject>
    {
        new JsonObject { ["role"] = "system", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = SystemPrompt } } }
    };

    public void AddSystemPrompt(string prompt)
    {
        SystemPrompt = prompt;
        if (Messages.Count > 0) Messages[0] = new JsonObject
        {
            ["role"] = "system", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } }
        };
        else Reset();
    }

    /// <summary>
    /// Add a text-only message. Role defaults by alternating user/assistant;
    /// override with `role` for system-forced injections.
    /// </summary>
    public void AddTextMessage(string text, string? role = null)
    {
        var inferred = role ?? InferNextRole();
        Messages.Add(new JsonObject
        {
            ["role"]    = inferred,
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
        });
    }

    /// <summary>
    /// Add a multimodal message — text + one or more base64-encoded JPEG/PNG
    /// images. The engine picks the right content shape (Anthropic image
    /// block vs OpenAI image_url).
    /// </summary>
    public void AddImageMessage(string text, IReadOnlyList<string> base64Jpegs, string? role = null)
    {
        var inferred = role ?? InferNextRole();
        var content  = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var b64 in base64Jpegs)
        {
            // Sniff the actual image format from the base64 magic header.
            // The capture pipeline now produces lossless PNG (not JPEG), so
            // hardcoding "image/jpeg" produces a 422 from Anthropic — the
            // API is strict about media_type matching the byte stream.
            var mediaType = DetectImageMediaType(b64);
            if (_engine is AnthropicEngine)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"]       = "base64",
                        ["media_type"] = mediaType,
                        ["data"]       = b64
                    }
                });
            }
            else
            {
                // For OpenAI-compat the image_url's data URL embeds the mime
                // — having it match the actual bytes prevents "invalid image
                // format" rejections on Groq / OpenAI / StepFun / Azure.
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"]    = $"data:{mediaType};base64,{b64}",
                        ["detail"] = "high"
                    }
                });
            }
        }
        Messages.Add(new JsonObject { ["role"] = inferred, ["content"] = content });
    }

    /// <summary>
    /// Sniff a base64 PNG / JPEG / GIF / WebP from the magic bytes at the
    /// start of the string. PNG: "iVBORw…" (89 50 4E 47). JPEG: "/9j/"
    /// (FF D8 FF). GIF: "R0lGOD…" (47 49 46 38). WebP: "UklGR…" (52 49
    /// 46 46). Defaults to <c>image/png</c> for unrecognized payloads —
    /// safer than image/jpeg since the Vantage capture pipeline produces
    /// PNG by default.
    /// </summary>
    private static string DetectImageMediaType(string b64)
    {
        if (string.IsNullOrEmpty(b64) || b64.Length < 8) return "image/png";
        var p = b64.Length >= 8 ? b64.Substring(0, 8) : b64;
        // PNG = 89 50 4E 47 0D 0A 1A 0A → ASCII base64 "iVBORw0K"
        if (p.StartsWith("iVBORw0K", StringComparison.Ordinal)) return "image/png";
        // JPEG = FF D8 FF ?? → "/9j/" (with optional 4th byte)
        if (p.StartsWith("/9j/", StringComparison.Ordinal) ||
            p.StartsWith("/9j/4", StringComparison.Ordinal)) return "image/jpeg";
        // GIF = "GIF8…" → "R0lGOD"
        if (p.StartsWith("R0lGOD", StringComparison.Ordinal)) return "image/gif";
        // WebP = "RIFF????WEBP" → "UklGR"
        if (p.StartsWith("UklGR", StringComparison.Ordinal)) return "image/webp";
        // BMP = "Qk…" (42 4D) → "Qk0" / "Qk1"
        if (p.StartsWith("Qk0", StringComparison.Ordinal) ||
            p.StartsWith("Qk1", StringComparison.Ordinal)) return "image/bmp";
        return "image/png";
    }

    /// <summary>Plain text completion. For grounding/short prompts.</summary>
    public async Task<LmmResponse> GetResponseAsync(double temperature = 0.0, int maxTokens = 4096, CancellationToken ct = default)
    {
        // Bound the request body — long-horizon workers (10+ steps with PNG
        // screenshots) can otherwise pile up hundreds of MB of base64 image
        // blocks, which some upstream proxies cap at ~250 MB. We squashed
        // old image attachments to text placeholders so the model still has
        // the conversation context but the wire payload stays sane.
        CompactImageHistory(
            maxBodyBytes: DefaultImageHistoryBudgetBytes,
            keepLastImageTurns: ActiveVisualHistoryTurns);

        // Defensive deep clone — same parent-tracking problem we hit in
        // AgentOrchestrator before. Each LMMEngine.GenerateAsync re-parents
        // the message array into a request body; without cloning, the
        // second call would throw "node already has a parent".
        var snapshot = Messages.Select(m => (JsonObject)m.DeepClone()!).ToList();
        var req = new LmmRequest
        {
            Model = _engine.Model,
            Messages = snapshot,
            MaxTokens = maxTokens,
            Temperature = temperature,
            UseThinking = UseThinking
        };
        return await _engine.GenerateAsync(req, ct);
    }

    /// <summary>
    /// Walk the message history, measure the projected JSON byte size of all
    /// base64 image blocks, and replace any image blocks in turns older than
    /// the K-th most recent image-bearing message with a
    /// "[screenshot omitted — turn N]" placeholder. K is
    /// <paramref name="keepLastImageTurns"/>. The slot count + names of
    /// recent turns are preserved so the model can still reference what it
    /// had been looking at. Called automatically from
    /// <see cref="GetResponseAsync"/>; exposed for tests / debug.
    ///
    /// Implementation is single-pass: walk Messages once to find the cutoff
    /// index, then a second pass to strip. The previous version did up to
    /// four passes (backwards scan + HashSet construction + forward
    /// strip + body-size re-measurement) and allocated a List&lt;int&gt; +
    /// HashSet&lt;int&gt; for every call — replaced here with one integer.
    /// Per step of a long-horizon worker this saves ~6 heap allocations
    /// and a Messages.Count-sized body-size walk.
    /// </summary>
    public int CompactImageHistory(long maxBodyBytes, int keepLastImageTurns)
    {
        if (Messages.Count == 0 || keepLastImageTurns <= 0) return 0;

        // Pass 1 — walk backwards, count image-bearing turns. The index of
        // the K-th from the back is `keepCutoff`. Anything with a lower
        // index is older and is a candidate for stripping.
        int keepCutoff = -1;
        int seenImages = 0;
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (!HasImageBlock(Messages[i])) continue;
            seenImages++;
            if (seenImages == keepLastImageTurns)
            {
                keepCutoff = i;
                break;
            }
        }
        // Nothing to compact — fewer than K image turns present, or all are
        // right at the tail and we already finished the walk without
        // hitting K (= the keepCutoff stays -1).
        if (keepCutoff <= 0) return 0;

        // Pass 2 — strip image blocks from every message below `keepCutoff`.
        int replaced = 0;
        long approxSaved = 0;
        for (int i = 0; i < keepCutoff; i++)
        {
            replaced += StripImageBlocks(Messages[i], i, ref approxSaved);
        }

        // If we're STILL over budget after the cutoff-pass — for example
        // because recent image turns themselves were large — strip from
        // the keep-cutoff upward, oldest first. This loop is at most K
        // iterations, not a full pass over Messages.
        if (ApproxBodyBytes() > maxBodyBytes)
        {
            for (int i = keepCutoff; i < Messages.Count; i++)
            {
                int dropped = StripImageBlocks(Messages[i], i, ref approxSaved);
                if (dropped == 0) continue;
                replaced += dropped;
                if (ApproxBodyBytes() <= maxBodyBytes) break;
            }
        }

        if (replaced > 0)
        {
            CommonUtils.LogDiagnostic("lmm-image-history-compacted",
                $"replaced={replaced} approxSaved={approxSaved} keepLast={keepLastImageTurns} " +
                $"msgs={Messages.Count} approxBodyBytes={ApproxBodyBytes()}");
        }
        return replaced;
    }

    private static bool HasImageBlock(JsonObject msg)
    {
        if (msg["content"] is not JsonArray arr) return false;
        foreach (var block in arr)
        {
            if (block is JsonObject o && (o["type"]?.GetValue<string>() is "image" or "image_url"))
                return true;
        }
        return false;
    }

    private static int StripImageBlocks(JsonObject msg, int turnIndex, ref long approxSaved)
    {
        if (msg["content"] is not JsonArray arr) return 0;
        int n = arr.Count;
        int dropped = 0;
        var kept = new JsonArray();
        foreach (var block in arr)
        {
            if (block is null) continue;
            if (block is JsonObject o && o["type"]?.GetValue<string>() is "image" or "image_url")
            {
                approxSaved += EstimateImageBlockBytes(o);
                dropped++;
                continue;
            }
            kept.Add(block.DeepClone());
        }
        if (dropped == 0) return 0;

        // Append a textual so the model knows we trimmed.
        kept.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] =
                $"[visual history folded: screenshot from turn {turnIndex} omitted to keep the request bounded; " +
                "use the surrounding action/result text and the latest screenshots for current state]"
        });
        // Replace content
        msg["content"] = kept;
        return dropped;
    }

    private static long EstimateImageBlockBytes(JsonObject imageBlock)
    {
        // For Anthropic: source.data is a base64 string. For OpenAI:
        // image_url.url is "data:image/jpeg;base64,…". Pull whichever.
        if (imageBlock["source"] is JsonObject src &&
            src["data"] is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s.Length;  // base64 char count ≈ byte count for the wire body
        }
        if (imageBlock["image_url"] is JsonObject iu &&
            iu["url"] is JsonValue u && u.TryGetValue<string>(out var url))
        {
            var comma = url.IndexOf(',');
            return comma > 0 ? url.Length - comma - 1 : url.Length;
        }
        return 0;
    }

    /// <summary>Cheap body-size estimator — sum of base64 image strings + a
    /// rough per-message overhead. Used by <see cref="CompactImageHistory"/>
    /// so we don't re-serialize the full payload to decide whether to squash.</summary>
    private long ApproxBodyBytes()
    {
        long n = 0;
        foreach (var m in Messages)
        {
            n += 64;  // message envelope overhead
            if (m["content"] is JsonArray arr)
            {
                foreach (var block in arr)
                {
                    if (block is JsonObject o) n += EstimateImageBlockBytes(o);
                }
            }
        }
        return n;
    }

    private string InferNextRole()
    {
        if (Messages.Count == 0) return "user";
        var last = Messages[^1]["role"]?.GetValue<string>() ?? "user";
        return last == "user" ? "assistant" : "user";
    }
}
