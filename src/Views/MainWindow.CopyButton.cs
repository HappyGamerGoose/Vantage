// SPDX-License-Identifier: MIT
// Vantage — MainWindow.CopyButton.cs
//
// Per-message "Copy" affordance. Each chat message in the DataTemplate
// has a small icon button below the bubble; clicking it copies the
// message text to the clipboard via WindowsAppManager.WriteClipboard,
// then flips the icon to a checkmark for ~1.5 s as visual feedback.
//
// Robustness notes (post-mortem of v1.5.44 crash):
//   * v1.5.44 grabbed AccentBrush via Application.Current.Resources —
//     but AccentBrush lives in MainWindow.xaml's <Grid.Resources>, not
//     the app-level dictionary, so that lookup returned null and the
//     FontIcon got Foreground=null. Combined with the dead-code
//     if/else that captured a `message` variable it never used, the
//     first copy click on a fresh app launch could NRE deep inside
//     WinUI's brush resolution. v1.5.50 uses a hard-coded
//     SolidColorBrush (#0078D4 — the same hex as AccentBrush in the
//     XAML) and a single, identical feedback path for both tag
//     states, so there is no longer a code path that depends on
//     the application's resource dictionary being in any particular
//     state.
//   * All UI mutations go through the button's DispatcherQueue, so
//     the feedback swap is safe even if the click handler is invoked
//     from a non-UI thread (e.g. an automation framework).
//   * The per-button feedback timer is keyed by the Button
//     instance, not by the message, so rapid re-clicks on the same
//     message just reset the existing timer instead of stacking
//     feedback windows or leaking DispatcherQueueTimer instances.

using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vantage.Models;
using Vantage.Services;
using Windows.UI;

namespace Vantage;

public sealed partial class MainWindow
{
    // Glyphs used by the copy button. E8C8 = Segoe Fluent "Copy",
    // E73E = legacy "CheckMark". We pick E73E because it ships in
    // every Segoe font and is unambiguous at small sizes.
    private const string CopyGlyph  = "\uE8C8";
    private const string CheckGlyph = "\uE73E";

    // Hard-coded fallback for the checkmark foreground. Matches the
    // AccentBrush hex (#0078D4) in MainWindow.xaml so the feedback
    // checkmark is visually consistent with the rest of the app
    // without depending on a resource lookup that might race with
    // page initialization.
    private static readonly SolidColorBrush s_checkmarkBrush = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));

    // Per-button feedback timer. We key on the button instance so a
    // rapid second click on the same message just resets the same
    // timer instead of stacking feedback windows.
    private static readonly Dictionary<Button, DispatcherQueueTimer> s_copyFeedbackTimers = new();

    private void CopyMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Tag is not ChatMessage message) return;

        // Prefer CleanText — it strips the leading "[Agent error] " /
        // "[Provider error] " / "[Configuration error] " brackets so
        // the pasted copy reads cleanly. For a normal message
        // CleanText == Text, so this is a no-op there.
        var text = message.CopyableText;
        if (string.IsNullOrEmpty(text))
        {
            // In-progress agent run with no text yet — the user
            // clicked before any tokens landed. Best we can do is
            // nothing visible; the button will become useful once
            // the run emits its first chunk.
            return;
        }

        var ok = WindowsAppManager.WriteClipboard(text);
        if (!ok)
        {
            // Clipboard write can fail if another process is holding
            // the clipboard open. Don't crash the UI — just leave the
            // icon unchanged so the user gets visual feedback that
            // "nothing happened" and can try again.
            return;
        }

        ShowCopyFeedback(button);
    }

    private void ShowCopyFeedback(Button button)
    {
        // Run the UI swap on the button's own dispatcher. The click
        // handler is normally invoked on the UI thread, but explicit
        // marshaling makes the feedback safe regardless of the call
        // site's threading model (e.g. an automation framework that
        // synthesises clicks from a worker thread).
        var dq = button.DispatcherQueue;
        if (dq is not null && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() => ShowCopyFeedback(button));
            return;
        }

        // Capture the original Content (a FontIcon from the DataTemplate)
        // so we can restore it after the checkmark flash.
        var originalContent = button.Content;

        // Single, identical feedback path. No tag-dependent branching
        // — the previous version had a dead-code if/else that captured
        // a `message` variable it never used, and a `Tag`-conditional
        // shape that made the code harder to reason about for no
        // behavioral difference.
        button.Content = new FontIcon
        {
            FontSize = 14,
            Foreground = s_checkmarkBrush,
            Glyph = CheckGlyph,
        };

        // Reset the per-button timer (or create one). DispatcherQueueTimer
        // is the WinAppSDK preferred timer; creating it on the current
        // thread's DispatcherQueue is the documented pattern.
        if (s_copyFeedbackTimers.TryGetValue(button, out var existing))
        {
            existing.Stop();
        }
        else
        {
            var t = (dq ?? DispatcherQueue.GetForCurrentThread())?.CreateTimer();
            if (t is null)
            {
                // No dispatcher available — restore the icon
                // immediately so we don't leave a stuck checkmark.
                SafeRestoreContent(button, originalContent);
                return;
            }
            t.Interval = TimeSpan.FromMilliseconds(1500);
            t.Tick += (_, _) => SafeRestoreContent(button, originalContent);
            s_copyFeedbackTimers[button] = t;
        }
        s_copyFeedbackTimers[button].Start();
    }

    private void SafeRestoreContent(Button button, object? originalContent)
    {
        if (s_copyFeedbackTimers.TryGetValue(button, out var t))
        {
            t.Stop();
            s_copyFeedbackTimers.Remove(button);
        }
        // Only restore if the button is still in the visual tree —
        // protects against a data-template recycle that handed us a
        // stale Button instance. Catch any COMException or
        // NullReferenceException from a torn-down visual tree so the
        // timer Tick doesn't crash the app on close.
        try
        {
            button.Content = originalContent;
        }
        catch
        {
            // Button may have been recycled or the window may be
            // tearing down; the next render of the same message will
            // show the default copy icon again anyway.
        }
    }
}
