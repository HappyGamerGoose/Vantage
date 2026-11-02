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
    private const int MaxReadExcerpts = 60;
    private const int MaxExcerptsPerPage = 12;
    private const int MaxExcerptLength = 420;
    private const int MinValueDigits = 3;
    private const int MinNoisyDecimals = 9;
    private const decimal NoiseTolerance = 0.000000001m;

    private static readonly Regex UrlOrPath = new(@"\S*(?:://|[\\/])\S*", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\w.])\d[\d,]*(?:\.\d+)?(?!\w)", RegexOptions.Compiled);
    private static readonly Regex ShellWrite = new(
        @"\b(?:Set-Content|Add-Content|Out-File|Set-Clipboard|WriteAllText|WriteAllLines|AppendAllText)\b|New-Item\b.*-Value\b|(?<![\d&])>{1,2}\s*[\w""'$\\.]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Year = new(@"^(19|20)\d\d$", RegexOptions.Compiled);
    private static readonly Regex BrowserSuffix = new(
        @"(?:\s+and \d+ more pages?)?(?:\s+-\s+(?:Personal|Work|Profile \d+))?\s+-\s+(?:Microsoft Edge|Google Chrome|Mozilla Firefox)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record ReadExcerpt(string Page, string Text);

    private readonly List<string> _notes = new();
    private readonly List<ReadExcerpt> _read = new();

    // A long task reads more than the prompt can show. Five articles once pushed
    // out the first one's figures before they were typed, so correct values were
    // refused and read again until the run gave up. What was read stays a source.
    private readonly HashSet<string> _readValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
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
            if (key.Count(char.IsDigit) >= MinValueDigits && !Year.IsMatch(key)) values[Canonical(key)] = written;
        }
        return values;
    }

    // 237.860 and 237.86 are the same number.
    private static string Canonical(string digits) =>
        digits.Contains('.') ? digits.TrimEnd('0').TrimEnd('.') : digits;

    /// <summary>
    /// Floating-point output such as 237.860000000001 means 237.86, and refusing
    /// that once blocked a correct answer. Only true noise qualifies: the value
    /// must sit within a billionth of a shorter number, so 237.87 still fails.
    /// </summary>
    internal static string? WithoutFloatingPointNoise(string key)
    {
        var point = key.IndexOf('.');
        if (point < 0 || key.Length - point - 1 < MinNoisyDecimals) return null;
        if (!decimal.TryParse(key, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        for (var decimals = 0; decimals < MinNoisyDecimals; decimals++)
        {
            var rounded = Math.Round(value, decimals);
            if (Math.Abs(value - rounded) < NoiseTolerance)
                return Canonical(rounded.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture));
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="typed"/> is <paramref name="source"/> rounded to
    /// fewer decimal places, either way a halfway value can go: PowerShell's
    /// [math]::Round rounds 12.25 to 12.2, most people to 12.3. A whole number
    /// does not count, so 238 is still refused where only 237.86 was read.
    /// </summary>
    internal static bool IsRoundingOf(string typed, string source)
    {
        var point = typed.IndexOf('.');
        if (point < 0) return false;
        var decimals = typed.Length - point - 1;
        var sourcePoint = source.IndexOf('.');
        if (sourcePoint < 0 || source.Length - sourcePoint - 1 <= decimals) return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!decimal.TryParse(typed, System.Globalization.NumberStyles.Float, culture, out var shown)
            || !decimal.TryParse(source, System.Globalization.NumberStyles.Float, culture, out var exact))
            return false;
        return Math.Round(exact, decimals, MidpointRounding.AwayFromZero) == shown
            || Math.Round(exact, decimals, MidpointRounding.ToEven) == shown;
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
            case "done":
                // Telling the user a number is no safer than typing it.
                if (action.GetString("answer") is { Length: > 0 } answer) yield return answer;
                break;
            case "run_powershell":
                // Writing a file from the shell is typing by another route.
                if (action.GetString("command") is { Length: > 0 } command && ShellWrite.IsMatch(command)) yield return command;
                break;
        }
    }

    /// <summary>The page a window title names, without the browser's own words.</summary>
    internal static string PageLabel(string? windowTitle)
    {
        var title = (windowTitle ?? string.Empty).Replace("​", string.Empty).Trim();
        title = BrowserSuffix.Replace(title, string.Empty);
        return title.Length == 0 ? "this window" : title;
    }

    /// <summary>
    /// Text read from the window this turn: shown to the model, and a source.
    /// Returns how many passages have not been read before in this run.
    /// </summary>
    public int RecordRead(IEnumerable<string> excerpts, string? windowTitle = null)
    {
        var page = PageLabel(windowTitle);
        var added = 0;
        foreach (var excerpt in excerpts)
        {
            _readValues.UnionWith(ExtractValues(excerpt).Keys);
            if (_seen.Add($"{page}\n{excerpt}")) added++;

            // Reading a passage again makes it the newest, so it is the last to go.
            var entry = new ReadExcerpt(page, excerpt.Length <= MaxExcerptLength ? excerpt : excerpt[..MaxExcerptLength]);
            _read.Remove(entry);
            _read.Add(entry);
        }

        // One long page must not push the other pages out of view.
        while (_read.Count(entry => entry.Page == page) > MaxExcerptsPerPage)
            _read.RemoveAt(_read.FindIndex(entry => entry.Page == page));
        while (_read.Count > MaxReadExcerpts) _read.RemoveAt(0);
        return added;
    }

    /// <summary>
    /// Command output stays a source for the whole run too, however many later
    /// commands push it out of the short list the action layer keeps.
    /// </summary>
    public void RememberOutput(IEnumerable<string> outputs)
    {
        foreach (var output in outputs)
        {
            if (_seen.Add($"output\n{output}")) _readValues.UnionWith(ExtractValues(output).Keys);
        }
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

        var sources = _readValues.Concat(otherSources.SelectMany(source => ExtractValues(source).Keys)).ToList();
        foreach (var key in sources)
        {
            missing.Remove(key);
            if (WithoutFloatingPointNoise(key) is { } clean) missing.Remove(clean);
        }
        // Calculator showed 325.2860638931146 and the task wanted one decimal place.
        // Refusing 325.3 sent the model to work every density out again in PowerShell.
        foreach (var key in missing.Keys.ToList())
        {
            if (sources.Any(source => IsRoundingOf(key, source))) missing.Remove(key);
        }
        if (missing.Count == 0) return Array.Empty<string>();

        if (_readingUnavailable && action.GetBool("unconfirmed", false))
        {
            foreach (var (key, written) in missing) _typedUnconfirmed[key] = written;
            return Array.Empty<string>();
        }
        return missing.Values.ToList();
    }

    /// <summary>Values typed without a source that nothing has confirmed since.</summary>
    public IReadOnlyList<string> Unconfirmed() =>
        _typedUnconfirmed.Where(pair => !_readValues.Contains(pair.Key)).Select(pair => pair.Value).ToList();

    public string BuildPromptBlock()
    {
        if (_read.Count == 0 && _notes.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        if (_read.Count > 0)
        {
            sb.AppendLine("TEXT_READ (exact text from read_text, by page; take values from here, digit for digit, and do not read again what is already here):");
            foreach (var page in _read.GroupBy(entry => entry.Page))
            {
                sb.Append("  From \"").Append(page.Key).AppendLine("\":");
                foreach (var entry in page) sb.Append("    - ").AppendLine(entry.Text);
            }
        }
        if (_notes.Count > 0)
        {
            sb.AppendLine("NOTES (reminders you wrote yourself; not proof of any value):");
            foreach (var note in _notes) sb.Append("  - ").AppendLine(note);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// A task can name the app that must do the work. Typing the sums into Calculator
/// and then taking the answers from PowerShell passed every check yet ignored
/// the instruction, so the result has to be read from Calculator itself.
/// </summary>
internal static class RequiredToolUse
{
    public static bool CalculatorRequired(string? goal) =>
        goal is not null && goal.Contains("calculator", StringComparison.OrdinalIgnoreCase);

    public static bool IsCalculatorWindow(string? title) =>
        title is not null && title.Contains("calculator", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sums the action types into Calculator, in order. Calculator shows only
    /// the latest answer, and Gemini 3.8 Flash typed four sums in a row and read
    /// only the last, so three of five answers were never taken from Calculator.
    /// </summary>
    public static List<string> CalculatorSumsTyped(AgentAction action, string? foregroundTitle)
    {
        var sums = new List<string>();
        var inCalculator = IsCalculatorWindow(foregroundTitle);
        var steps = action.Action.Trim().Equals("batch", StringComparison.OrdinalIgnoreCase)
            ? action.GetActionList("steps") : new List<AgentAction> { action };
        foreach (var step in steps)
        {
            var name = step.Action.Trim().ToLowerInvariant();
            if (name is "focus_app" or "launch_app" or "focus_window" or "activate_window")
            {
                var app = step.GetString("title") ?? step.GetString("executable") ?? step.GetString("app") ?? step.GetString("name") ?? string.Empty;
                inCalculator = app.Contains("calc", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (name is not ("type" or "type_text" or "type_window_text")) continue;
            if (step.GetString("text") is not { } text || !text.Contains('=')) continue;
            var target = step.GetString("target_title") ?? step.GetString("window_title");
            if (target is not null ? IsCalculatorWindow(target) : inCalculator) sums.Add(text.Trim());
        }
        return sums;
    }

    // "Use Notepad to save", "in Notepad, type": Notepad is the tool. "Close Notepad" alone is not.
    private static readonly Regex NotepadAsTool = new(
        @"\b(?:use|using|with|in|via|through)\s+(?:the\s+)?(?:windows\s+)?notepad\b|\bnotepad\s+to\s+(?:save|write|create|type|make)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Text inside quotes is a value, not a command: "a -> b" redirects nothing.
    private static readonly Regex QuotedText = new(
        @"'(?:[^']|'')*'|""(?:[^""`]|`.)*""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex FileWrite = new(
        @"\b(?:Set-Content|Add-Content|Out-File|Tee-Object)\b" +
        @"|\bNew-Item\b[^;|\r\n]*\s-Value\b" +
        @"|\[(?:System\.)?IO\.File\]::(?:Write|Append)" +
        @"|\bIO\.StreamWriter\b" +
        @"|(?<![-=<>])>{1,2}\s*(?!\$null\b|&|nul\b)(?=[""'$\w.\\/~])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A task that names Notepad wants the files written there. Gemini 3.8 Flash
    /// planned to write all five country files with Set-Content and to open and
    /// close Notepad only "to satisfy the app requirement".
    /// </summary>
    public static bool NotepadRequired(string? goal) => goal is not null && NotepadAsTool.IsMatch(goal);

    /// <summary>True when the action, or any step of a batch, runs PowerShell that writes a file.</summary>
    public static bool WritesFileWithPowerShell(AgentAction action)
    {
        var name = action.Action.Trim().ToLowerInvariant();
        if (name == "batch") return action.GetActionList("steps").Any(WritesFileWithPowerShell);
        return name == "run_powershell" && PowerShellWritesFile(action.GetString("command"));
    }

    /// <summary>Folders, zips, reads and sums pass; Set-Content, Out-File, &gt; and the like do not.</summary>
    public static bool PowerShellWritesFile(string? command) =>
        !string.IsNullOrWhiteSpace(command) && FileWrite.IsMatch(QuotedText.Replace(command, "\"\""));
}

/// <summary>
/// The clipboard belongs to the user until this run puts something there. A
/// blocked write once led to Ctrl+V pasting whatever the user had copied, which
/// could as easily have been a password.
/// </summary>
internal static class ClipboardPolicy
{
    private static readonly string[] ClipboardWords = { "clipboard", "paste", "pasting", "copied" };

    public static bool GoalAllowsUserClipboard(string? goal) =>
        goal is not null && ClipboardWords.Any(word => goal.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Follows the action in order. Returns whether it would paste something
    /// this run did not place, and whether the clipboard is the run's own after it.
    /// </summary>
    public static (bool PastesForeignContent, bool OursAfter) Evaluate(AgentAction action, bool oursBefore)
    {
        var name = action.Action.Trim().ToLowerInvariant();
        if (name == "batch")
        {
            var ours = oursBefore;
            foreach (var step in action.GetActionList("steps"))
            {
                var (foreign, after) = Evaluate(step, ours);
                if (foreign) return (true, ours);
                ours = after;
            }
            return (false, ours);
        }

        if (name == "vantage_set_clipboard") return (false, true);
        if (name == "run_powershell"
            && (action.GetString("command") ?? string.Empty).Contains("Set-Clipboard", StringComparison.OrdinalIgnoreCase))
            return (false, true);

        var chord = name switch
        {
            "key" => string.Join("+", action.GetStringList("keys")),
            "press_key" or "press_window_key" => action.GetString("combo") ?? string.Empty,
            _ => string.Empty,
        };
        var keys = chord.ToLowerInvariant().Replace(" ", string.Empty).Replace("control", "ctrl")
            .Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (keys.Length == 2 && keys.Contains("ctrl"))
        {
            if (keys.Contains("c") || keys.Contains("x")) return (false, true);
            if (keys.Contains("v")) return (!oursBefore, oursBefore);
        }
        return (false, oursBefore);
    }
}
