// SPDX-License-Identifier: MIT
// Vantage — Services/Agent/WorldDiff.cs
//
// Symmetric diff between two WorldSnapshots. Used by ActionVerifier to
// decide "did the action land as expected" without leaning on the model's
// visual judgment.

namespace Vantage.Services.Agent;

public sealed record WorldDiff(
    bool ForegroundChanged,
    string OldForegroundTitle,
    string NewForegroundTitle,
    bool ForegroundProcessChanged,
    string OldForegroundProcess,
    string NewForegroundProcess,
    bool CursorMoved,
    int CursorDeltaX,
    int CursorDeltaY,
    bool ClipboardChanged,
    string? OldClipboardText,
    string? NewClipboardText,
    int WindowsAddedCount,
    int WindowsRemovedCount,
    bool FingerprintChanged)
{
    public static WorldDiff Between(WorldSnapshot before, WorldSnapshot after, WorldSnapshot? beforeFingerprintSource = null)
    {
        var foregroundChanged = before.ForegroundTitle != after.ForegroundTitle
                             || before.ForegroundHwnd != after.ForegroundHwnd;
        var processChanged = before.ForegroundProcess != after.ForegroundProcess
                          || before.ForegroundPid != after.ForegroundPid;
        var dx = after.CursorX - before.CursorX;
        var dy = after.CursorY - before.CursorY;
        var cursorMoved = dx * dx + dy * dy >= 16; // ≥ 4 logical px in either axis
        var clipChanged = before.ClipboardText != after.ClipboardText;
        var fpChanged = before.Fingerprint != after.Fingerprint;
        var beforeWindows = before.VisibleWindowHandles.ToHashSet();
        var afterWindows = after.VisibleWindowHandles.ToHashSet();
        var addedWindows = afterWindows.Except(beforeWindows).Count();
        var removedWindows = beforeWindows.Except(afterWindows).Count();

        return new WorldDiff(
            foregroundChanged,
            before.ForegroundTitle,
            after.ForegroundTitle,
            processChanged,
            before.ForegroundProcess,
            after.ForegroundProcess,
            cursorMoved,
            dx, dy,
            clipChanged,
            before.ClipboardText,
            after.ClipboardText,
            addedWindows,
            removedWindows,
            fpChanged);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (ForegroundChanged)
            parts.Add($"fg: '{OldForegroundTitle}' -> '{NewForegroundTitle}'");
        if (ForegroundProcessChanged)
            parts.Add($"proc: {OldForegroundProcess} -> {NewForegroundProcess}");
        if (CursorMoved)
            parts.Add($"cursor: ({CursorDeltaX:+#;-#;0},{CursorDeltaY:+#;-#;0})");
        if (ClipboardChanged)
            parts.Add($"clip changed ({(OldClipboardText?.Length ?? 0)} -> {NewClipboardText?.Length ?? 0} chars)");
        if (WindowsAddedCount > 0)   parts.Add($"+{WindowsAddedCount} windows");
        if (WindowsRemovedCount > 0) parts.Add($"-{WindowsRemovedCount} windows");
        if (parts.Count == 0) return "(no observable change)";
        return string.Join("; ", parts);
    }
}
