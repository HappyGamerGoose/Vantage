// SPDX-License-Identifier: MIT
// Vantage — Services/VisionCapability.cs
//
// Single source of truth for "can this provider drive the desktop-
// control agent?". Resolution order (most decisive first):
//
//   1. Provider.VisionOverride                — user pinned Yes or No.
//   2. KTextOnlyModels (denylist)              — known text-only model families
//                                                → No unconditionally.
//   3. KKnownVisionSubstrings (positive match) — known vision model families
//                                                → Yes unconditionally.
//   4. KGenericVisionMarkers (positive match)  — any model name containing
//                                                -vision / -vl / -mm /
//                                                multimodal / -multimodal
//                                                → Yes.
//   5. Live probe (capped, time-bounded)       — 16×16 blank JPEG at the
//                                                model. Result cached for at
//                                                most 10 minutes per
//                                                (provider.Id, model).
//   6. Default                                 — optimistic Yes (try with
//                                                vision). If the actual call
//                                                rejects the image, Worker
//                                                surfaces the error cleanly.
//
// The previous version used a *positive-only* whitelist that returned
// false for any unknown model name. With custom OpenRouter / Together /
// Fireworks / self-hosted endpoints that means well-known vision models
// pass through, but anything slightly renamed gets blocked. Now unknown
// models are trusted to be vision-capable — the actual call tells us.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Vantage.Models;

namespace Vantage.Services;

public sealed class VisionCapability
{
    /// <summary>
    /// User-level verdict for a configured provider. Most providers should
    /// use Auto; pin Yes/No when the heuristic gets something wrong.
    /// </summary>
    public VisionVerdict VerdictAsync(Provider provider) => Classify(provider);

    /// <summary>Synchronous version. Used by the UI when the model-selector needs the verdict.</summary>
    public VisionVerdict Classify(Provider provider)
    {
        var model = provider.DefaultModel ?? "";
        if (provider.VisionOverride == VisionOverride.ForceNo)  return VisionVerdict.No;
        if (provider.VisionOverride == VisionOverride.ForceYes) return VisionVerdict.Yes;

        if (IsKnownTextOnly(model)) return VisionVerdict.No;
        if (IsKnownVisionModel(model)) return VisionVerdict.Yes;
        if (HasGenericVisionMarker(model)) return VisionVerdict.Yes;

        // Empty/unknown → optimistic so custom providers get a chance.
        return string.IsNullOrWhiteSpace(model) ? VisionVerdict.No : VisionVerdict.Unknown;
    }

    /// <summary>
    /// Run a real 1-token vision probe against the provider. Cached per
    /// (provider.Id, model) for a short window so transient errors don't
    /// stick and a model change is picked up immediately.
    /// </summary>
    public async Task<VisionVerdict> SupportsAsync(Provider provider)
    {
        var cls = Classify(provider);
        if (cls is VisionVerdict.Yes or VisionVerdict.No) return cls;

        var key = provider.Id + "|" + (provider.DefaultModel ?? "");
        if (_probeCache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return entry.Result;
        }

        bool? probeResult;
        try { probeResult = await ProbeAsync(provider); }
        catch { probeResult = null; }

        var verdict = probeResult switch
        {
            true  => VisionVerdict.Yes,
            false => VisionVerdict.No,
            null  => VisionVerdict.Unknown, // inconclusive — try anyway
        };
        _probeCache[key] = (verdict, DateTimeOffset.UtcNow.AddMinutes(10));
        return verdict;
    }

    public void InvalidateProvider(string providerId)
    {
        // Best-effort: clear every cached entry belonging to this provider.
        // (Cache keys include the model, so we iterate to find them.)
        var stale = new List<string>();
        foreach (var kv in _probeCache)
            if (kv.Key.StartsWith(providerId + "|", StringComparison.Ordinal))
                stale.Add(kv.Key);
        foreach (var k in stale) _probeCache.Remove(k);
    }

    private readonly Dictionary<string, (VisionVerdict Result, DateTimeOffset ExpiresAt)> _probeCache = new();

    // ─────────────────────────────────────────────────────────────────
    //  Heuristic tables
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Known text-only model families. These reject image content; if a
    /// user configures one of these, the agent should refuse up front
    /// rather than burning API calls. Match is substring + lowercased.
    /// </summary>
    private static readonly string[] KTextOnlyModels =
    {
        // OpenAI text-only
        "text-embedding", "text-search", "text-similarity", "text-moderation",
        "code-search", "babbage-code",
        "gpt-3.5-turbo-instruct",
        // Audio models (not chat)
        "whisper-", "tts-", "gpt-4o-transcribe", "gpt-4o-mini-transcribe",
        // Image generators (input is prompt only, no image)
        "dall-e-", "gpt-image-",
        // Legacy completion models
        "davinci", "curie-", "babbage-002", "ada-",
        // Inference / scoring endpoints
        "text-scorer", "moderation-",
        // Meta pure-text Llama models (anything not in vision families)
        "llama-3.1-", "llama-3.3-", "llama-guard-2-",
    };

    /// <summary>
    /// Explicit positive matches — vision-capable model families. Used
    /// to short-circuit before the generic-marker check so we don't
    /// accidentally classify a hypothetical text model whose name happens
    /// to contain "vision" as vision-capable. All entries are
    /// substring matches on the lowercased model name.
    /// </summary>
    private static readonly string[] KKnownVisionModels =
    {
        // Anthropic
        "claude-3",
        "claude-sonnet-4",
        "claude-opus-4",
        "claude-haiku-4",
        "claude-3-5-sonnet",
        "claude-3-5-haiku",
        "claude-3-7-sonnet",
        // OpenAI — every 4o / 4-turbo / 4.1 / 5 variant supports images.
        "gpt-4o",
        "gpt-4-vision",
        "gpt-4-turbo",
        "gpt-4.1",
        "gpt-5",
        // OpenAI reasoning models pass images through.
        "o1-",
        "o1-mini",
        "o3-",
        "o4-",
        // Google — all released Gemini generations accept images
        "gemini",
        "gemma-3",
        // Meta Llama vision variants — both native Llama 4 and the
        // older Llama 3.2 Vision (covered by OpenRouter / Together /
        // Hugging Face / Groq). Includes the meta-llama/ OpenAI-route
        // alias prefixes used by OpenRouter and similar aggregators.
        "llama-3.2-90b-vision",
        "llama-3.2-11b-vision",
        "llama-4",
        // Llama 4 aliases — 17B Scout, 17B/128E Maverick, Behemoth.
        // Matched as the bare name only (without "llama-4" prefix) so
        // deployments that rename the model still qualify.
        "scout-17b",
        "maverick-17b",
        // Mistral — Pixtral family + Mistral Small 3 vision-capable.
        "pixtral",
        "mistral-small-3",
        // Qwen VL family
        "qwen-vl",
        "qwen2-vl",
        "qwen2.5-vl",
        // DeepSeek
        "deepseek-vl",
        // Microsoft Phi — vision variants
        "phi-3-vision",
        "phi-3.5-vision",
        "phi-4-multimodal",
        // xAI Grok vision
        "grok-2-vision",
        "grok-vision",
        "grok-1.5-vision",
        // Open-source multimodal models. Many deployments rename them
        // arbitrarily, but every release uses one of these substrings.
        "llava",
        "molmo",
        "paligemma",
        "internvl",
        "smolvlm",
        "llama-guard-3-vision",
        // Vision-only research models
        "fuyu-8b",
        "cogvlm",
        "yi-vl",
    };

    /// <summary>
    /// Generic markers applied after the explicit lists. A model whose
    /// name contains any of these is treated as vision-capable because
    /// every contemporary VLM uses at least one of these conventions.
    /// </summary>
    private static readonly string[] KGenericVisionMarkers =
    {
        "-vision", "vision-", "_vision",
        "-vl", "_vl",
        "-mm-", "-multimodal", "multimodal-",
        "image-text-to-text",
        "-oc",
    };

    public static bool IsKnownTextOnly(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.ToLowerInvariant();
        foreach (var t in KTextOnlyModels)
            if (m.Contains(t)) return true;
        return false;
    }

    public static bool IsKnownVisionModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.ToLowerInvariant();
        foreach (var v in KKnownVisionModels)
            if (m.Contains(v)) return true;
        return false;
    }

    public static bool HasGenericVisionMarker(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.ToLowerInvariant();
        foreach (var t in KGenericVisionMarkers)
            if (m.Contains(t)) return true;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Live probe
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a single non-streaming chat completion that includes a
    /// minimal 16×16 blank JPEG. Returns:
    ///   true  — provider accepted the image content (HTTP 200 OK).
    ///   false — provider rejected it with a vision-related 4xx.
    ///   null  — inconclusive (network failure, auth, rate-limit, 5xx,
    ///           non-modality 4xx). The caller falls through to a
    ///           default-allow verdict so the agent gets a real failure
    ///           message instead of silent refusal.
    /// Each call costs ≤ 1 generated token.
    /// </summary>
    private static async Task<bool?> ProbeAsync(Provider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
            string.IsNullOrWhiteSpace(provider.ApiKey)    ||
            string.IsNullOrWhiteSpace(provider.DefaultModel))
            return null;

        const string JpegBase64 =
            "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAARABEBAP/xAAdAAEAAgMBAQEAAAAAAAAAAAAABgcEBQgDAAIBA/8QAFgEBAQEAAAAAAAAAAAAAAAAAAAEC/9oAMBAAMCAxACAAAAAFAAAAAB/8QAGhAAAgIDAAAAAAAAAAAAAAAAAAcFBgIDBP/aAAgBAQABPxA=";

        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var isAnthropic = baseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req  = new HttpRequestMessage(HttpMethod.Post,
                isAnthropic ? $"{baseUrl}/v1/messages" : $"{baseUrl}/chat/completions");

            var apiKey = provider.ApiKey.Trim();
            if (isAnthropic)
            {
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Headers.Add("anthropic-beta", "computer-use-2025-01-24");
            }
            else
            {
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }

            var payload = isAnthropic
                ? BuildAnthropicProbePayload(provider.DefaultModel.Trim(), JpegBase64)
                : BuildOpenAIProbePayload(provider.DefaultModel.Trim(), JpegBase64);

            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (resp.IsSuccessStatusCode) return true;

            if (resp.StatusCode == HttpStatusCode.BadRequest ||
                resp.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                string body;
                try { body = await resp.Content.ReadAsStringAsync(); }
                catch { return null; }
                var lower = body.ToLowerInvariant();
                if (lower.Contains("vision")                  ||
                    lower.Contains("image_url")               ||
                    lower.Contains("multimodal")              ||
                    lower.Contains("image input")             ||
                    lower.Contains("does not support image")  ||
                    lower.Contains("image is not supported")  ||
                    lower.Contains("expected `type` to be")   ||  // OpenAI variant
                    lower.Contains("no `image_url` ")         ||
                    lower.Contains("invalid content part"))
                    return false;
                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildOpenAIProbePayload(string model, string jpegB64) =>
        JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 1,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "." },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/jpeg;base64,{jpegB64}" }
                        }
                    }
                }
            }
        });

    private static string BuildAnthropicProbePayload(string model, string jpegB64) =>
        JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 1,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = "image/jpeg",
                                data = jpegB64
                            }
                        },
                        new { type = "text", text = "." }
                    }
                }
            }
        });
}

public enum VisionVerdict
{
    Unknown,
    Yes,
    No,
}
