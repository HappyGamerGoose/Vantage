using System.IO;
using System.Text.Json;
using Vantage.Common;
using Vantage.Models;
using Vantage.Services.Agent;
using Microsoft.UI.Dispatching;

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
            CommonUtils.LogDiagnostic("history-load", "no-file fresh-start");
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

                    // Rebuild the live AgentRunViewModel from the
                    // persisted snapshot so the stepper, progress
                    // bar, counter strip, and termination card all
                    // render on next launch. The marshal helper
                    // runs every UI mutation on the UI thread; the
                    // view model itself runs no live code paths until
                    // something binds to it.
                    if (message.AgentRunSnapshot is not null)
                    {
                        // DispatcherQueueHandler is a void() delegate
                        // (no parameters), so we wrap the marshalled
                        // Action in a parameterless lambda instead of
                        // passing the Action itself — the framework
                        // would otherwise treat the Action's
                        // parameter as a return slot and refuse the
                        // call.
                        message.ReconstructAgentRun(a => DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => a()));
                    }
                }
            }

            CommonUtils.LogDiagnostic("history-load", $"count={conversations.Count} path={HistoryFile}");
            return conversations;
        }
        catch (Exception ex)
        {
            // Corrupted payload — keep a timestamped copy so the user
            // can recover by hand, then start clean. Without the
            // backup step a stray crash would silently destroy the
            // file on next save.
            var backup = Path.Combine(DataFolder, $"history-corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(HistoryFile, backup, overwrite: true); } catch { }
            CommonUtils.LogDiagnostic("history-load-corrupt",
                $"backed-up-to={backup} {ex.GetType().Name}: {ex.Message}");
            return new List<Conversation>();
        }
    }

    public async Task SaveAsync(IEnumerable<Conversation> conversations)
    {
        await WriteAtomicAsync(conversations, "history-save");
    }

    /// <summary>
    /// Synchronous variant used by Window.Closing — we have to finish
    /// writing before the process is gone, so we can't yield. The
    /// underlying file write is synchronous when invoked this way
    /// (JsonSerializer.Serialize → file stream → FileStream.Flush →
    /// File.Move), so this completes in a few milliseconds on a
    /// healthy disk. Reuses the same atomic-rename pattern as the
    /// async path so a partial write never tears the file.
    /// </summary>
    public void SaveSync(IEnumerable<Conversation> conversations)
    {
        WriteAtomicSync(conversations, "history-save-sync");
    }

    /// <summary>
    /// Atomic JSON write shared by the async and sync paths. We
    /// serialize to a sibling .tmp, fsync, then rename over the
    /// real file. The rename is the durability point — a power
    /// loss before this line leaves the previous file intact.
    /// </summary>
    private async Task WriteAtomicAsync(IEnumerable<Conversation> conversations, string diagCategory)
    {
        Directory.CreateDirectory(DataFolder);
        var tempFile = HistoryFile + ".tmp";
        try
        {
            await using (var stream = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(stream, conversations, JsonDefaults.Persist).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(tempFile, HistoryFile, overwrite: true);
            var count = conversations is ICollection<Conversation> col ? col.Count : -1;
            CommonUtils.LogDiagnostic(diagCategory, $"count={count} path={HistoryFile}");
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("history-save-failed",
                $"{ex.GetType().Name}: {ex.Message}");
            // Re-throw so the caller (PersistAsync / FlushStateBeforeExit)
            // can decide whether to surface the failure. The previous
            // implementation swallowed it silently, which made
            // "I added a provider and it's gone on restart" impossible
            // to debug.
            throw;
        }
    }

    private void WriteAtomicSync(IEnumerable<Conversation> conversations, string diagCategory)
    {
        Directory.CreateDirectory(DataFolder);
        var tempFile = HistoryFile + ".tmp";
        try
        {
            using (var stream = File.Create(tempFile))
            {
                JsonSerializer.Serialize(stream, conversations, JsonDefaults.Persist);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempFile, HistoryFile, overwrite: true);
            var count = conversations is ICollection<Conversation> col ? col.Count : -1;
            CommonUtils.LogDiagnostic(diagCategory, $"count={count} path={HistoryFile}");
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("history-save-failed",
                $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
