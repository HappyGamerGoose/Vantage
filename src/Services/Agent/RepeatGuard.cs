// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/RepeatGuard.cs
//
// Stops a model that keeps sending the same action. Checking the screen cannot
// catch this: a click that opens and closes a dropdown changes the screen every
// time, so 146 identical clicks all counted as progress, and a warning in the
// prompt was ignored as well. This counts the actions themselves.

using System.Text;
using System.Text.Json;

namespace Vantage.Services.Agent;

internal sealed class RepeatGuard
{
    public const int RefuseAt = 5;
    public const int StopAt = 10;

    // A loop can alternate, so a repeating run of up to this many actions counts too.
    private const int LongestCycle = 3;
    private const int KeptActions = LongestCycle * (StopAt + 1);

    // Fields that describe the model's expectations rather than the action.
    private static readonly HashSet<string> IgnoredFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "expected_state", "confidence", "task_update", "notes", "thought", "thoughts",
    };

    private readonly List<string> _recent = new();

    /// <summary>Records the action and returns how many times in a row it has now come up.</summary>
    public int Register(AgentAction action)
    {
        // Waiting and scrolling have no target to get wrong and repeat honestly,
        // and the check for no visible progress already covers a dead scroll.
        if (action.Action.Trim().ToLowerInvariant() is "done" or "fail" or "wait" or "scroll" or "scroll_xy") return 0;

        _recent.Add(KeyFor(action));
        if (_recent.Count > KeptActions) _recent.RemoveAt(0);

        var repeats = 1;
        for (var cycle = 1; cycle <= LongestCycle; cycle++) repeats = Math.Max(repeats, CyclesAtEnd(cycle));
        return repeats;
    }

    /// <summary>
    /// The last action moved the task on, so it starts a new count. A read_text
    /// that keeps finding new passages looks like a repeat but is doing what the
    /// app told it to do.
    /// </summary>
    public void Clear() => _recent.Clear();

    private int CyclesAtEnd(int length)
    {
        var cycles = 1;
        while (_recent.Count >= length * (cycles + 1))
        {
            for (var i = 1; i <= length; i++)
            {
                if (_recent[^i] != _recent[^(i + (length * cycles))]) return cycles;
            }
            cycles++;
        }
        return cycles;
    }

    private static string KeyFor(AgentAction action)
    {
        var key = new StringBuilder(action.Action.Trim().ToLowerInvariant());
        if (action.Raw.ValueKind != JsonValueKind.Object) return key.ToString();
        foreach (var field in action.Raw.EnumerateObject().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (IgnoredFields.Contains(field.Name) || field.NameEquals("action")) continue;
            key.Append('|').Append(field.Name).Append('=').Append(field.Value.GetRawText());
        }
        return key.ToString();
    }
}
