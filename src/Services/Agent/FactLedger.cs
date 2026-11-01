// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/FactLedger.cs
//
// Only the newest messages stay in the conversation, so a value read on an
// earlier page is gone unless it is written down. A real run read three figures
// correctly, lost them, and typed invented ones. The ledger keeps what the model
// recorded word for word in every prompt, and flags any number about to be typed
// that has no source, because nothing else in the agent checks content.

using System.Text;
using System.Text.RegularExpressions;

namespace Vantage.Services.Agent;

internal sealed class FactLedger
{
    private const int MaxNotes = 40;
    private const int MaxNoteLength = 240;
    private const int MaxReasoning = 12;
    private const int MaxReasoningLength = 160;
    private const int MinValueDigits = 3;

    private static readonly Regex UrlOrPath = new(@"\S*(?:://|[\\/])\S*", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\w.])\d[\d,]*(?:\.\d+)?(?!\w)", RegexOptions.Compiled);

    private readonly List<string> _notes = new();
    private readonly List<string> _reasoning = new();
    private readonly HashSet<string> _challenged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _typedWithoutSource = new(StringComparer.Ordinal);
    private bool _finishChallenged;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Numbers of three or more digits as written, keyed by their digits alone.</summary>
    internal static Dictionary<string, string> ExtractValues(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return values;
        foreach (Match match in Number.Matches(UrlOrPath.Replace(text, " ")))
        {
            var written = match.Value.TrimEnd(',');
            var key = written.Replace(",", string.Empty);
            if (key.Count(char.IsDigit) >= MinValueDigits) values[key] = written;
        }
        return values;
    }

    /// <summary>Everything an action would type, including inside a batch.</summary>
    internal static IEnumerable<string> TypedText(AgentAction action)
    {
        switch (action.Action.Trim().ToLowerInvariant())
        {
            case "batch":
                foreach (var step in action.GetActionList("steps"))
                foreach (var text in TypedText(step))
                    yield return text;
                break;
            case "type" or "type_text" or "type_window_text" or "vantage_set_clipboard":
                if (action.GetString("text") is { Length: > 0 } typed) yield return typed;
                break;
            case "set_value":
                if (action.GetString("value") is { Length: > 0 } value) yield return value;
                break;
        }
    }

    public void RecordNotes(AgentAction action)
    {
        foreach (var note in ReadNotes(action)) AddNote(note);
        if (!action.Action.Equals("batch", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var step in action.GetActionList("steps")) RecordNotes(step);
    }

    private static IEnumerable<string> ReadNotes(AgentAction action)
    {
        foreach (var note in action.GetStringList("note")) yield return note;
        if (action.GetString("note") is { Length: > 0 } single) yield return single;
        if (action.Action.Equals("save_to_knowledge", StringComparison.OrdinalIgnoreCase))
            foreach (var fact in action.GetStringList("text")) yield return fact;
    }

    private void AddNote(string note)
    {
        var text = note.Trim();
        if (text.Length == 0 || _notes.Contains(text, StringComparer.Ordinal)) return;
        _notes.Add(text.Length <= MaxNoteLength ? text : text[..MaxNoteLength]);
        while (_notes.Count > MaxNotes) _notes.RemoveAt(0);
    }

    /// <summary>
    /// Backstop for a model that forgets to note what it read: lines of its own
    /// reasoning that hold a value stay visible. They jog its memory but never
    /// count as a source, since invented numbers appear in reasoning too.
    /// </summary>
    public void RecordReasoning(int turn, string? thoughts)
    {
        if (string.IsNullOrWhiteSpace(thoughts)) return;
        foreach (var raw in thoughts.Split('\n'))
        {
            var line = raw.Trim();
            if (ExtractValues(line).Count == 0) continue;
            if (line.Length > MaxReasoningLength) line = line[..MaxReasoningLength];
            if (_reasoning.Any(existing => existing.EndsWith(line, StringComparison.Ordinal))) continue;
            _reasoning.Add($"turn {turn}: {line}");
        }
        while (_reasoning.Count > MaxReasoning) _reasoning.RemoveAt(0);
    }

    /// <summary>
    /// Returns numbers this action would type that have no source and have not
    /// been challenged before. The first attempt is held; if the model insists
    /// the value goes through and is reported as unconfirmed when the run ends,
    /// because a legitimate number must never be able to deadlock a task.
    /// </summary>
    public IReadOnlyList<string> ChallengeTyping(AgentAction action, IEnumerable<string?> otherSources)
    {
        var missing = MissingSources(action, otherSources);
        var fresh = new List<string>();
        foreach (var (key, written) in missing)
        {
            if (_challenged.Add(key)) fresh.Add(written);
        }
        if (fresh.Count > 0) return fresh;

        foreach (var (key, written) in missing) _typedWithoutSource[key] = written;
        return Array.Empty<string>();
    }

    private Dictionary<string, string> MissingSources(AgentAction action, IEnumerable<string?> otherSources)
    {
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in TypedText(action))
        foreach (var (key, written) in ExtractValues(text))
            missing[key] = written;
        if (missing.Count == 0) return missing;

        foreach (var source in _notes.Concat(otherSources))
        foreach (var key in ExtractValues(source).Keys)
            missing.Remove(key);
        return missing;
    }

    /// <summary>Values typed with no source that nothing has confirmed since.</summary>
    public IReadOnlyList<string> Unconfirmed()
    {
        var noted = new HashSet<string>(_notes.SelectMany(note => ExtractValues(note).Keys), StringComparer.Ordinal);
        return _typedWithoutSource.Where(pair => !noted.Contains(pair.Key)).Select(pair => pair.Value).ToList();
    }

    /// <summary>True the first time a finish is attempted with unconfirmed values.</summary>
    public bool ShouldChallengeFinish()
    {
        if (_finishChallenged || Unconfirmed().Count == 0) return false;
        _finishChallenged = true;
        return true;
    }

    public string BuildPromptBlock()
    {
        if (_notes.Count == 0 && _reasoning.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        if (_notes.Count > 0)
        {
            sb.AppendLine("NOTES (what you recorded with `note`; use these exact values):");
            foreach (var note in _notes) sb.Append("  - ").AppendLine(note);
        }
        if (_reasoning.Count > 0)
        {
            sb.AppendLine("EARLIER_REASONING (your past words, possibly wrong; check the page if a value matters):");
            foreach (var line in _reasoning) sb.Append("  - ").AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}
