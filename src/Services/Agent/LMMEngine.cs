// SPDX-License-Identifier: MIT
// Vantage — Services/S3/LMMEngine.cs
//
// Ported from gui_agents/s3/core/engine.py. Thin HTTP wrapper around the
// OpenAI-compatible / Anthropic Messages API. The single `LMMEngine` base
// class abstracts the protocol differences; subclasses implement
// `GenerateAsync` with the right transport + auth headers.
//
// We deliberately don't bring over the OpenAI / Azure / vLLM / HuggingFace
// / Gemini / Parasail sub-classes — Vantage already routes every provider
// through its own `Provider` model with its own `BaseUrl` / `ApiKey`, and
// the two relevant wire formats are Anthropic-native and OpenAI-compatible.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vantage.Services.Agent;

public enum LmmProvider { Anthropic, OpenAICompat }

public sealed class LmmRequest
{
    public string Model { get; init; } = "";
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.0;
    public List<JsonObject> Messages { get; init; } = new();
    public bool UseThinking { get; init; } = false;
}

public sealed class LmmResponse
{
    public string Text { get; init; } = "";
    public string? Thinking { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

public abstract class LMMEngine
{
    public string Model { get; protected set; } = "";
    public string BaseUrl { get; protected set; } = "";
    public string ApiKey { get; protected set; } = "";

    /// <summary>Hard ceiling on the request body we'll even attempt to send.
    /// Most upstream LLM routers cap the wire body at ~250 MB; if our
    /// payload would exceed ~32 MB we already know it'll be rejected, so we
    /// fail fast with a clear message instead of waiting for the round
    /// trip. Tweak per provider if a known-good endpoint has a bigger
    /// envelope.</summary>
    protected const long MaxBodyBytes = 32L * 1024 * 1024;

    public abstract Task<LmmResponse> GenerateAsync(LmmRequest request, CancellationToken ct);

    /// <summary>Throw a clear <see cref="LmmProviderException"/> when the wire
    /// body is too big. Called by both engine implementations right after
    /// serializing the JSON request, before opening a TCP connection.</summary>
    protected static void EnsureBodyUnderLimit(string body)
    {
        if (body.Length <= MaxBodyBytes) return;
        var mb = body.Length / 1024d / 1024d;
        throw new LmmProviderException(
            status: 413,
            reason: "payload-too-large",
            message: $"Request body {mb:F1} MB exceeds the {MaxBodyBytes / 1024 / 1024} MB pre-flight limit. " +
                     "Long-horizon runs pile up base64 image attachments in the message history; " +
                     "LmmAgent.GetResponseAsync now compacts older images automatically, but the " +
                     "current conversation was already over budget. Try a shorter task or restart the agent.",
            body: $"payload-size={body.Length} bytes limit={MaxBodyBytes} bytes");
    }

    public static LMMEngine Create(Vantage.Models.Provider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no BaseUrl.");

        // Anthropic-shaped endpoint → Anthropic native messages API
        var isAnthropic = provider.BaseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase);
        return isAnthropic
            ? new AnthropicEngine(provider)
            : new OpenAICompatEngine(provider);
    }
}

public sealed class AnthropicEngine : LMMEngine
{
    private readonly HttpClient _http;

    public AnthropicEngine(Vantage.Models.Provider provider)
    {
        Model = string.IsNullOrWhiteSpace(provider.DefaultModel) ? "claude-sonnet-4-5" : provider.DefaultModel.Trim();
        BaseUrl = provider.BaseUrl.TrimEnd('/');
        ApiKey = provider.ApiKey.Trim();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public override async Task<LmmResponse> GenerateAsync(LmmRequest request, CancellationToken ct)
    {
        var sysMsg = request.Messages.FirstOrDefault(m => m["role"]?.GetValue<string>() == "system");
        var bodyMsgs = request.Messages.Where(m => m["role"]?.GetValue<string>() != "system").ToList();

        var body = new JsonObject
        {
            ["model"]       = request.Model,
            ["messages"]    = new JsonArray(bodyMsgs.Select(m => m.DeepClone()).ToArray()),
            ["max_tokens"]  = request.MaxTokens,
            ["temperature"] = request.Temperature
        };
        if (sysMsg is not null)
        {
            var sysContent = sysMsg["content"];
            body["system"] = sysContent is JsonArray sa
                ? new JsonArray(sa.Select(b => (JsonNode?)b?.DeepClone()).ToArray())
                : JsonValue.Create(sysContent?.ToJsonString() ?? "");
        }
        if (request.UseThinking)
        {
            body["thinking"] = new JsonObject
            {
                ["type"]          = "enabled",
                ["budget_tokens"] = 4096
            };
        }

        var bodyJson = body.ToJsonString();
        // Fail fast — don't burn a round trip on a payload the upstream
        // router will reject anyway. LmmAgent.GetResponseAsync already
        // squashes old images, so this only fires in a degenerate case.
        EnsureBodyUnderLimit(bodyJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta", "computer-use-2025-01-24");
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            // Always log the request body too so a 4xx that is "One of the
            // identified items was invalid" can be traced to the specific
            // message item the provider rejected.
            var reqBody = body.ToJsonString();
            if (reqBody.Length > 8_000) reqBody = reqBody[..8_000] + "…";
            CommonUtils.LogDiagnostic("anthropic-http-error",
                $"model={Model} status={status} body={bodyText}\n---request---\n{reqBody}");
            if (status == 429 || status == 529)
            {
                var kind = RateLimitParser.DetectKind(status, bodyText);
                var retry = RateLimitParser.ExtractRetryAfter(resp, bodyText);
                throw new LlmRateLimitException(status, retry, Model, bodyText, kind);
            }
            throw new LmmProviderException(status, resp.ReasonPhrase ?? "error",
                LmmErrorClassifier.ExtractMessage(bodyText) ?? $"HTTP {status} from Anthropic",
                bodyText);
        }

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        var sbText = new StringBuilder();
        string? thinking = null;
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "thinking")
                thinking = block.GetProperty("thinking").GetString();
            else if (type == "text")
                sbText.AppendLine(block.GetProperty("text").GetString());
        }
        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        var text = sbText.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "?";
            CommonUtils.LogDiagnostic("anthropic-empty-response",
                $"model={Model} status={(int)resp.StatusCode} stop_reason={stopReason} body={bodyText}");
        }
        return new LmmResponse
        {
            Text        = text,
            Thinking    = thinking,
            InputTokens = usage.ValueKind != JsonValueKind.Undefined ? usage.GetProperty("input_tokens").GetInt32() : 0,
            OutputTokens= usage.ValueKind != JsonValueKind.Undefined ? usage.GetProperty("output_tokens").GetInt32() : 0
        };
    }
}

public sealed class OpenAICompatEngine : LMMEngine
{
    private readonly HttpClient _http;

    public OpenAICompatEngine(Vantage.Models.Provider provider)
    {
        Model = string.IsNullOrWhiteSpace(provider.DefaultModel)
            ? "llama-3.3-70b-versatile"  // sensible Groq default
            : provider.DefaultModel.Trim();
        BaseUrl = provider.BaseUrl.TrimEnd('/');
        ApiKey = provider.ApiKey.Trim();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public override async Task<LmmResponse> GenerateAsync(LmmRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"]       = request.Model,
            ["max_tokens"]  = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["messages"]    = new JsonArray(request.Messages.Select(m => m.DeepClone()).ToArray())
        };

        var bodyJson = body.ToJsonString();
        // Fail fast — see EnsureBodyUnderLimit. Compactor in LmmAgent.
        EnsureBodyUnderLimit(bodyJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            // Always log the request body too so a 4xx that says
            // "One of the identified items was invalid" can be traced to
            // the specific message item the provider rejected.
            var reqBody = body.ToJsonString();
            if (reqBody.Length > 8_000) reqBody = reqBody[..8_000] + "…";
            CommonUtils.LogDiagnostic("openai-http-error",
                $"model={Model} status={status} body={bodyText}\n---request---\n{reqBody}");
            if (status == 429 || status == 529)
            {
                var kind = RateLimitParser.DetectKind(status, bodyText);
                var retry = RateLimitParser.ExtractRetryAfter(resp, bodyText);
                throw new LlmRateLimitException(status, retry, Model, bodyText, kind);
            }
            // Surface a cleaned-up provider error so the UI never shows
            // raw Groq JSON. We extract the human-readable `error.message`
            // from the body when present, and classify known Groq quirks
            // (notably "invalid format" / "invalid_request_error") so
            // downstream code can react rather than treat it as a fault.
            throw new LmmProviderException(status, resp.ReasonPhrase ?? "error",
                LmmErrorClassifier.ExtractMessage(bodyText) ?? $"HTTP {status} from {BaseUrl}",
                bodyText);
        }

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var msg    = choice.GetProperty("message");
        var text   = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "?";

        if (string.IsNullOrWhiteSpace(text))
        {
            CommonUtils.LogDiagnostic("openai-empty-response",
                $"model={Model} status={(int)resp.StatusCode} finish_reason={finish} body={bodyText}");
        }

        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var pt)) inTok = pt.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var ct2)) outTok = ct2.GetInt32();
        }

        return new LmmResponse { Text = text, InputTokens = inTok, OutputTokens = outTok };
    }
}