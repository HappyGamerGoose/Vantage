using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vantage.Common;
using Vantage.Models;
using Vantage.Services.Agent;

namespace Vantage.Services;

/// <summary>
/// Persists the user's custom AI providers to JSON in
/// <c>%LOCALAPPDATA%\Vantage\providers.json</c>. API keys are protected
/// with Windows DPAPI for the current user before they reach disk.
///
/// Earlier revisions stored providers in the MSIX package's
/// <c>ApplicationData.Current.LocalSettings</c>. That works for the
/// package's identity-bound settings store, but the writes are
/// buffered by the UWP runtime and there is no public flush API — a
/// hard close (task manager, sign-out, OS shutdown) can drop the last
/// write. LocalSettings also hides the data from the user: there is
/// no file the user can inspect, back up, or hand-edit, and the
/// location (<c>%LOCALAPPDATA%\Packages\&lt;pfname&gt;\Settings\settings.dat</c>)
/// is non-obvious.
///
/// A plain file write is synchronous from the caller's perspective
/// (FileStream.Flush / dispose completes before the call returns),
/// which is the durability guarantee the user actually needs when
/// the window is closing. We also keep a one-time migration from the
/// old LocalSettings location so the user doesn't lose the providers
/// they already added in v1.0/v1.5.x.
/// </summary>
public sealed class ProviderStore
{
    public const string FileName = "providers.json";
    private const string ProtectedKeyPrefix = "dpapi:v1:";
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("Vantage.Provider.ApiKey.v1");

    public string DataFolder { get; }

    public string ProviderFile { get; }

    public ProviderStore()
    {
        DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vantage");
        ProviderFile = Path.Combine(DataFolder, FileName);
    }

    /// <summary>
    /// Load all user-added providers. Returns an empty list if the
    /// file is missing or unparseable. On first run, also performs a
    /// one-time migration from the legacy LocalSettings container so
    /// the user's v1.0/v1.5.x providers survive the storage switch.
    /// </summary>
    public IReadOnlyList<Provider> Load()
    {
        Directory.CreateDirectory(DataFolder);

        // First-run migration: if there's no file (or it's an empty
        // stub from a prior buggy load) and the legacy LocalSettings
        // container has CustomProviders, copy it over. We treat a
        // zero-byte / parse-empty file the same as "missing" so a
        // user who briefly hit the load-order bug on a previous
        // build still gets their v1.0/v1.5.x providers recovered.
        if (!File.Exists(ProviderFile) || new FileInfo(ProviderFile).Length == 0)
        {
            TryMigrateFromLocalSettings();
        }

        if (!File.Exists(ProviderFile))
        {
            return new List<Provider>();
        }

        try
        {
            List<Provider> custom;
            using (var stream = File.OpenRead(ProviderFile))
            {
                custom = JsonSerializer.Deserialize<List<Provider>>(stream, JsonDefaults.Persist)
                    ?? new List<Provider>();
            }

            var providers = new List<Provider>(custom.Count);
            var foundLegacyPlaintextKey = false;
            foreach (var provider in custom)
            {
                provider.Kind = ProviderKind.Custom;
                provider.ApiKey = UnprotectApiKey(provider.ApiKey, out var wasLegacyPlaintext);
                foundLegacyPlaintextKey |= wasLegacyPlaintext;
                providers.Add(provider);
            }

            if (foundLegacyPlaintextKey)
            {
                try
                {
                    Save(providers);
                    CommonUtils.LogDiagnostic("providers-key-migrated", $"count={providers.Count}");
                }
                catch (Exception ex)
                {
                    CommonUtils.LogDiagnostic("providers-key-migration-deferred", ex.GetType().Name);
                }
            }

            CommonUtils.LogDiagnostic("providers-load", $"count={providers.Count}");
            return providers;
        }
        catch (Exception ex)
        {
            // Corrupted payload — back it up so the user can recover by
            // hand, then start clean. Without the backup step a stray
            // crash would silently destroy the file on next save.
            var backup = Path.Combine(DataFolder,
                $"providers-corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(ProviderFile, backup, overwrite: true); } catch { }
            CommonUtils.LogDiagnostic("providers-load-corrupt",
                $"backed-up-to={backup} {ex.GetType().Name}: {ex.Message}");
            return new List<Provider>();
        }
    }

    public void Save(IEnumerable<Provider> providers)
    {
        Directory.CreateDirectory(DataFolder);

        var custom = providers
            .Where(p => p.Kind == ProviderKind.Custom)
            .Select(CloneForStorage)
            .ToList();

        // Atomic write: serialize to a sibling .tmp, fsync, rename. The
        // rename is the durability point — a power loss before this
        // line leaves the previous file intact.
        var tempFile = ProviderFile + ".tmp";
        try
        {
            using (var stream = File.Create(tempFile))
            {
                JsonSerializer.Serialize(stream, custom, JsonDefaults.Persist);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempFile, ProviderFile, overwrite: true);
            CommonUtils.LogDiagnostic("providers-save", $"count={custom.Count} path={ProviderFile}");
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("providers-save-failed",
                $"{ex.GetType().Name}: {ex.Message}");
            // Re-throw so the caller (SaveProviders / FlushStateBeforeExit)
            // can decide whether to surface the failure. The previous
            // implementation swallowed it silently, which made "I added
            // a provider and it's gone on restart" impossible to debug.
            throw;
        }
    }

    private static Provider CloneForStorage(Provider provider)
    {
        return new Provider
        {
            Id = provider.Id,
            Kind = ProviderKind.Custom,
            BuiltInKey = provider.BuiltInKey,
            Name = provider.Name,
            BaseUrl = provider.BaseUrl,
            ApiKey = ProtectApiKey(provider.ApiKey),
            DefaultModel = provider.DefaultModel,
            IsEnabled = provider.IsEnabled,
            Status = provider.Status,
            LastTestedAt = provider.LastTestedAt,
            LastTestMessage = provider.LastTestMessage,
            VisionOverride = provider.VisionOverride,
        };
    }

    private static string ProtectApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return string.Empty;

        var clearBytes = Encoding.UTF8.GetBytes(apiKey);
        var protectedBytes = ProtectedData.Protect(clearBytes, KeyEntropy, DataProtectionScope.CurrentUser);
        return ProtectedKeyPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectApiKey(string storedValue, out bool wasLegacyPlaintext)
    {
        wasLegacyPlaintext = false;
        if (string.IsNullOrWhiteSpace(storedValue)) return string.Empty;

        if (!storedValue.StartsWith(ProtectedKeyPrefix, StringComparison.Ordinal))
        {
            wasLegacyPlaintext = true;
            return storedValue;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(storedValue[ProtectedKeyPrefix.Length..]);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, KeyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("providers-key-decrypt-failed", ex.GetType().Name);
            return string.Empty;
        }
    }

    /// <summary>
    /// One-time migration: if the legacy LocalSettings container has a
    /// CustomProviders blob, copy it into the new file-based store and
    /// clear the legacy key so we don't keep re-migrating. Wrapped in a
    /// try/catch because the user may be running an unpackaged dev
    /// build where LocalSettings isn't available — that's fine, the
    /// empty file just won't be created.
    /// </summary>
    private void TryMigrateFromLocalSettings()
    {
        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (!localSettings.Containers.TryGetValue("Vantage", out var container))
            {
                return;
            }
            if (!container.Values.TryGetValue("CustomProviders", out var raw))
            {
                return;
            }
            var json = raw?.ToString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            // Validate the JSON before writing it to disk. A bad blob
            // shouldn't poison the new file-based store.
            var parsed = JsonSerializer.Deserialize<List<Provider>>(json, JsonDefaults.Persist);
            if (parsed is null || parsed.Count == 0)
            {
                return;
            }

            Save(parsed);
            CommonUtils.LogDiagnostic("providers-migrated",
                $"from=LocalSettings count={parsed.Count} to={ProviderFile}");

            // Best-effort clear of the legacy key so a future launch
            // doesn't re-migrate. Failure here is fine — the file is
            // already written, the worst case is we migrate again.
            try { container.Values.Remove("CustomProviders"); } catch { }
        }
        catch (Exception ex)
        {
            // LocalSettings is unavailable (e.g. dev / unpackaged run).
            // Not an error — the new file-based store is the source of
            // truth from here on.
            CommonUtils.LogDiagnostic("providers-migrate-skipped",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
