using System.Text.Json;
using Vantage.Common;
using Vantage.Models;

namespace Vantage.Services;

public sealed class LocalHistoryStore
{
    public LocalHistoryStore()
    {
        DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vantage");
        HistoryFile = Path.Combine(DataFolder, "history.json");
    }

    public string DataFolder { get; }

    public string HistoryFile { get; }

    public async Task<IList<Conversation>> LoadAsync()
    {
        Directory.CreateDirectory(DataFolder);

        if (!File.Exists(HistoryFile))
        {
            return new List<Conversation>();
        }

        try
        {
            await using var stream = File.OpenRead(HistoryFile);
            var conversations = await JsonSerializer.DeserializeAsync<List<Conversation>>(stream, JsonDefaults.Persist)
                ?? new List<Conversation>();

            foreach (var conversation in conversations)
            {
                conversation.Messages ??= new();
                if (conversation.CreatedAt == default)
                {
                    conversation.CreatedAt = DateTimeOffset.Now;
                }

                if (conversation.UpdatedAt == default)
                {
                    conversation.UpdatedAt = conversation.Messages.LastOrDefault()?.CreatedAt ?? conversation.CreatedAt;
                }

                foreach (var message in conversation.Messages)
                {
                    if (message.CreatedAt == default)
                    {
                        message.CreatedAt = conversation.CreatedAt;
                    }
                }
            }

            return conversations;
        }
        catch
        {
            var backup = Path.Combine(DataFolder, $"history-corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(HistoryFile, backup, overwrite: true);
            return new List<Conversation>();
        }
    }

    public async Task SaveAsync(IEnumerable<Conversation> conversations)
    {
        Directory.CreateDirectory(DataFolder);

        var tempFile = Path.Combine(DataFolder, "history.tmp");
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, conversations, JsonDefaults.Persist);
        }

        File.Move(tempFile, HistoryFile, overwrite: true);
    }
}
