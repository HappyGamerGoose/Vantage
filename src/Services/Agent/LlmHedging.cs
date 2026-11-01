// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/LlmHedging.cs
//
// Provider latency is heavy-tailed: logged runs had a 2 s norm with calls of
// 12-42 s mixed in, and those few calls were most of the run's total time. When
// a call has taken far longer than this model's normal, a second identical
// request goes out and whichever answers first wins. A healthy call never
// sends a second request, so the extra cost is limited to the slow outliers.

using System.Collections.Concurrent;

namespace Vantage.Services.Agent;

internal static class LlmLatency
{
    private const int WindowSize = 15;
    private const int MinSamples = 3;
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public readonly Queue<long> Samples = new();
        public DateTimeOffset NoHedgeUntil = DateTimeOffset.MinValue;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    public static void Record(string key, long elapsedMs)
    {
        var entry = Entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            entry.Samples.Enqueue(elapsedMs);
            while (entry.Samples.Count > WindowSize) entry.Samples.Dequeue();
        }
    }

    /// <summary>A throttled provider must not receive extra traffic.</summary>
    public static void NoteRateLimited(string key)
    {
        var entry = Entries.GetOrAdd(key, _ => new Entry());
        lock (entry) entry.NoHedgeUntil = DateTimeOffset.UtcNow + RateLimitCooldown;
    }

    /// <summary>When to send the second request, or null to send only one.</summary>
    public static TimeSpan? HedgeDelay(string key)
    {
        if (!Entries.TryGetValue(key, out var entry)) return null;
        lock (entry)
        {
            if (DateTimeOffset.UtcNow < entry.NoHedgeUntil || entry.Samples.Count < MinSamples) return null;
            var sorted = entry.Samples.OrderBy(sample => sample).ToList();
            return DelayForMedian(sorted[sorted.Count / 2]);
        }
    }

    internal static TimeSpan DelayForMedian(long medianMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(medianMs * 3, 6_000, 25_000));
}

internal static class HedgedRequest
{
    /// <summary>
    /// Runs <paramref name="send"/>, adding a second attempt after
    /// <paramref name="delay"/> if the first has not finished. The first
    /// success wins and the other is cancelled; a failure only surfaces once
    /// no attempt can still succeed.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> send,
        TimeSpan? delay,
        CancellationToken ct,
        Action? onHedged = null)
    {
        if (delay is null) return await send(ct);

        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = send(firstCts.Token);

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay.Value, timerCts.Token);
        if (await Task.WhenAny(first, timer) == first)
        {
            timerCts.Cancel();
            return await first;
        }
        ct.ThrowIfCancellationRequested();

        onHedged?.Invoke();
        using var secondCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var second = send(secondCts.Token);

        var winner = await Task.WhenAny(first, second);
        var other = winner == first ? second : first;
        if (winner.IsCompletedSuccessfully)
        {
            (winner == first ? secondCts : firstCts).Cancel();
            Observe(other);
            return await winner;
        }

        // The quicker attempt failed; the slower one may still answer.
        ct.ThrowIfCancellationRequested();
        try
        {
            return await other;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return await winner;
        }
    }

    private static void Observe<T>(Task<T> task) =>
        _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
