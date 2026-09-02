// SPDX-License-Identifier: MIT
// Vantage - local display-only model naming.

using System.Text.RegularExpressions;

namespace Vantage.Services;

/// <summary>
/// Turns provider model identifiers into short names for the picker only.
/// The identifier itself remains untouched in Provider.DefaultModel.
///
/// This intentionally runs as a tiny local naming layer rather than making a
/// network request or bundling a second language model for a cosmetic label.
/// It is deterministic, instant, and can be replaced by an optional SLM
/// adapter later without changing the ModelChoice contract.
/// </summary>
public static class ModelDisplayNameService
{
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["meta/muse-glimmer-30b"] = "Muse Glimmer",
        ["muse-glimmer-30b"] = "Muse Glimmer",
        ["openai/gpt-4.1"] = "GPT",
        ["gpt-4.1"] = "GPT",
        ["anthropic/claude-sonnet-4-5"] = "Claude Sonnet",
        ["claude-sonnet-4-5"] = "Claude Sonnet",
        ["google/gemini-2.5-pro"] = "Gemini Pro",
        ["gemini-2.5-pro"] = "Gemini Pro",
    };

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "base", "chat", "instruct", "it", "latest", "preview", "experimental",
        "exp", "free", "online", "nitro", "turbo", "hf",
    };

    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai", "api", "gpt", "llm", "ocr", "t5", "vl", "xl",
    };

    private static readonly string[] KnownBases =
    {
        "deepseek", "nemotron", "mistral", "mixtral", "claude", "gemini",
        "llama", "qwen", "kimi", "gemma", "grok", "phi", "command", "gpt",
    };

    public static string GetFriendlyName(string? modelId)
    {
        var raw = modelId?.Trim() ?? string.Empty;
        if (raw.Length == 0) return "Unnamed model";
        if (KnownLabels.TryGetValue(raw, out var known)) return known;

        var candidate = raw;
        var slash = candidate.LastIndexOf('/');
        if (slash >= 0 && slash < candidate.Length - 1)
            candidate = candidate[(slash + 1)..];

        var variant = candidate.IndexOf(':');
        if (variant > 0) candidate = candidate[..variant];

        var words = new List<string>();
        foreach (var token in Regex.Split(candidate, "[-_.\\s]+"))
        {
            var word = NormalizeToken(token);
            if (word.Length == 0 || Noise.Contains(word)) continue;
            if (!words.Contains(word, StringComparer.OrdinalIgnoreCase)) words.Add(word);
        }

        if (words.Count == 0) return "Unnamed model";
        return string.Join(' ', words.Select(FormatWord));
    }

    private static string NormalizeToken(string token)
    {
        var value = token.Trim();
        if (value.Length == 0) return string.Empty;

        if (Regex.IsMatch(value, @"^\d+(?:\.\d+)?[a-z]*$", RegexOptions.IgnoreCase))
            return string.Empty;

        var lower = value.ToLowerInvariant();
        foreach (var knownBase in KnownBases)
        {
            if (lower.StartsWith(knownBase, StringComparison.OrdinalIgnoreCase))
                return knownBase;
        }

        // Remove parameter/version digits from names such as "muse30b" while
        // preserving a meaningful alphabetic token.
        var withoutDigits = Regex.Replace(value, @"\d", string.Empty);
        if (withoutDigits.Length <= 2 && withoutDigits.Length < value.Length)
            return string.Empty;
        return withoutDigits;
    }

    private static string FormatWord(string word)
    {
        if (Acronyms.Contains(word)) return word.ToUpperInvariant();
        if (word.Length == 1) return word.ToUpperInvariant();
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }
}
