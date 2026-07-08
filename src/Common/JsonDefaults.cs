// SPDX-License-Identifier: MIT
// Vantage — Common/JsonDefaults.cs
//
// Single source of truth for `JsonSerializerOptions`. Both
// ProviderStore and LocalHistoryStore previously built their own
// identical instances on first serialization; consolidating here saves
// an option-cache allocation per store and keeps every JSON write
// consistent (same casing, same indent, same number-formatting).

using System.Text.Json;

namespace Vantage.Common;

internal static class JsonDefaults
{
    /// <summary>
    /// The single options instance shared by every persistence layer.
    /// `WriteIndented = true` makes on-disk `<package>.json` files
    /// diff-friendly for inspection without significantly bloating them.
    /// `DefaultIgnoreCondition = WhenWritingNull` keeps optional fields
    /// out of the persisted blob so a regeneration with the same model
    /// doesn't churn on every save.
    /// </summary>
    public static readonly JsonSerializerOptions Persist =
        new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
}
