namespace Vantage.Models;

public sealed class SearchResult
{
    public string ConversationId { get; set; } = string.Empty;

    public string ConversationTitle { get; set; } = string.Empty;

    public ChatMessage Message { get; set; } = new();

    public string Snippet { get; set; } = string.Empty;

    public string Detail => $"{ConversationTitle} - {Message.Author}";
}
