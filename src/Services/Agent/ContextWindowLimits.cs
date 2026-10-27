// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/ContextWindowLimits.cs
//
// Catalog of LLM context window limits, token estimation, and proactive/reactive
// context limit boundary detection.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vantage.Services.Agent;

public static class ContextWindowLimits
{
    private const int DefaultContextWindow = 32_768;
    private const int GroqSafeContextCeiling = 24_000; // Protect against Groq TPM throttling
    private const int MaxProactiveCompactionCeiling = 96_000; // Bound round-trip payload latency

    public sealed record TokenEstimate(
        int TextTokens,
        int ImageTokens,
        int TotalTokens,
        int ContextLimit,
        int CompactionThreshold,
        bool NeedsCompaction);

    /// <summary>
    /// Resolves the maximum context window in tokens for a given model and optional endpoint URL.
    /// </summary>
    public static int GetModelContextWindow(string? model, string? baseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase))
        {
            // Groq models have generous theoretical contexts (128k) but strict TPM (tokens/min) quotas.
            // A conservative ceiling prevents rate-limit stalls on free and standard tiers.
            return GroqSafeContextCeiling;
        }

        if (string.IsNullOrWhiteSpace(model)) return DefaultContextWindow;

        var m = model.ToLowerInvariant().Trim();

        // Anthropic Claude families (200k standard)
        if (m.Contains("claude"))
            return 200_000;

        // Google Gemini families (1M standard)
        if (m.Contains("gemini"))
            return 1_000_000;

        // OpenAI flagship & reasoning models
        if (m.Contains("gpt-4o") || m.Contains("gpt-4-turbo") || m.Contains("o1") || m.Contains("o3-mini") || m.Contains("chatgpt-4o"))
            return 128_000;
        if (m.Contains("gpt-4"))
            return 8_192;
        if (m.Contains("gpt-3.5"))
            return 16_385;

        // Meta Llama families
        if (m.Contains("llama-3.3") || m.Contains("llama-3.2") || m.Contains("llama-3.1") || m.Contains("llama3.3") || m.Contains("llama3.1"))
            return 128_000;
        if (m.Contains("llama-3-8b") || m.Contains("llama-3-70b") || m.Contains("llama3-8b") || m.Contains("llama3-70b"))
            return 8_192;
        if (m.Contains("llama-2") || m.Contains("llama2"))
            return 4_096;

        // DeepSeek
        if (m.Contains("deepseek"))
            return 64_000;

        // Mistral
        if (m.Contains("mistral-large"))
            return 128_000;
        if (m.Contains("mistral") || m.Contains("mixtral") || m.Contains("codestral"))
            return 32_768;

        // Qwen
        if (m.Contains("qwen-2.5") || m.Contains("qwen2.5"))
            return 131_072;
        if (m.Contains("qwen"))
            return 32_768;

        // Gemma
        if (m.Contains("gemma-2") || m.Contains("gemma2"))
            return 8_192;

        return DefaultContextWindow;
    }

    /// <summary>
    /// Computes the proactive token threshold at which context compaction should activate.
    /// Typically 75% of context window minus completion headroom.
    /// </summary>
    public static int GetCompactionThreshold(string? model, string? baseUrl = null, int maxOutputTokens = 4096)
    {
        var window = GetModelContextWindow(model, baseUrl);
        var targetThreshold = (int)(window * 0.75) - maxOutputTokens;
        
        // Never exceed the proactive ceiling to ensure reasonable payload transport size
        targetThreshold = Math.Min(targetThreshold, MaxProactiveCompactionCeiling);

        // Ensure a healthy lower bound
        return Math.Max(targetThreshold, 4_000);
    }

    /// <summary>
    /// Accurately estimates token usage across text and multimodal image blocks.
    /// </summary>
    public static TokenEstimate EstimateTokens(
        IEnumerable<JsonObject> messages,
        int maxOutputTokens = 4096,
        string? model = null,
        string? baseUrl = null)
    {
        int textTokens = 0;
        int imageTokens = 0;

        foreach (var message in messages)
        {
            textTokens += 4; // Framing envelope overhead per turn (role, delimiters)

            if (message["content"] is not JsonArray content) continue;

            foreach (var block in content)
            {
                if (block is not JsonObject obj) continue;
                var type = obj["type"]?.GetValue<string>();

                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    var text = obj["text"]?.GetValue<string>() ?? string.Empty;
                    if (text.Length > 0)
                    {
                        // 1 token ~= 3.8 English characters for typical markdown/code/JSON
                        textTokens += (int)Math.Ceiling(text.Length / 3.8);
                    }
                }
                else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                {
                    // A standard high-detail 1152px downscaled screenshot in Anthropic/OpenAI
                    // consumes approximately 1,600 tokens.
                    imageTokens += 1600;
                }
            }
        }

        int total = textTokens + imageTokens + Math.Max(0, maxOutputTokens);
        int contextLimit = GetModelContextWindow(model, baseUrl);
        int threshold = GetCompactionThreshold(model, baseUrl, maxOutputTokens);

        return new TokenEstimate(
            TextTokens: textTokens,
            ImageTokens: imageTokens,
            TotalTokens: total,
            ContextLimit: contextLimit,
            CompactionThreshold: threshold,
            NeedsCompaction: total >= threshold);
    }

    /// <summary>
    /// Detects whether an HTTP error status, error code, or response body indicates that
    /// the provider's context window or token limits were reached or exceeded.
    /// </summary>
    public static bool IsContextLimitExceeded(int httpStatus, string? body, string? errorMessage)
    {
        if (httpStatus == 413) return true; // Payload Too Large

        var combined = $"{body ?? ""} {errorMessage ?? ""}".ToLowerInvariant();

        if (combined.Contains("context_length_exceeded") ||
            combined.Contains("maximum context length") ||
            combined.Contains("context length") ||
            combined.Contains("prompt is too long") ||
            combined.Contains("prompt exceeds") ||
            combined.Contains("too many tokens") ||
            combined.Contains("token limit") ||
            combined.Contains("exceeds model context") ||
            combined.Contains("payload-too-large") ||
            combined.Contains("payload too large") ||
            combined.Contains("request entity too large") ||
            combined.Contains("request body exceeds") ||
            combined.Contains("reduce the length of the messages") ||
            combined.Contains("maximum model length") ||
            combined.Contains("max_model_len") ||
            combined.Contains("longer than the maximum"))
        {
            return true;
        }

        // TPM rate limits where requested tokens exceed allowance
        if (httpStatus == 429 &&
            (combined.Contains("tokens per minute") || combined.Contains(" tpm ")) &&
            (combined.Contains("requested") || combined.Contains("limit")))
        {
            return true;
        }

        return false;
    }
}
