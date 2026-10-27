// SPDX-License-Identifier: MIT
// Vantage — Services/ModelCatalogService.cs
//
// Asks a provider which models it serves (GET {baseUrl}/models), so the
// user can tick models off a list instead of typing ids from memory. Works
// for OpenAI-compatible endpoints and for Anthropic's native API.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Vantage.Services;

public static class ModelCatalogService
{
    /// <summary>
    /// Returns the model ids the endpoint advertises, sorted. Throws with a
    /// readable message when the endpoint can't be reached or rejects the key.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("Enter the Base URL first.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Enter the API key first.");

        var isAnthropic = baseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase);
        var url = isAnthropic ? $"{baseUrl}/v1/models?limit=200" : $"{baseUrl}/models";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (isAnthropic)
        {
            req.Headers.Add("x-api-key", apiKey.Trim());
            req.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey.Trim()}");
        }

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(resp.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    => "The provider rejected the API key.",
                System.Net.HttpStatusCode.NotFound
                    => "This endpoint doesn't list its models. Type the model id instead.",
                _ => $"The provider answered HTTP {(int)resp.StatusCode}.",
            });
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) ? data : default;
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The provider's model list was in an unexpected format.");

        return items.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : e.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
