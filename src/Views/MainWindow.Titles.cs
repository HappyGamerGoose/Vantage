// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Titles.cs
//
// Auto-naming logic for conversations: the first words of the first user
// message. Without this, every chat would read "New conversation" until the
// user manually renames it — disorienting for long sessions.

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
    /// The opening words of the first user message, cut at a word. Picking the
    /// longest word named a long task after whatever happened to be longest, such
    /// as "UNCONFIRMED", which said nothing about it.
    /// </summary>
    private static string MakeConversationTitle(string text)
    {
        const int maxLength = 36;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Chat";

        var title = new StringBuilder();
        var used = 0;
        foreach (var word in words)
        {
            if (title.Length + 1 + word.Length > maxLength) break;
            if (title.Length > 0) title.Append(' ');
            title.Append(word);
            used++;
        }

        // A single word too long to fit is cut instead.
        var result = used > 0 ? title.ToString() : words[0][..maxLength];
        if (used < words.Length) result = result.TrimEnd(',', ';', ':', '-', '.') + "…";

        // Capitalize the first letter so it looks like a real title.
        return char.ToUpperInvariant(result[0]) + result[1..];
    }
}
