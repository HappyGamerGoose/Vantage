// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/PageTextReader.cs
//
// Reads a window's actual text through the accessibility API. A screenshot is
// shrunk before the model sees it, and small digits read from one have been
// wrong even when the same model got them right a run earlier. The text itself
// has no such problem: a 189,000-character Wikipedia article came back in 0.1 s.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UIAutomation;

namespace Vantage.Services.Agent;

internal static class PageTextReader
{
    public const int MinUsableLength = 20;

    private const int DocumentControlType = 50030;
    private const int TextControlType = 50020;
    private const int EditControlType = 50004;
    private const int ControlTypePropertyId = 30003;
    private const int NamePropertyId = 30005;
    private const int ValuePropertyId = 30045;
    private const int TextPatternId = 10014;
    private const int MaxExcerptsPerSearch = 4;
    private const int ExcerptContext = 180;
    private const int MaxWordStretch = 60;
    private const int UnfilteredLength = 1500;
    private const int MaxFallbackLength = 200_000;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The window's text, or an empty string if it exposes none in time.</summary>
    public static async Task<string> ReadAsync(IntPtr window, TimeSpan budget)
    {
        if (window == IntPtr.Zero) return string.Empty;
        try
        {
            return await Task.Run(() => Read(window)).WaitAsync(budget);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Read(IntPtr window)
    {
        IUIAutomation? automation = null;
        IUIAutomationElement? root = null;
        try
        {
            automation = new CUIAutomation();
            root = automation.ElementFromHandle(window);
            var document = ReadDocument(automation, root);
            return document.Length >= MinUsableLength ? document : ReadElementText(automation, root);
        }
        finally
        {
            Release(root);
            Release(automation);
        }
    }

    // Web pages and rich editors expose everything through one text pattern.
    private static string ReadDocument(IUIAutomation automation, IUIAutomationElement root)
    {
        IUIAutomationCondition? condition = null;
        IUIAutomationElement? document = null;
        object? pattern = null;
        IUIAutomationTextRange? range = null;
        try
        {
            condition = automation.CreatePropertyCondition(ControlTypePropertyId, DocumentControlType);
            document = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (document is null) return string.Empty;
            pattern = document.GetCurrentPattern(TextPatternId);
            if (pattern is not IUIAutomationTextPattern text) return string.Empty;
            range = text.DocumentRange;
            return range.GetText(-1) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Release(range);
            Release(pattern);
            Release(document);
            Release(condition);
        }
    }

    // Native windows usually have no document, but their labels and fields do
    // carry the text shown on screen.
    private static string ReadElementText(IUIAutomation automation, IUIAutomationElement root)
    {
        IUIAutomationCacheRequest? cache = null;
        IUIAutomationCondition? isText = null, isEdit = null, either = null;
        IUIAutomationElementArray? found = null;
        try
        {
            cache = automation.CreateCacheRequest();
            cache.AddProperty(NamePropertyId);
            cache.AddProperty(ValuePropertyId);
            isText = automation.CreatePropertyCondition(ControlTypePropertyId, TextControlType);
            isEdit = automation.CreatePropertyCondition(ControlTypePropertyId, EditControlType);
            either = automation.CreateOrCondition(isText, isEdit);
            found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, either, cache);

            var sb = new StringBuilder();
            for (var i = 0; i < found.Length && sb.Length < MaxFallbackLength; i++)
            {
                IUIAutomationElement? element = null;
                try
                {
                    element = found.GetElement(i);
                    if (element.CachedName is { Length: > 0 } name) sb.AppendLine(name);
                    if (element.GetCachedPropertyValue(ValuePropertyId) is string { Length: > 0 } value) sb.AppendLine(value);
                }
                catch
                {
                    // UIA trees are live; an element can vanish mid-enumeration.
                }
                finally
                {
                    Release(element);
                }
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Release(found);
            Release(either);
            Release(isEdit);
            Release(isText);
            Release(cache);
        }
    }

    /// <summary>
    /// The text around each search term, with white space collapsed. With no
    /// search terms, the start of the text.
    /// </summary>
    internal static List<string> ExtractExcerpts(string text, IReadOnlyList<string> searches) =>
        ExtractExcerpts(text, searches, null, out _);

    /// <summary>
    /// With <paramref name="progress"/>, each search carries on after the matches
    /// it already returned. Asking again used to return the same first matches,
    /// and a model looking for one further down asked 20 times.
    /// </summary>
    internal static List<string> ExtractExcerpts(string text, IReadOnlyList<string> searches, IDictionary<string, int>? progress, out bool moreMatches)
    {
        moreMatches = false;
        var clean = Whitespace.Replace(text.Replace('￼', ' '), " ").Trim();
        var excerpts = new List<string>();
        if (clean.Length == 0) return excerpts;
        if (searches.Count == 0)
        {
            excerpts.Add(clean.Length <= UnfilteredLength ? clean : clean[..UnfilteredLength]);
            return excerpts;
        }

        foreach (var search in searches.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var term = search.Trim();
            var key = term.ToLowerInvariant();
            var position = 0;
            if (progress is not null && progress.TryGetValue(key, out var reached)) position = Math.Min(reached, clean.Length);
            var found = 0;
            for (var hits = 0; hits < MaxExcerptsPerSearch; hits++)
            {
                var index = clean.IndexOf(term, position, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var start = WordStart(clean, Math.Max(0, index - (ExcerptContext / 3)));
                var end = WordEnd(clean, Math.Min(clean.Length, index + term.Length + ExcerptContext));
                excerpts.Add(clean[start..end]);
                position = end;
                found++;
            }
            if (progress is null) continue;

            // Once a search runs out it starts over, in case the earliest matches
            // have since dropped out of TEXT_READ.
            if (found == 0) progress.Remove(key);
            else progress[key] = position;
            if (clean.IndexOf(term, position, StringComparison.OrdinalIgnoreCase) >= 0) moreMatches = true;
        }
        return excerpts;
    }

    // An excerpt must not begin or end inside a word. Cutting through 2,780,085
    // left the model with 80,085, which it rightly distrusted and then replaced
    // with a guess, and the fragment would have counted as a value that was read.
    private static int WordStart(string text, int start)
    {
        for (var i = start; i > 0 && start - i < MaxWordStretch; i--)
        {
            if (text[i - 1] == ' ') return i;
        }
        return start;
    }

    private static int WordEnd(string text, int end)
    {
        for (var i = end; i < text.Length && i - end < MaxWordStretch; i++)
        {
            if (text[i] == ' ') return i;
        }
        return end == text.Length || text.Length - end < MaxWordStretch ? text.Length : end;
    }

    private static void Release(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        catch
        {
        }
    }
}
