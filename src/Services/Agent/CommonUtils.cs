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
    /// diff record) to a separate, unbounded verbose log at the same
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
        slot = new LogWriter(path, LogLock, FlushEvery);
        return slot;
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
    public static string ExtractLastJsonBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var jsonMatches = Regex.Matches(text, "```json\\s*\\n?(.*?)```", RegexOptions.Singleline);
        if (jsonMatches.Count > 0) return jsonMatches[^1].Groups[1].Value.Trim();

        var anyMatches = Regex.Matches(text, "```\\s*\\n?(.*?)```", RegexOptions.Singleline);
        if (anyMatches.Count > 0) return anyMatches[^1].Groups[1].Value.Trim();

        // Fallback: walk through the text from the end, find a '{' that
        // opens a balanced object containing "action":
        var last = ExtractLastBalancedObject(text);
        if (!string.IsNullOrEmpty(last) && last.IndexOf("\"action\"", StringComparison.Ordinal) >= 0)
            return last;

        return "";
    }

    /// <summary>
    /// Find the last top-level balanced {...} object in a string by
    /// scanning from the end. Skips braces inside strings.
    /// </summary>
    private static string? ExtractLastBalancedObject(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] != '}') continue;
            var depth = 1;
            var inStr = false;
            var esc = false;
            for (var j = i - 1; j >= 0; j--)
            {
                var c = text[j];
                if (esc) { esc = false; continue; }
                if (c == '\\' && inStr) { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '}') depth++;
                else if (c == '{')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // candidate spans [j..i]
                        var candidate = text.Substring(j, i - j + 1);
                        try
                        {
                            using var _ = JsonDocument.Parse(candidate);
                            return candidate;
                        }
                        catch
                        {
                            // not valid JSON — keep scanning
                            break;
                        }
                    }
                }
            }
        }
        return null;
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
            using var doc = JsonDocument.Parse(jsonText);
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
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
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
            LogDiagnostic("llm-call-exhausted", $"attempts={maxRetries} last_error={lastPlan}");
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
            // Groq / OpenAI shape: { "error": { "message": "..." } }
            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
            // OpenAI alternate: top-level "message" (older shape)
            if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
            {
                return m2.GetString();
            }
            // Anthropic shape: { "type":"error", "error": { "type":..., "message": ... } }
            if (root.TryGetProperty("type", out _) &&
                root.TryGetProperty("error", out var e2) &&
                e2.ValueKind == JsonValueKind.Object &&
                e2.TryGetProperty("message", out var am) &&
                am.ValueKind == JsonValueKind.String)
            {
                return am.GetString();
            }
        }
        catch { /* not JSON */ }
        return null;
    }
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
}
