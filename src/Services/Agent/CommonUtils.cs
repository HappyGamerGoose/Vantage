// SPDX-License-Identifier: MIT
// Vantage — Services/S3/CommonUtils.cs
//
// Ported from gui_agents/s3/utils/common_utils.py + formatters.py.
// Helpers for parsing JSON action responses, extracting code blocks,
// splitting thoughts/answer sections, retrying LLM calls, and flushing
// message history to fit context windows.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vantage.Common;

namespace Vantage.Services.Agent;

public static class CommonUtils
{
    private static readonly object LogLock = new();

    // Long-lived StreamWriters that hold an open file handle for the
    // duration of the agent run. Previously every LogDiagnostic /
    // LogVerbose call opened / seeked / wrote / closed the file —
    // 8 calls per step × 30+ steps × ~3 syscalls each = a real cost.
    // Writers are flushed every FlushEvery = 16 entries (a balance
    // between durability and write-amplification) and closed on
    // dispose / process exit. The 1024-byte buffer matches
    // StreamWriter's default; we let it fill before calling Flush().
    private static LogWriter? _diagWriter;
    private static LogWriter? _verboseWriter;

    private const int FlushEvery = 16;
    private const long MaxLogBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Append a single-line diagnostic to %LOCALAPPDATA%\Vantage\_diag.log.
    /// Used to capture empty LLM responses and grounding failures so we can
    /// post-mortem an agent run without re-attaching a debugger.
    /// </summary>
    public static void LogDiagnostic(string category, string content)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {category}: {Truncate(content, 400)}\n";
            var w = LazyOpenWriter(ref _diagWriter, "_diag.log");
            w.WriteLine(line);
        }
        catch { /* logging must never crash the agent */ }
    }

    /// <summary>
    /// Append a multi-line block (full prompt, full response, full verifier
    /// diff record) to a separate verbose log at the same
    /// %LOCALAPPDATA%\Vantage\ directory. Use this when you need the
    /// uncut content for debugging — the truncated single-line log above
    /// hides details like the model's reason text and the JSON block.
    /// </summary>
    public static void LogVerbose(string category, string content)
    {
        try
        {
            var block = $"--- [{DateTime.Now:HH:mm:ss.fff}] {category} ---\n" +
                        (content ?? string.Empty) +
                        (content != null && !content.EndsWith("\n") ? "\n" : "") +
                        "---\n";
            var w = LazyOpenWriter(ref _verboseWriter, "agent-verbose.log");
            w.Write(block);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Build / reuse the StreamWriter for a log file. Used both by the
    /// diagnostic + verbose APIs and the manual flush path. The writer
    /// is held open for the rest of the process lifetime so subsequent
    /// LogXxx calls don't pay the open+seek+close tax.
    /// </summary>
    private static LogWriter LazyOpenWriter(ref LogWriter? slot, string fileName)
    {
        if (slot is { Disposed: false }) return slot;
        var path = PathFor(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateIfLarge(path, MaxLogBytes);
        slot = new LogWriter(path, LogLock, FlushEvery);
        return slot;
    }

    /// <summary>
    /// Logs hold the text of whatever a run read and were never trimmed, so they
    /// only grew. The newest <paramref name="maxBytes"/> or so stay, and one older
    /// file is kept for looking back at the run before.
    /// </summary>
    internal static void RotateIfLarge(string path, long maxBytes)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > maxBytes) file.MoveTo(path + ".old", overwrite: true);
        }
        catch { /* a log still in use just rotates on a later open */ }
    }

    /// <summary>
    /// Force-flush both log streams. Called from the window-close
    /// handler so a graceful shutdown doesn't lose the last ~15 entries.
    /// </summary>
    public static void FlushLogs()
    {
        lock (LogLock)
        {
            _diagWriter?.FlushNow();
            _verboseWriter?.FlushNow();
        }
    }

    private static string PathFor(string name)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vantage");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    internal static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
    /// <summary>
    /// Extract the LAST JSON-looking block from a string. Tries in order:
    ///   1. ```json ... ``` fenced block (preferred)
    ///   2. any ``` ... ``` fenced block
    ///   3. last balanced top-level {...} object in the text (handles
    ///      models that emit plain JSON without fences)
    /// Returns empty string if nothing parseable is found.
    /// </summary>
    // Harmless slips in an otherwise clear action should not cost a turn.
    private static readonly JsonDocumentOptions ActionJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static string ExtractLastJsonBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // A fence is only a hint: models put stray closing tags inside it, which
        // once rejected the same valid action three times and stopped a run. If
        // no fence holds exactly an action, look for one anywhere in the reply.
        foreach (var pattern in new[] { "```json\\s*\\n?(.*?)```", "```\\s*\\n?(.*?)```" })
        {
            var matches = Regex.Matches(text, pattern, RegexOptions.Singleline);
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var content = matches[i].Groups[1].Value.Trim();
                if (ParseAgentAction(content) is not null) return content;
            }
        }
        return FindLastActionObject(text) ?? "";
    }

    /// <summary>The last complete JSON object in the text that is an action.</summary>
    private static string? FindLastActionObject(string text)
    {
        string? found = null;
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf('{', position);
            if (start < 0) break;
            var end = FindObjectEnd(text, start);
            if (end >= 0 && text.Substring(start, end - start + 1) is var candidate && ParseAgentAction(candidate) is not null)
            {
                found = candidate;
                position = end + 1;
            }
            else
            {
                position = start + 1;
            }
        }
        return found;
    }

    /// <summary>Index of the brace closing the object at <paramref name="start"/>, or -1.</summary>
    private static int FindObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Says what is wrong with a reply that holds no usable action.</summary>
    public static string DescribeActionProblem(string reply)
    {
        if (!reply.Contains('{')) return "it has no JSON object";
        var fenced = Regex.Match(reply, "```(?:json)?\\s*\\n?(.*?)```", RegexOptions.Singleline);
        var json = fenced.Success ? fenced.Groups[1].Value.Trim() : reply[reply.IndexOf('{')..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(json, ActionJsonOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("action", out _)
                ? "its `action` field is not a name"
                : "its JSON object has no `action` field";
        }
        catch (JsonException ex)
        {
            return $"its JSON is invalid ({ex.Message.Split(". Path")[0]})";
        }
    }
    /// <summary>
    /// Split a response into (thoughts, answer) sections. Returns the
    /// full text as `answer` and empty `thoughts` on parse failure.
    /// </summary>
    public static (string Thoughts, string Answer) SplitThoughtsAnswer(string text)
    {
        try
        {
            var thoughts = text.Split("<thoughts>")[^1].Split("</thoughts>")[0].Trim();
            var answer    = text.Split("<answer>")[^1].Split("</answer>")[0].Trim();
            return (thoughts, answer);
        }
        catch
        {
            return ("", text.Trim());
        }
    }

    /// <summary>
    /// Parse a JSON action object. Returns null on parse failure or if the
    /// JSON is missing required fields. Use the helper to validate worker
    /// output without crashing the loop.
    /// </summary>
    public static AgentAction? ParseAgentAction(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonText, ActionJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("action", out var actionProp)) return null;
            var action = actionProp.GetString();
            if (string.IsNullOrWhiteSpace(action)) return null;
            return new AgentAction { Raw = root.Clone(), Action = action };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Call the LLM with intelligent retry handling. 429 / TPM rate-limit
    /// errors are treated specially: the engine extracts the suggested wait
    /// (from the response body or Retry-After header) and we sleep that long
    /// before the next attempt, capped at 60s. Other transient errors get
    /// a short 1s backoff. Returns the empty string if all attempts fail.
    /// </summary>
    public static async Task<string> CallLlmSafeAsync(
        LmmAgent agent, double temperature = 0.0, int maxNewTokens = 4096,
        int maxRetries = 8, CancellationToken ct = default)
    {
        string lastPlan = "";
        var attemptsMade = 0;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            attemptsMade = attempt;
            try
            {
                var resp = await agent.GetResponseAsync(temperature, maxNewTokens, ct);
                if (!string.IsNullOrEmpty(resp.Text)) return resp.Text;
                lastPlan = ""; // engine returned empty content
                break;
            }
            catch (LlmRateLimitException rlex)
            {
                // Daily-quota exhaustion can't be retried inside one
                // task — same payload would just hit the same wall for
                // minutes to hours. Surface it cleanly so the user can
                // swap providers or wait.
                if (rlex.Kind == RateLimitKind.DailyQuota)
                {
                    LogDiagnostic("llm-daily-quota",
                        $"model={rlex.ModelHint} retry_after={rlex.RetryAfterSeconds:F0}s ({rlex.RetryAfterSeconds/60:F1}min)");
                    throw;
                }
                // Generic 429 with no body hint — we don't know if the wait
                // is seconds or minutes, and the default fallback of 5s
                // would otherwise chain 8 attempts × 5s = 40s of silent
                // spinning. Bail after one retry so the throttle surfaces
                // quickly and the user can decide to switch providers.
                if (rlex.Kind == RateLimitKind.Unknown && attempt >= 2)
                {
                    LogDiagnostic("llm-unknown-429-fast-fail",
                        $"model={rlex.ModelHint} attempt={attempt} msg={Truncate(rlex.Message, 160)}");
                    throw;
                }
                if (attempt >= maxRetries) throw;
                // TPM (per-minute) waits are short — honor them, capped at 60s.
                var waitMs = (int)Math.Min(rlex.RetryAfterSeconds * 1000.0, 60_000);
                LogDiagnostic("llm-ratelimit-retry",
                    $"attempt={attempt}/{maxRetries} kind={rlex.Kind} retry_after={rlex.RetryAfterSeconds:F2}s model={rlex.ModelHint}");
                await Task.Delay(waitMs, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (LmmProviderException pex)
            {
                if (ContextWindowLimits.IsContextLimitExceeded(pex.HttpStatus, pex.RawBody, pex.Message) && attempt < maxRetries)
                {
                    LogDiagnostic("llm-context-limit-exceeded",
                        $"status={pex.HttpStatus} msg={Truncate(pex.Message, 200)} initiating emergency context compaction and retrying");
                    agent.EmergencyCompactOnLimitExceeded(maxNewTokens);
                    await Task.Delay(200, ct);
                    continue;
                }

                // Terminal-ish provider error (e.g. Groq's "invalid format"
                // 4xx). Don't loop — same payload won't suddenly succeed.
                lastPlan = pex.Message;
                LogDiagnostic("llm-call-provider-error",
                    $"status={pex.HttpStatus} error={Truncate(pex.Message, 240)}");
                break;
            }
            catch (Exception ex)
            {
                lastPlan = ex.Message;
                if (attempt >= maxRetries) break;
                await Task.Delay(1000, ct);
            }
        }
        if (!string.IsNullOrEmpty(lastPlan))
            LogDiagnostic("llm-call-exhausted", $"attempts={attemptsMade} last_error={lastPlan}");
        // If we stopped because of a terminal provider error, bubble its
        // message up so the UI gets a clean, single-line explanation
        // instead of an empty plan that triggers another retry.
        if (!string.IsNullOrEmpty(lastPlan))
            throw new LmmProviderException(0, "exhausted", lastPlan, lastPlan);
        return "";
    }
}

/// <summary>
/// Thrown by LMMEngine when the provider returns HTTP 429 (rate-limited).
/// Carries the suggested wait in <see cref="RetryAfterSeconds"/> so callers
/// (CommonUtils.CallLlmSafeAsync) can honor the provider's back-off hint
/// instead of retrying in a fixed 1s window. Quality-preserving — same
/// model, same prompt, just timed correctly.
/// </summary>
public sealed class LlmRateLimitException : Exception
{
    public int HttpStatus { get; }
    public double RetryAfterSeconds { get; }
    public string ModelHint { get; }
    public RateLimitKind Kind { get; }
    public string? ResetEta { get; }

    public LlmRateLimitException(int status, double retryAfter, string modelHint, string body, RateLimitKind kind, string? resetEta = null)
        : base(BuildMessage(status, retryAfter, kind, resetEta, body))
    {
        HttpStatus = status;
        RetryAfterSeconds = retryAfter;
        ModelHint = modelHint;
        Kind = kind;
        ResetEta = resetEta;
    }

    private static string BuildMessage(int status, double retryAfter, RateLimitKind kind, string? resetEta, string body)
    {
        return kind switch
        {
            RateLimitKind.DailyQuota =>
                $"Daily quota exhausted (HTTP {status}). Resets in ~{FormatEta(retryAfter)}.\n" +
                "Switch to a different provider in AI Providers, or wait for the quota to reset.",
            RateLimitKind.MinuteQuota =>
                $"Rate-limited (HTTP {status}). Retrying in {retryAfter:F1}s.",
            RateLimitKind.ServiceUnavailable =>
                $"Service temporarily unavailable (HTTP {status}). Retrying in {retryAfter:F1}s.",
            RateLimitKind.Unknown =>
                // Generic 429 with no diagnostic body — provider didn't disclose
                // whether this is per-minute or per-day. Don't keep retrying
                // blindly; surface the throttle so the user can switch or wait.
                $"Provider throttled this request (HTTP {status}).\n" +
                "No retry-after hint was provided. Switch to a different provider " +
                $"in AI Providers, or wait a minute and try again.",
            _ => $"Rate limit ({status}); retry after {retryAfter:F2}s: {(string.IsNullOrEmpty(body) ? "" : (body.Length > 200 ? body.Substring(0, 200) + "…" : body))}"
        };
    }

    private static string FormatEta(double sec)
    {
        if (sec <= 0) return "soon";
        var t = TimeSpan.FromSeconds(sec);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)sec}s";
    }
}

public enum RateLimitKind
{
    /// <summary>Per-minute tokens (Groq TPM, 30K cap). Retry after seconds.</summary>
    MinuteQuota,
    /// <summary>Per-day tokens (Groq TPD, 500K cap). Retry after minutes-or-hours.</summary>
    DailyQuota,
    /// <summary>Service unavailable / overload. Retry after seconds.</summary>
    ServiceUnavailable,
    /// <summary>Generic 429 without diagnostic body.</summary>
    Unknown,
}

internal static class RateLimitParser
{
    private static readonly Regex GroqRetryRegex = new(
        @"Please try again in\s+(\d+(?:\.\d+)?)s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Detect which kind of rate limit the provider hit. Look at the body first.</summary>
    public static RateLimitKind DetectKind(int status, string body)
    {
        var lower = body?.ToLowerInvariant() ?? "";
        if (lower.Contains("tokens per day") || lower.Contains(" tpd ") || lower.Contains("limit 500000"))
            return RateLimitKind.DailyQuota;
        if (lower.Contains("tokens per minute") || lower.Contains(" tpm ") || lower.Contains("limit 30000"))
            return RateLimitKind.MinuteQuota;
        if (status == 529)
            return RateLimitKind.ServiceUnavailable;
        return RateLimitKind.Unknown;
    }

    /// <summary>
    /// Extract the provider's suggested wait. Tries Retry-After header
    /// first, falls back to parsing "Please try again in X.XXs" from the
    /// body (Groq format), then to a conservative 5s default. For TPD
    /// limits it returns the actual long wait (often minutes), so callers
    /// can choose to short-circuit rather than spin.
    /// </summary>
    public static double ExtractRetryAfter(HttpResponseMessage resp, string body)
    {
        if (resp.Headers.TryGetValues("Retry-After", out var values))
        {
            var first = values.FirstOrDefault();
            if (first is not null && double.TryParse(first, out var sec) && sec > 0)
                return sec;
        }
        var m = GroqRetryRegex.Match(body);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var w) && w > 0)
            return w;
        return 5.0;
    }
}

/// <summary>
/// Provider error with a human-readable Message + the raw body kept for
/// the diagnostic log. Thrown by LMMEngine for any non-2xx response other
/// than 429/529 (those become LlmRateLimitException). Carries the upstream
/// status so the UI can classify the failure correctly.
/// </summary>
public sealed class LmmProviderException : Exception
{
    public int HttpStatus { get; }
    public string RawBody { get; }

    public LmmProviderException(int status, string reason, string message, string body)
        : base(message)
    {
        HttpStatus = status;
        RawBody = body;
    }
}

/// <summary>
/// Pull the human-readable field out of a provider error body so the UI
/// never shows raw JSON. Both Anthropic and OpenAI-compatible providers
/// return <c>{"error":{"message":"..."}}</c> (or top-level <c>message</c>);
/// we try both shapes.
/// </summary>
internal static class LmmErrorClassifier
{
    public static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // OpenAI and Anthropic: { "error": { "message": ... } }
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var msg) && TextOf(msg) is { } errorText)
            {
                return errorText;
            }
            // Older shape: top-level "message"; FastAPI servers: top-level "detail".
            foreach (var field in new[] { "message", "detail" })
            {
                if (root.TryGetProperty(field, out var value) && TextOf(value) is { } text) return text;
            }
        }
        catch { /* not JSON */ }
        return null;
    }

    // A message is usually a string, but some servers send a list of validation
    // errors with the text in "msg". Read only as a string, Inception's "this
    // model does not take images" reached the user as a bare "HTTP 400".
    private static string? TextOf(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString()?.Trim();
                if (text?.StartsWith("Value error, ", StringComparison.Ordinal) == true) text = text["Value error, ".Length..];
                return string.IsNullOrWhiteSpace(text) ? null : text;
            case JsonValueKind.Array:
                var parts = value.EnumerateArray().Select(TextOf).Where(part => part is not null).Distinct().ToList();
                return parts.Count == 0 ? null : string.Join("; ", parts);
            case JsonValueKind.Object:
                if (value.TryGetProperty("msg", out var msg)) return TextOf(msg);
                if (value.TryGetProperty("message", out var message)) return TextOf(message);
                return null;
            default:
                return null;
        }
    }

    /// <summary>True when the provider refused a request because the model does not take images.</summary>
    public static bool RejectsImages(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var lower = body.ToLowerInvariant();
        var aboutImages = lower.Contains("image") || lower.Contains("vision") || lower.Contains("multimodal");
        var refused = lower.Contains("not support") || lower.Contains("unsupported") || lower.Contains("does not accept");
        // A picture in the wrong format or size is a different problem.
        var aboutOnePicture = lower.Contains("format") || lower.Contains("too large") || lower.Contains("size");
        return aboutImages && refused && !aboutOnePicture;
    }

    /// <summary>
    /// What to tell the user about a refused request: a plain explanation when
    /// the model does not take images, otherwise the provider's own message.
    /// </summary>
    public static string? Describe(string body, string model) =>
        RejectsImages(body)
            ? $"{model} does not accept images. Act sends a screenshot every turn so the model can see your screen, " +
              "so it needs a model that accepts images: pick one in the model menu, or turn Act off to chat with this one. " +
              "If this model's Vision setting on its provider card is Force Yes, set it back to Auto."
            : ExtractMessage(body);
}

public sealed class AgentAction
{
    public required string Action { get; init; }
    public required JsonElement Raw { get; init; }

    public string? GetString(string field) =>
        Raw.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int? GetInt(string field) =>
        Raw.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    public bool GetBool(string field, bool fallback = false) =>
        Raw.TryGetProperty(field, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    public List<string> GetStringList(string field)
    {
        var result = new List<string>();
        if (!Raw.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            }
        return result;
    }

    public List<AgentAction> GetActionList(string field)
    {
        var result = new List<AgentAction>();
        if (!Raw.TryGetProperty(field, out var values) || values.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("action", out var actionValue)
                || actionValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var actionName = actionValue.GetString();
            if (!string.IsNullOrWhiteSpace(actionName))
                result.Add(new AgentAction { Action = actionName, Raw = value.Clone() });
        }
        return result;
    }

    public (int X, int Y)? GetPoint(string xField = "x", string yField = "y")
    {
        var x = GetInt(xField);
        var y = GetInt(yField);
        if (x is not null && y is not null) return (x.Value, y.Value);
        if (Raw.TryGetProperty("coordinate", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2)
        {
            var arr = c.EnumerateArray().ToArray();
            if (arr[0].ValueKind == JsonValueKind.Number && arr[1].ValueKind == JsonValueKind.Number)
                return (arr[0].GetInt32(), arr[1].GetInt32());
        }
        if (Raw.TryGetProperty("point", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
        {
            var arr = p.EnumerateArray().ToArray();
            if (arr[0].ValueKind == JsonValueKind.Number && arr[1].ValueKind == JsonValueKind.Number)
                return (arr[0].GetInt32(), arr[1].GetInt32());
        }
        return null;
    }
}
