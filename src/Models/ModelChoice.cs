// SPDX-License-Identifier: MIT
// Vantage — Models/ModelChoice.cs
//
// Composite identity for the model picker. Each (provider, model)
// pair becomes one ComboBox entry, so a single provider hosting many
// models surfaces as one row per model.

namespace Vantage.Models;

public sealed class ModelChoice
{
    public Provider Provider { get; set; } = default!;
    public string ModelId { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;

    public string ProviderDisplay => Provider?.Name ?? string.Empty;

    public string Tooltip => string.IsNullOrWhiteSpace(ProviderDisplay)
        ? ModelId
        : $"{ProviderDisplay} · {ModelId}";
}
