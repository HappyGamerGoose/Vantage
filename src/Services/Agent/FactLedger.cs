// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/FactLedger.cs
//
// Keeps what has really been read, and refuses numbers that have no source.
// Real runs read figures correctly and lost them, or misread them from a shrunken
// screenshot, and then typed the wrong number and reported success. A model's own
// word proves nothing, so only text Vantage itself read counts as a source: the
// read_text action, command output, the current control names and the task.

using System.Text;
using System.Text.RegularExpressions;

namespace Vantage.Services.Agent;

internal sealed class FactLedger
{
    private const int MaxNotes = 40;
    private const int MaxNoteLength = 240;
    private const int MaxReadExcerpts = 24;
    private const int MaxExcerptLength = 420;
    private const int MinValueDigits = 3;

    private static readonly Regex UrlOrPath = new(@"\S*(?:://|[\\/])\S*", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\w.])\d[\d,]*(?:\.\d+)?(?!\w)", RegexOptions.Compiled);
    private static readonly Regex Year = new(@"^(19|20)\d\d$", RegexOptions.Compiled);

    private readonly List<string> _notes = new();
    private readonly List<string> _read = new();
    private readonly Dictionary<string, string> _typedUnconfirmed = new(StringComparer.Ordinal);
    private bool _readingUnavailable;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>
    /// Numbers of three or more digits as written, keyed by their digits alone.
    /// A plain year is left out: it is usually context rather than data, and
    /// demanding a source for it would stop ordinary tasks.
    /// </summary>
    internal static Dictionary<string, string> ExtractValues(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return values;
        foreach (Match match in Number.Matches(UrlOrPath.Replace(text, " ")))
        {
            var written = match.Value.TrimEnd(',');
            var key = written.Replace(",", string.Empty);
            if (key.Count(char.IsDigit) >= MinValueDigits && !Year.IsMatch(key)) values[key] = written;
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

    /// <summary>Text read from the window this turn: shown to the model, and a source.</summary>
    public void RecordRead(IEnumerable<string> excerpts)
    {
        foreach (var excerpt in excerpts)
        {
            var text = excerpt.Length <= MaxExcerptLength ? excerpt : excerpt[..MaxExcerptLength];
            if (!_read.Contains(text, StringComparer.Ordinal)) _read.Add(text);
        }
        while (_read.Count > MaxReadExcerpts) _read.RemoveAt(0);
    }

    /// <summary>The window has no readable text, so a screenshot is all there is.</summary>
    public void RecordReadingUnavailable() => _readingUnavailable = true;

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
    /// Numbers this action may not type, because nothing Vantage read contains
    /// them. Trying again changes nothing. The only exception is a window that
    /// has been shown to expose no text, where `unconfirmed: true` lets the value
    /// through and the run is reported as needing review.
    /// </summary>
    public IReadOnlyList<string> BlockedValues(AgentAction action, IEnumerable<string?> otherSources)
    {
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in TypedText(action))
        foreach (var (key, written) in ExtractValues(text))
            missing[key] = written;
        if (missing.Count == 0) return Array.Empty<string>();

        foreach (var source in _read.Concat(otherSources))
        foreach (var key in ExtractValues(source).Keys)
            missing.Remove(key);
        if (missing.Count == 0) return Array.Empty<string>();

        if (_readingUnavailable && action.GetBool("unconfirmed", false))
        {
            foreach (var (key, written) in missing) _typedUnconfirmed[key] = written;
            return Array.Empty<string>();
        }
        return missing.Values.ToList();
    }

    /// <summary>Values typed without a source that nothing has confirmed since.</summary>
    public IReadOnlyList<string> Unconfirmed()
    {
        var read = new HashSet<string>(_read.SelectMany(text => ExtractValues(text).Keys), StringComparer.Ordinal);
        return _typedUnconfirmed.Where(pair => !read.Contains(pair.Key)).Select(pair => pair.Value).ToList();
    }

    public string BuildPromptBlock()
    {
        if (_read.Count == 0 && _notes.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        if (_read.Count > 0)
        {
            sb.AppendLine("TEXT_READ (exact text from read_text; take values from here, digit for digit):");
            foreach (var excerpt in _read) sb.Append("  - ").AppendLine(excerpt);
        }
        if (_notes.Count > 0)
        {
            sb.AppendLine("NOTES (reminders you wrote yourself; not proof of any value):");
            foreach (var note in _notes) sb.Append("  - ").AppendLine(note);
        }
        return sb.ToString().TrimEnd();
    }
}
