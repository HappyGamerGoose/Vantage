// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Titles.cs
//
// Auto-naming logic for conversations. Splits the first user message into
// tokens, drops stop-words, and surfaces the longest surviving noun as the
// sidebar title. Without this, every chat would read "New conversation"
// until the user manually renames it — disorienting for long sessions.

using System.IO;
using System.Text;
using Vantage.Models;

namespace Vantage;

public sealed partial class MainWindow
{
    private void ApplyTitleFromMessage(Conversation conversation, ChatMessage message)
    {
        if (conversation.Title != "New conversation" && conversation.Title != "Welcome to Vantage")
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            conversation.Title = MakeConversationTitle(message.Text);
        }
        else if (!string.IsNullOrWhiteSpace(message.ImagePath))
        {
            conversation.Title = Path.GetFileName(message.ImagePath);
        }
    }

    /// <summary>
    /// Derive a single important word from the first user message and use
    /// it as the conversation title. Falls through to a short text snippet
    /// if the prompt has no usable nouns.
    /// </summary>
    private static string MakeConversationTitle(string text)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // English common words to strip when hunting for a topic word
            "a", "an", "the", "and", "or", "but", "if", "is", "are", "was", "were",
            "to", "of", "in", "on", "at", "by", "for", "with", "from", "as", "into",
            "this", "that", "these", "those", "i", "you", "we", "they", "it", "he", "she",
            "my", "your", "our", "their", "its", "do", "does", "did", "have", "has", "had",
            "open", "close", "click", "type", "send", "find", "search", "show", "tell",
            "give", "make", "let", "now", "then", "can", "could", "would", "should",
            "will", "about", "just", "some", "any", "all", "every", "very", "really",
            "please", "thanks", "open", "using", "use", "want", "need"
        };

        // Tokenize: letters, digits, and underscore count as part of a
        // word; everything else is a separator. This avoids the period in
        // "e.g." winning over real keywords.
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                current.Append(c);
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        // First, try the longest non-stopword word. Subjective "most
        // important keyword" — longer tokens in English tend to be content
        // words, while function words are short and get filtered by either
        // the stopword list or a minimum-length rule.
        string? chosen = null;
        foreach (var tok in tokens)
        {
            if (tok.Length < 3) continue;
            if (stopwords.Contains(tok)) continue;
            if (chosen is null || tok.Length > chosen.Length) chosen = tok;
        }

        // If the prompt has nothing useful (e.g. "do it"), use a short
        // readable snippet so the conversation still has a recognizable
        // name in the sidebar.
        if (string.IsNullOrEmpty(chosen))
        {
            var snippet = string.Join(' ', tokens).Trim();
            if (snippet.Length > 28) snippet = snippet.Substring(0, 28) + "…";
            return string.IsNullOrEmpty(snippet) ? "Chat" : snippet;
        }

        // Capitalize the first letter so it looks like a real title.
        return char.ToUpperInvariant(chosen[0]) + chosen.Substring(1);
    }
}
