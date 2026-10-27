// SPDX-License-Identifier: MIT
// Vantage — Models/ProviderModel.cs
//
// One model hosted by a provider. The provider owns the endpoint and the
// key; each model owns its id, the name the user wants to see in the
// model selector, and its own vision override (different models behind the
// same endpoint rarely agree on image support).

using System;

using System.Text.Json.Serialization;

namespace Vantage.Models;

public sealed class ProviderModel
{
    /// <summary>The id sent to the API, e.g. <c>gpt-4o</c> or <c>nvidia/nemotron-…</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-chosen label for the selector. Blank means "show the model id as-is".</summary>
    public string DisplayName { get; set; } = string.Empty;

    public VisionOverride VisionOverride { get; set; } = VisionOverride.Auto;

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName.Trim();
}
