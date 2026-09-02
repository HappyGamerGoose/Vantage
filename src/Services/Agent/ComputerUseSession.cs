// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UIAutomation;

namespace Vantage.Services.Agent;

/// <summary>
/// Window-scoped observation and input state. An observation may authorize
/// exactly one element or coordinate action; callers must observe again after
/// any action that can change focus, layout, or the accessibility tree.
/// </summary>
internal sealed class ComputerUseSession
{
    private const int MaxElements = 240;
    private const int ValuePatternId = 10002;
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMinutes(2);

    private sealed record ObservedElement(
        int Index,
        string Name,
        string Role,
        bool Enabled,
        bool Offscreen,
        PhysicalRect Bounds,
        IUIAutomationElement NativeElement);

    private sealed record Observation(
        string Id,
        string WindowId,
        WindowsAppManager.WindowInfo Window,
        DateTimeOffset CreatedAt,
        IReadOnlyDictionary<int, ObservedElement> Elements,
        string Tree,
        string FocusedElement,
        bool FocusedIsPassword);

    private readonly object _gate = new();
    private Observation? _current;

    public static bool IsScopedAction(string? action) => action?.Trim().ToLowerInvariant() is
        "list_windows" or "get_window_state" or "activate_window" or
        "click_element" or "click_window_xy" or "scroll_window" or
        "drag_window" or "set_value" or "type_window_text" or "press_window_key";

    public void Invalidate()
    {
        Observation? stale;
        lock (_gate)
        {
            stale = _current;
            _current = null;
        }
        if (stale is not null) ReleaseObservation(stale);
    }

    public string ListWindows()
    {
        var windows = WindowsAppManager.ListVisibleWindows();
        if (windows.Count == 0) return "No targetable windows are open.";

        var sb = new StringBuilder();
        sb.AppendLine($"{windows.Count} targetable window(s). Use only a returned window_id:");
        foreach (var window in windows.Take(80))
        {
            sb.Append("  window_id=").Append(FormatWindowId(window))
              .Append(" pid=").Append(window.Pid)
              .Append(" title=\"").Append(OneLine(window.Title, 120)).AppendLine("\"");
        }
        return sb.ToString().TrimEnd();
    }

    public ActionResult ActivateWindow(string? windowId)
    {
        Invalidate();
        if (!TryResolveWindow(windowId, out var window, out var error))
            return new ActionResult(ActionOutcome.Failed, error);

        var focused = WindowsAppManager.FocusWindow(window!);
        return new ActionResult(
            focused ? ActionOutcome.Success : ActionOutcome.Failed,
            focused
                ? $"activated {FormatWindowId(window!)} \"{OneLine(window!.Title, 100)}\""
                : $"could not activate {FormatWindowId(window!)}");
    }

    public ActionResult ObserveWindow(string? windowId)
    {
        if (!TryResolveWindow(windowId, out var window, out var error))
            return new ActionResult(ActionOutcome.Failed, error);

        var observed = ReadAccessibility(window!);
        Observation? previous;
        lock (_gate)
        {
            previous = _current;
            _current = observed;
        }
        if (previous is not null) ReleaseObservation(previous);

        var sb = new StringBuilder();
        sb.Append("observation_id=").Append(observed.Id)
          .Append(" window_id=").Append(observed.WindowId)
          .Append(" title=\"").Append(OneLine(window!.Title, 120)).AppendLine("\"");
        if (!string.IsNullOrWhiteSpace(observed.FocusedElement))
            sb.Append("focused: ").AppendLine(observed.FocusedElement);
        sb.Append(observed.Tree);

        return new ActionResult(ActionOutcome.Success, sb.ToString().TrimEnd());
    }

    public async Task<ActionResult> ClickElementAsync(
        string? windowId,
        string? observationId,
        int? elementIndex,
        string button,
        int clickCount,
        CancellationToken ct)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (elementIndex is null || !liveObservation.Elements.TryGetValue(elementIndex.Value, out var element))
                return new ActionResult(ActionOutcome.Failed, "element_index was not returned by that observation; re-observe the window");
            if (!element.Enabled || element.Offscreen || element.Bounds.Width <= 0 || element.Bounds.Height <= 0)
                return new ActionResult(ActionOutcome.Failed, $"element [{element.Index}] is disabled, offscreen, or has no clickable bounds; re-observe after scrolling");
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; no click was sent");

            var (x, y) = PhysicalToLogical(element.Bounds.CenterX, element.Bounds.CenterY);
            await SendClickAsync(x, y, button, clickCount, ct);
            return new ActionResult(ActionOutcome.Success,
                $"clicked [{element.Index}] {element.Role} \"{OneLine(element.Name, 100)}\" in {liveObservation.WindowId}");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    public async Task<ActionResult> ClickWindowAsync(
        string? windowId,
        string? observationId,
        int? relativeX,
        int? relativeY,
        string button,
        int clickCount,
        CancellationToken ct)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (relativeX is null || relativeY is null)
                return new ActionResult(ActionOutcome.Failed, "click_window_xy requires integer x and y from the latest window observation");
            if (!TryWindowPoint(liveObservation, relativeX.Value, relativeY.Value, out var x, out var y, out error))
                return new ActionResult(ActionOutcome.Failed, error);
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; no click was sent");

            await SendClickAsync(x, y, button, clickCount, ct);
            return new ActionResult(ActionOutcome.Success,
                $"clicked window-relative ({relativeX},{relativeY}) in {liveObservation.WindowId}");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    public ActionResult ScrollWindow(
        string? windowId,
        string? observationId,
        int? relativeX,
        int? relativeY,
        int? scrollY)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (relativeX is null || relativeY is null || scrollY is null)
                return new ActionResult(ActionOutcome.Failed, "scroll_window requires x, y, and scroll_y from the latest window observation");
            if (!TryWindowPoint(liveObservation, relativeX.Value, relativeY.Value, out var x, out var y, out error))
                return new ActionResult(ActionOutcome.Failed, error);
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; no scroll was sent");

            // The plugin uses positive Y for down. Win32 wheel deltas use the opposite sign.
            WindowsAutomationService.Scroll(-Math.Clamp(scrollY.Value, -2400, 2400), x, y);
            return new ActionResult(ActionOutcome.Success,
                $"scrolled {scrollY.Value} at window-relative ({relativeX},{relativeY}) in {liveObservation.WindowId}");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    public async Task<ActionResult> DragWindowAsync(
        string? windowId,
        string? observationId,
        int? fromX,
        int? fromY,
        int? toX,
        int? toY,
        CancellationToken ct)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (fromX is null || fromY is null || toX is null || toY is null)
                return new ActionResult(ActionOutcome.Failed, "drag_window requires from_x, from_y, to_x, and to_y");
            if (!TryWindowPoint(liveObservation, fromX.Value, fromY.Value, out var sx, out var sy, out error)
                || !TryWindowPoint(liveObservation, toX.Value, toY.Value, out var ex, out var ey, out error))
                return new ActionResult(ActionOutcome.Failed, error);
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; no drag was sent");

            await WindowsAutomationService.DragAsync(sx, sy, ex, ey, WindowsAutomationService.MouseButton.Left, ct);
            return new ActionResult(ActionOutcome.Success,
                $"dragged window-relative ({fromX},{fromY}) -> ({toX},{toY}) in {liveObservation.WindowId}");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    public ActionResult SetValue(
        string? windowId,
        string? observationId,
        int? elementIndex,
        string value)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (elementIndex is null || !liveObservation.Elements.TryGetValue(elementIndex.Value, out var element))
                return new ActionResult(ActionOutcome.Failed, "element_index was not returned by that observation; re-observe the window");
            if (!element.Enabled)
                return new ActionResult(ActionOutcome.Failed, $"element [{element.Index}] is disabled");
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; value was not changed");

            var patternObject = element.NativeElement.GetCurrentPattern(ValuePatternId);
            try
            {
                if (patternObject is not IUIAutomationValuePattern pattern)
                {
                    return new ActionResult(ActionOutcome.Failed,
                        $"element [{element.Index}] does not expose a writable Value pattern");
                }
                pattern.SetValue(value);
                return new ActionResult(ActionOutcome.Success,
                    $"set value on [{element.Index}] {element.Role} \"{OneLine(element.Name, 100)}\"");
            }
            finally
            {
                ReleaseComObject(patternObject);
            }
        }
        catch (Exception ex)
        {
            return new ActionResult(ActionOutcome.Failed,
                $"element [{elementIndex}] does not expose a writable Value pattern: {ex.Message}");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    public async Task<ActionResult> TypeTextAsync(
        string? windowId,
        string? observationId,
        string text,
        int delayMs,
        CancellationToken ct)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (string.IsNullOrEmpty(text))
                return new ActionResult(ActionOutcome.Failed, "type_window_text requires non-empty text");
            if (string.IsNullOrWhiteSpace(liveObservation.FocusedElement))
                return new ActionResult(ActionOutcome.Failed, "the latest observation did not identify a focused element; click the editable control, re-observe, then type");
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; no text was sent");

            var typed = await WindowsAppManager.TypeTextAsync(text, Math.Clamp(delayMs, 0, 1000), ct);
            var expected = text.EnumerateRunes().Count();
            return new ActionResult(
                typed == expected ? ActionOutcome.Success : ActionOutcome.Failed,
                typed == expected
                    ? $"typed {typed} character(s) into {liveObservation.WindowId} focused={liveObservation.FocusedElement}"
                    : $"typed only {typed} of {expected} character(s); re-observe before retrying");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    public ActionResult PressKey(
        string? windowId,
        string? observationId,
        string combo)
    {
        if (!TryConsume(windowId, observationId, out var observation, out var error))
            return new ActionResult(ActionOutcome.Failed, error);
        var liveObservation = observation!;
        try
        {
            if (string.IsNullOrWhiteSpace(combo))
                return new ActionResult(ActionOutcome.Failed, "press_window_key requires a key or chord");
            if (!WindowsAppManager.FocusWindow(liveObservation.Window))
                return new ActionResult(ActionOutcome.Failed, "target window could not be activated; no key was sent");

            var ok = WindowsAppManager.PressKey(combo);
            return new ActionResult(
                ok ? ActionOutcome.Success : ActionOutcome.Failed,
                ok ? $"pressed {combo} in {liveObservation.WindowId}" : $"key '{combo}' was rejected");
        }
        finally
        {
            ReleaseObservation(liveObservation);
        }
    }

    private static Observation ReadAccessibility(WindowsAppManager.WindowInfo window)
    {
        var elements = new Dictionary<int, ObservedElement>();
        var tree = new StringBuilder();
        var focused = string.Empty;
        var focusedIsPassword = false;

        IUIAutomation? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? found = null;
        IUIAutomationElement? focusedElement = null;
        try
        {
            automation = new CUIAutomation();
            root = automation.ElementFromHandle(window.Handle);
            condition = automation.CreateTrueCondition();
            found = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            int count = Math.Min(found.Length, MaxElements);

            for (var i = 0; i < count; i++)
            {
                IUIAutomationElement? native = null;
                var retained = false;
                try
                {
                    native = found.GetElement(i);
                    var name = SafeString(() => native.CurrentName);
                    var automationId = SafeString(() => native.CurrentAutomationId);
                    var role = ControlTypeName(native.CurrentControlType);
                    var enabled = SafeBool(() => native.CurrentIsEnabled != 0, true);
                    var offscreen = SafeBool(() => native.CurrentIsOffscreen != 0, false);
                    var isPassword = SafeBool(() => native.CurrentIsPassword != 0, false);
                    var bounds = ReadBounds(native);
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(automationId)) continue;
                    var index = elements.Count;
                    var label = string.IsNullOrWhiteSpace(name) ? automationId : name;
                    var element = new ObservedElement(index, label, role, enabled, offscreen, bounds, native);
                    elements[index] = element;
                    retained = true;
                    tree.Append('[').Append(index).Append("] ").Append(role)
                        .Append(" name=\"").Append(OneLine(label, 140)).Append('"');
                    if (!string.IsNullOrWhiteSpace(automationId) && !automationId.Equals(label, StringComparison.Ordinal))
                        tree.Append(" id=\"").Append(OneLine(automationId, 80)).Append('"');
                    if (!enabled) tree.Append(" disabled");
                    if (offscreen) tree.Append(" offscreen");
                    if (bounds.Width > 0 && bounds.Height > 0)
                        tree.Append(" bounds=").Append(bounds.Left).Append(',').Append(bounds.Top)
                            .Append(' ').Append(bounds.Width).Append('x').Append(bounds.Height);
                    tree.AppendLine();
                }
                catch
                {
                    // UIA trees are live; individual elements may disappear during enumeration.
                }
                finally
                {
                    if (!retained) ReleaseComObject(native);
                }
            }

            try
            {
                focusedElement = automation.GetFocusedElement();
                if (focusedElement.CurrentProcessId == window.Pid)
                {
                    focusedIsPassword = SafeBool(() => focusedElement.CurrentIsPassword != 0, false);
                    var name = SafeString(() => focusedElement.CurrentName);
                    if (string.IsNullOrWhiteSpace(name) && focusedIsPassword)
                        name = "password control";
                    var role = ControlTypeName(focusedElement.CurrentControlType);
                    if (!string.IsNullOrWhiteSpace(name)) focused = $"{role} name=\"{OneLine(name, 140)}\"";
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            tree.Append("Accessibility unavailable: ").Append(OneLine(ex.Message, 180));
        }
        finally
        {
            ReleaseComObject(focusedElement);
            ReleaseComObject(found);
            ReleaseComObject(condition);
            ReleaseComObject(root);
            ReleaseComObject(automation);
        }

        if (tree.Length == 0) tree.Append("No named accessibility elements were returned. Activate the window and use a fresh screenshot.");
        return new Observation(
            "obs-" + Guid.NewGuid().ToString("N")[..12],
            FormatWindowId(window),
            window,
            DateTimeOffset.UtcNow,
            elements,
            tree.ToString().TrimEnd(),
            focused,
            focusedIsPassword);
    }

    private bool TryConsume(
        string? windowId,
        string? observationId,
        out Observation? observation,
        out string error)
    {
        lock (_gate)
        {
            observation = _current;
            _current = null;
        }

        if (observation is null)
        {
            error = "no live observation; call get_window_state and use its returned observation_id exactly once";
            return false;
        }
        if (!string.Equals(observation.Id, observationId?.Trim(), StringComparison.Ordinal)
            || !string.Equals(observation.WindowId, windowId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ReleaseObservation(observation);
            error = "window_id or observation_id does not match the latest observation; re-observe before acting";
            return false;
        }
        if (DateTimeOffset.UtcNow - observation.CreatedAt > ObservationLifetime)
        {
            ReleaseObservation(observation);
            error = "the observation expired; re-observe before acting";
            return false;
        }
        if (!TryResolveWindow(windowId, out var current, out _)
            || current!.Pid != observation.Window.Pid
            || current.Handle != observation.Window.Handle)
        {
            ReleaseObservation(observation);
            error = "the observed window closed or changed identity; list windows again";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void ReleaseObservation(Observation observation)
    {
        foreach (var element in observation.Elements.Values)
            ReleaseComObject(element.NativeElement);
    }

    private static void ReleaseComObject(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch
        {
            // UIA providers can disappear while their COM wrappers are released.
        }
    }

    private static bool TryResolveWindow(
        string? windowId,
        out WindowsAppManager.WindowInfo? window,
        out string error)
    {
        window = null;
        var normalized = windowId?.Trim() ?? string.Empty;
        if (!TryParseWindowId(normalized, out var handle, out var pid))
        {
            error = "invalid window_id; call list_windows and copy one returned identifier exactly";
            return false;
        }

        var matches = WindowsAppManager.ListVisibleWindows()
            .Where(candidate => candidate.Handle == handle && candidate.Pid == pid)
            .ToList();
        if (matches.Count != 1)
        {
            error = $"window_id {normalized} no longer resolves to exactly one visible window; call list_windows again";
            return false;
        }
        window = matches[0];
        error = string.Empty;
        return true;
    }

    internal static string FormatWindowId(WindowsAppManager.WindowInfo window) =>
        $"win-{window.Handle.ToInt64():X}-{window.Pid}";

    internal static bool TryParseWindowId(string value, out IntPtr handle, out uint pid)
    {
        handle = IntPtr.Zero;
        pid = 0;
        var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !parts[0].Equals("win", StringComparison.OrdinalIgnoreCase)) return false;
        if (!long.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawHandle)) return false;
        if (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return false;
        handle = new IntPtr(rawHandle);
        return handle != IntPtr.Zero && pid != 0;
    }

    private static bool TryWindowPoint(
        Observation observation,
        int relativeX,
        int relativeY,
        out int logicalX,
        out int logicalY,
        out string error)
    {
        logicalX = logicalY = 0;
        if (!WindowsAppManager.TryGetWindowBounds(observation.Window.Handle, out var bounds))
        {
            error = "could not read current target-window bounds; re-observe before acting";
            return false;
        }
        var scale = WindowsAutomationService.GetPrimaryMonitor().LogicalToPhysicalScale;
        var width = (int)Math.Round((bounds.Right - bounds.Left) / scale);
        var height = (int)Math.Round((bounds.Bottom - bounds.Top) / scale);
        if (relativeX < 0 || relativeY < 0 || relativeX >= width || relativeY >= height)
        {
            error = $"window-relative point ({relativeX},{relativeY}) is outside the observed {width}x{height} window";
            return false;
        }
        logicalX = (int)Math.Round(bounds.Left / scale) + relativeX;
        logicalY = (int)Math.Round(bounds.Top / scale) + relativeY;
        error = string.Empty;
        return true;
    }

    private static async Task SendClickAsync(int x, int y, string button, int clickCount, CancellationToken ct)
    {
        var normalized = button.Trim().ToLowerInvariant();
        if (normalized is not ("left" or "right" or "middle"))
            throw new ArgumentOutOfRangeException(nameof(button), "button must be left, right, or middle");
        for (var i = 0; i < Math.Clamp(clickCount, 1, 3); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (normalized == "right") WindowsAutomationService.RightClick(x, y);
            else if (normalized == "middle") WindowsAutomationService.MiddleClick(x, y);
            else WindowsAutomationService.LeftClick(x, y);
            if (i < clickCount - 1) await Task.Delay(80, ct);
        }
    }

    private static PhysicalRect ReadBounds(IUIAutomationElement element)
    {
        try
        {
            var rect = element.CurrentBoundingRectangle;
            return new PhysicalRect(
                rect.left,
                rect.top,
                Math.Max(0, rect.right - rect.left),
                Math.Max(0, rect.bottom - rect.top));
        }
        catch
        {
            return new PhysicalRect(0, 0, 0, 0);
        }
    }

    private static (int X, int Y) PhysicalToLogical(int x, int y)
    {
        var scale = WindowsAutomationService.GetPrimaryMonitor().LogicalToPhysicalScale;
        return ((int)Math.Round(x / scale), (int)Math.Round(y / scale));
    }

    private static string ControlTypeName(int id) => id switch
    {
        50000 => "button", 50002 => "checkbox", 50003 => "combobox",
        50004 => "edit", 50005 => "hyperlink", 50006 => "image",
        50007 => "list-item", 50008 => "list", 50009 => "menu",
        50010 => "menu-bar", 50011 => "menu-item", 50013 => "radio-button",
        50014 => "scroll-bar", 50015 => "slider", 50018 => "tab",
        50019 => "tab-item", 50020 => "text", 50021 => "toolbar",
        50023 => "tree", 50024 => "tree-item", 50028 => "data-grid",
        50029 => "data-item", 50030 => "document", 50032 => "window",
        50033 => "pane", 50036 => "table", _ => "control",
    };

    private static string SafeString(Func<string?> read)
    {
        try { return read()?.Trim() ?? string.Empty; } catch { return string.Empty; }
    }

    private static bool SafeBool(Func<bool> read, bool fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    private static string OneLine(string value, int max)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max] + "...";
    }

    private readonly record struct PhysicalRect(int Left, int Top, int Width, int Height)
    {
        public int CenterX => Left + Width / 2;
        public int CenterY => Top + Height / 2;
    }
}
