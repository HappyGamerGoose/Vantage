// SPDX-License-Identifier: MIT
// Vantage — Models/AgentRunViewModel.cs
//
// Live view-model that backs the new agent-run visualization in
// MainWindow.xaml. Replaces the old "text-only step log" with a
// structured, observable state so XAML bindings can drive:
//
//   - A "running" pill in the header (animated dot)
//   - A progress bar that ticks forward as phases complete
//   - A vertical stepper — every step lights up as it finishes
//   - A live counter strip (clicks / keys / types / waits / scroll / llm
//     tokens / errors) for at-a-glance activity
//   - A termination card (success / halted / error) at run completion
//
// Hooks call Mutate() to push mutations through the UI dispatcher so
// bindings update on the UI thread. ObservableCollection<PhaseRecord>
// drives an ItemsControl; PhaseRecord is itself INotifyPropertyChanged
// so a phase's status / duration ticks live without rebuilding the list.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace Vantage.Models;

public sealed class AgentRunViewModel : INotifyPropertyChanged
{
    private readonly Action<Action> _marshal;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tickTimer;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private int _stepsCompleted;

    public AgentRunViewModel(Action<Action> marshal)
    {
        _marshal = marshal;
        try
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is { } q)
            {
                _tickTimer = q.CreateTimer();
                _tickTimer.Interval = TimeSpan.FromMilliseconds(500);
                _tickTimer.Tick += (s, e) => OnPropertyChanged(nameof(ElapsedLabel));
            }
        }
        catch
        {
            // Best-effort tick — elapsed time is decorative, not load-bearing.
        }
    }

    public ObservableCollection<PhaseRecord> Phases { get; } = new();

    /// <summary>Heading line: e.g. "Vantage is working on your task".</summary>
    private string _headerTitle = "Working on your task";
    public string HeaderTitle { get => _headerTitle; set => Set(ref _headerTitle, value); }

    /// <summary>Subheading: "Started 2 s ago" or current action blurb.</summary>
    private string _statusText = "Initializing…";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>Elapsed time clock — refreshed by the tick timer.</summary>
    public string ElapsedLabel => FormatElapsed(DateTimeOffset.UtcNow - _startedAt);

    /// <summary>True once the run has ended.</summary>
    private bool _isFinished;
    public bool IsFinished
    {
        get => _isFinished;
        set
        {
            if (Set(ref _isFinished, value))
            {
                if (value) _tickTimer?.Stop();
                else _tickTimer?.Start();
                OnPropertyChanged(nameof(IsFinishedVisibility));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressIndeterminate));
            }
        }
    }

    /// <summary>"done in 4 steps · 47s" / "failed at step 5 · quota exhausted".</summary>
    private string? _terminationLabel;
    public string? TerminationLabel
    {
        get => _terminationLabel;
        set { if (Set(ref _terminationLabel, value)) OnPropertyChanged(nameof(IsFinishedVisibility)); }
    }

    /// <summary>success | fail | stopped.</summary>
    private string? _terminationKind;
    public string? TerminationKind { get => _terminationKind; set => Set(ref _terminationKind, value); }

    /// <summary>Steps completed. Backs the progress bar.</summary>
    public int StepsCompleted
    {
        get => _stepsCompleted;
        set { if (Set(ref _stepsCompleted, value)) { OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(ProgressIndeterminate)); } }
    }

    /// <summary>
    /// When the run is still going the agent doesn't know the total,
    /// so the bar shows an indeterminate shimmer (Windows-accent style).
    /// Determinate value kicks in only when the run is finished —
    /// ProgressPercent then equals 100 (success) or lastValue/total (fail).
    /// </summary>
    public bool ProgressIndeterminate => !IsFinished;
    public double ProgressPercent => IsFinished && StepsCompleted > 0 ? 100 : 0;

    /// <summary>Counter strip — built from the run history.</summary>
    public ObservableCollection<CounterRow> Counters { get; } = new();
    public Visibility CountersVisibility => Counters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Show the termination card only once the run is finished.</summary>
    public Visibility IsFinishedVisibility =>
        IsFinished && !string.IsNullOrEmpty(_terminationLabel)
            ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Run a mutation on the UI thread, then refresh dependent props.</summary>
    public void Mutate(Action<AgentRunViewModel> mutate) => _marshal(() => mutate(this));

    public void AddPhase(PhaseRecord phase) => Mutate(vm =>
    {
        vm.Phases.Add(phase);
        vm.StepsCompleted = vm.Phases.Count;
        vm.StatusText = phase.Title;
        UpdateCounters();
    });

    /// <summary>Mark the most recent running phase with a final status + duration.</summary>
    public void FinishLastRunning(PhaseStatus status)
    {
        Mutate(vm =>
        {
            if (vm.Phases.Count == 0) return;
            var last = vm.Phases[^1];
            if (last.Status != PhaseStatus.Running) return;
            last.Status = status;
            last.FinishedAt = DateTimeOffset.UtcNow;
            vm.StatusText = status == PhaseStatus.Failed ? "Failed" : "Done";
            vm.UpdateCounters();
        });
    }

    private void UpdateCounters()
    {
        // Group phases by their kind, surface top-N counters; recycles
        // the ObservableCollection so bindings see inserts/clears.
        var grouped = Phases.Where(p => p.Counter != null)
            .GroupBy(p => p.Counter!.Value)
            .Select(g => new CounterRow
            {
                Glyph = CounterGlyph(g.Key),
                Label = CounterLabel(g.Key),
                Count = g.Count(),
            });
        Counters.Clear();
        foreach (var c in grouped) Counters.Add(c);
    }

    public static string CounterGlyph(PhaseKind kind) => kind switch
    {
        PhaseKind.Click       => "\uE8C2", // mouse pointer
        PhaseKind.RightClick  => "\uE72B",
        PhaseKind.DoubleClick => "\uE8C2",
        PhaseKind.Type        => "\uE932", // pencil
        PhaseKind.Key         => "\uE765",
        PhaseKind.Scroll      => "\uE76C",
        PhaseKind.Wait        => "\uE916",
        PhaseKind.MoveMouse   => "\uE8C2",
        PhaseKind.LaunchApp   => "\uE8A7",
        PhaseKind.FocusApp    => "\uE8A7",
        PhaseKind.CloseApp    => "\uE8A7",
        PhaseKind.RunPowerShell => "\uE756",
        PhaseKind.ListProcesses => "\uE8FD",
        PhaseKind.KillProcess => "\uE74D",
        _ => "\uE91F",
    };

    public static string CounterLabel(PhaseKind kind) => kind switch
    {
        PhaseKind.Click       => "click",
        PhaseKind.RightClick  => "right-click",
        PhaseKind.DoubleClick => "double-click",
        PhaseKind.Type        => "chars",
        PhaseKind.Key         => "key",
        PhaseKind.Scroll      => "scroll",
        PhaseKind.Wait        => "wait",
        PhaseKind.MoveMouse   => "move",
        PhaseKind.LaunchApp   => "launch",
        PhaseKind.FocusApp    => "focus",
        PhaseKind.CloseApp    => "close",
        PhaseKind.RunPowerShell => "shell",
        PhaseKind.ListProcesses => "list",
        PhaseKind.KillProcess => "kill",
        _ => "step",
    };

    private static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "0s";
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }
}

public sealed class PhaseRecord : INotifyPropertyChanged
{
    public int Index { get; init; }
    public PhaseKind Kind { get; init; }
    public string IconGlyph { get; init; } = "\uE91F";

    /// <summary>Counter segment — null = don't show in counter strip.</summary>
    public PhaseKind? Counter { get; init; }

    private PhaseStatus _status = PhaseStatus.Running;
    public PhaseStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { OnPropertyChanged(nameof(StatusBrush)); OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(IsRunning)); } }
    }

    /// <summary>Short title — "Click · Start button" / "Press · Enter".</summary>
    public string Title { get; init; } = "";

    /// <summary>Subtitle — coordinate / extra detail; can be null.</summary>
    public string? Subtitle { get; init; }

    /// <summary>XAML binding hook — collapse the subtitle row when there's no detail to show.</summary>
    public Microsoft.UI.Xaml.Visibility HasSubtitle =>
        string.IsNullOrEmpty(Subtitle) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Live duration — updates while running so the user sees the step working.</summary>
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    public DateTimeOffset StartedAt
    {
        get => _startedAt;
        set { _startedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(DurationLabel)); }
    }

    private DateTimeOffset? _finishedAt;
    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        set
        {
            if (_finishedAt == value) return;
            _finishedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationLabel));
        }
    }

    public string DurationLabel
    {
        get
        {
            var end = FinishedAt ?? DateTimeOffset.UtcNow;
            var t = (long)(end - _startedAt).TotalMilliseconds;
            // No glyph for sub-50 ms phases — fast keypresses / click-dispatch
            // are common and an em-dash made the right edge look like a
            // separator column. We just leave it empty instead.
            if (t < 50) return "";
            if (t < 1000) return $"{t}ms";
            return $"{t / 1000.0:F1}s";
        }
    }

    public bool IsRunning => Status == PhaseStatus.Running;
    public bool IsDone    => Status == PhaseStatus.Done;
    public bool IsFailed  => Status == PhaseStatus.Failed;

    /// <summary>Brush key resolved by the DataTemplate binding.</summary>
    public string StatusBrush => Status switch
    {
        PhaseStatus.Done    => "AccentBrush",
        PhaseStatus.Failed  => "DangerBrush",
        PhaseStatus.Running => "AccentBrush",
        _                   => "SoftStrokeBrush",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null) { if (Equals(f, v)) return false; f = v; OnPropertyChanged(n); return true; }
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class CounterRow : INotifyPropertyChanged
{
    public string Glyph { get; init; } = "\uE91F";
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum PhaseKind
{
    Other,
    Click,
    RightClick,
    DoubleClick,
    Type,
    Key,
    Scroll,
    Wait,
    Screenshot,
    MoveMouse,
    LaunchApp,
    FocusApp,
    CloseApp,
    RunPowerShell,
    ListProcesses,
    KillProcess,
    Done,
    Fail,
}

public enum PhaseStatus
{
    Pending,
    Running,
    Done,
    Failed,
}
