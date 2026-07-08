using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Vantage.Common;
using Vantage.Models;
using Windows.Storage;

namespace Vantage.Services;

public sealed class ProviderStore
{
    private const string CustomProvidersKey = "CustomProviders";

    private readonly ApplicationDataStore _store;

    public ProviderStore()
    {
        _store = new ApplicationDataStore("Vantage");
    }

    /// <summary>
    /// Returns only the user-added providers. Built-ins were removed;
    /// the list starts empty until the user adds at least one endpoint.
    /// </summary>
    public IReadOnlyList<Provider> Load()
    {
        var providers = new List<Provider>();

        var customJson = _store.Get(CustomProvidersKey);
        if (!string.IsNullOrEmpty(customJson))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<List<Provider>>(customJson, JsonDefaults.Persist);
                if (custom is not null)
                {
                    foreach (var provider in custom)
                    {
                        provider.Kind = ProviderKind.Custom;
                        providers.Add(provider);
                    }
                }
            }
            catch
            {
                // Corrupted payload — start clean.
            }
        }

        return providers;
    }

    public void Save(IEnumerable<Provider> providers)
    {
        var custom = providers
            .Where(p => p.Kind == ProviderKind.Custom)
            .ToList();

        var json = JsonSerializer.Serialize(custom, JsonDefaults.Persist);
        _store.Set(CustomProvidersKey, json);
    }
}

internal sealed class ApplicationDataStore
{
    private readonly ApplicationDataContainer _container;

    public ApplicationDataStore(string name)
    {
        var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        if (!localSettings.Containers.TryGetValue(name, out var container))
        {
            container = localSettings.CreateContainer(name, ApplicationDataCreateDisposition.Always);
        }
        _container = container;
    }

    public string? Get(string key)
    {
        if (_container.Values.TryGetValue(key, out var value))
        {
            return value?.ToString();
        }
        return null;
    }

    public void Set(string key, string value)
    {
        _container.Values[key] = value;
    }
}