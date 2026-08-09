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

    /// <summary>Compact trust/evidence line inspired by operator consoles.</summary>
    private string _evidenceSummary = "Direct desktop control · screenshots stay bounded";
    public string EvidenceSummary { get => _evidenceSummary; set => Set(ref _evidenceSummary, value); }

    /// <summary>Elapsed time clock — refreshed by the tick timer.</summary>
    public string ElapsedLabel
    {
        get
        {
            // For a finished run restored from a snapshot, the
            // duration is frozen to FinalDurationMs so the displayed
            // time doesn't drift forward on every app launch. For a
            // live run, FinalDurationMs is null and we compute from
            // wall-clock against _startedAt.
            var elapsed = _finalDurationMs.HasValue
                ? TimeSpan.FromMilliseconds(_finalDurationMs.Value)
                : DateTimeOffset.UtcNow - _startedAt;
            return FormatElapsed(elapsed);
        }
    }

    /// <summary>True once the run has ended.</summary>
    private bool _isFinished;
    public bool IsFinished
    {
        get => _isFinished;
        set
        {
            if (Set(ref _isFinished, value))
            {
                if (value)
                {
                    _finalDurationMs ??= Math.Max(
                        0,
                        (long)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds);
                    _tickTimer?.Stop();
                    OnPropertyChanged(nameof(ElapsedLabel));
                }
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
    /// Total run duration in milliseconds, captured when the run
    /// finishes. For a live run this stays null and ElapsedLabel
    /// computes "now - StartedAt" so the clock ticks; for a run
    /// restored from a snapshot, the value is populated and the
    /// ElapsedLabel is frozen to that duration (so the user sees
    /// the same "47s" they saw at the moment the run ended, not a
    /// duration that drifts upward forever on every relaunch).
    /// </summary>
    private long? _finalDurationMs;
    public long? FinalDurationMs
    {
        get => _finalDurationMs;
        set { if (Set(ref _finalDurationMs, value)) OnPropertyChanged(nameof(ElapsedLabel)); }
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
                Kind  = g.Key,
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

    /// <summary>
    /// Capture the view model's current state as a serializable
    /// snapshot. Called from the chat-history save path so a future
    /// load can rebuild the visualization. The live tick timer is
    /// deliberately not captured — a restored view model gets a
    /// fresh (stopped) tick timer because the run is by definition
    /// over.
    /// </summary>
    public AgentRunSnapshot CreateSnapshot() => new()
    {
        HeaderTitle    = _headerTitle,
        StatusText     = _statusText,
        EvidenceSummary = _evidenceSummary,
        IsFinished     = _isFinished,
        TerminationLabel = _terminationLabel,
        TerminationKind  = _terminationKind,
        StepsCompleted  = _stepsCompleted,
        StartedAt      = _startedAt,
        FinalDurationMs = _finalDurationMs ?? (long)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds,
        Phases = Phases.Select(p => new PhaseSnapshot
        {
            Index      = p.Index,
            Kind       = p.Kind,
            Counter    = p.Counter,
            Status     = p.Status,
            Title      = p.Title,
            Subtitle   = p.Subtitle,
            StartedAt  = p.StartedAt,
            FinishedAt = p.FinishedAt,
        }).ToList(),
        Counters = Counters.Select(c => new CounterSnapshot
        {
            Kind  = c.Kind,
            Count = c.Count,
        }).ToList(),
    };

    /// <summary>
    /// Rebuild a finished-run view model from a persisted snapshot.
    /// The rebuilt VM is "frozen": IsFinished is true, the tick timer
    /// is created but immediately stopped (no clock drift), and
    /// FinalDurationMs is set so the ElapsedLabel renders the
    /// original duration rather than "now - StartedAt". The Kind
    /// enum stored on each counter is run through the same
    /// CounterGlyph / CounterLabel helpers used at runtime, so a
    /// future glyph-mapping change automatically applies to old
    /// snapshots.
    /// </summary>
    public static AgentRunViewModel FromSnapshot(AgentRunSnapshot snap, Action<Action> marshal)
    {
        var vm = new AgentRunViewModel(marshal)
        {
            HeaderTitle      = snap.HeaderTitle,
            StatusText       = snap.StatusText,
            EvidenceSummary  = string.IsNullOrWhiteSpace(snap.EvidenceSummary)
                ? "Direct desktop control · screenshots stay bounded"
                : snap.EvidenceSummary,
            IsFinished       = snap.IsFinished,
            TerminationLabel = snap.TerminationLabel,
            TerminationKind  = snap.TerminationKind,
            StepsCompleted   = snap.StepsCompleted,
            FinalDurationMs  = snap.FinalDurationMs,
        };
        // _startedAt is a private field (no public setter — it's
        // captured once per run, never mutated at runtime), so we
        // assign it directly here. The restored view model only
        // uses it for diagnostics / display, since ElapsedLabel is
        // frozen to FinalDurationMs.
        vm._startedAt = snap.StartedAt;
        // A restored run is by definition over — the tick timer
        // created in the constructor starts running, stop it
        // immediately so ElapsedLabel stays frozen.
        vm._tickTimer?.Stop();

        foreach (var ps in snap.Phases)
        {
            vm.Phases.Add(new PhaseRecord
            {
                Index     = ps.Index,
                Kind      = ps.Kind,
                Counter   = ps.Counter,
                Status    = ps.Status,
                Title     = ps.Title,
                Subtitle  = ps.Subtitle,
                StartedAt = ps.StartedAt,
                FinishedAt = ps.FinishedAt,
            });
        }
        foreach (var cs in snap.Counters)
        {
            vm.Counters.Add(new CounterRow
            {
                Kind  = cs.Kind,
                Glyph = CounterGlyph(cs.Kind),
                Label = CounterLabel(cs.Kind),
                Count = cs.Count,
            });
        }
        return vm;
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

public sealed class CounterRow
{
    /// <summary>The kind this counter aggregates. Needed for snapshot round-trip.</summary>
    public PhaseKind Kind { get; init; }

    /// <summary>Segoe Fluent glyph for the counter pill.</summary>
    public string Glyph { get; init; } = "\uE91F";

    /// <summary>Short label for the counter pill ("click" / "key" / "wait" / …).</summary>
    public string Label { get; init; } = "";

    public int Count { get; init; }
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
