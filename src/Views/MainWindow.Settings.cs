// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Settings.cs
//
// Settings persistence layer for MainWindow. Conversations go through
// LocalHistoryStore (JSON file); everything else (selected model, theme
// palette, theme mode, sidebar collapsed state) goes through LocalSettings.

using System.IO;
using System.Text.Json;
using Vantage.Services.Agent;

namespace Vantage;

public sealed partial class MainWindow
{
    private async Task PersistAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            await _historyStore.SaveAsync(Conversations);
        }
        catch (Exception ex)
        {
            // Persistence MUST never crash the UI. Log and keep running
            // so a transient file-system error doesn't lock the user out
            // of their app. The Window.Closing handler forces a final
            // flush on shutdown, so even a mid-run failure here still
            // gets a clean retry on close.
            CommonUtils.LogDiagnostic("conversations-persist-failed", ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Synchronous variant used by Window.Closing — we have to finish
    /// writing before the process is gone, so we can't yield. The actual
    /// underlying file write is synchronous when invoked this way
    /// (JsonSerializer.Serialize → file stream → File.Move), so this
    /// completes in a few milliseconds on a healthy disk.
    /// </summary>
    private void PersistSync()
    {
        try
        {
            Directory.CreateDirectory(_historyStore.DataFolder);
            var temp = Path.Combine(_historyStore.DataFolder, "history.tmp");
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, Conversations,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            File.Move(temp, _historyStore.HistoryFile, overwrite: true);
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("conversations-persist-sync-failed", ex.Message);
        }
    }

    /// <summary>
    /// Final flush hook for window close + Stop button + unobserved
    /// cancellation paths. Persists conversations AND providers to disk
    /// so neither one is ever lost between sessions. Synchronous so it
    /// has to complete before the process exits.
    /// </summary>
    private void FlushStateBeforeExit()
    {
        try { PersistSync(); } catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("flush-persist-sync-failed", ex.Message);
        }
        try { _providerStore.Save(_providers); }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("flush-provider-save-failed", ex.Message);
        }
    }

    private void LoadSettings()
    {
        // Restore the persisted palette + theme. ApplyCurrentTheme
        // also handles late-bound UI on the Settings page (Selector
        // indices sync up once the page is first shown — see
        // PalettePicker_Loaded / ThemeModeCombo_SelectionChanged).
        try
        {
            ApplyCurrentTheme();
        }
        catch
        {
            // Theme restore is best-effort; the app can run with default
            // theme if persisted state is corrupted.
        }

        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        if (localSettings.Values.TryGetValue("SidebarExpanded", out var se) && se is bool seb)
        {
            _sidebarExpanded = seb;
        }
        UpdateSidebarVisibility();
    }

    private bool GetSetting(string key, bool defaultValue)
    {
        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        return localSettings.Values.TryGetValue(key, out var val) && val is bool b ? b : defaultValue;
    }

    private string GetSetting(string key, string defaultValue)
    {
        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        return localSettings.Values.TryGetValue(key, out var val) && val is string s ? s : defaultValue;
    }
}
