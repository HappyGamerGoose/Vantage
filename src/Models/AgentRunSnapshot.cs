// SPDX-License-Identifier: MIT
// Vantage — Models/AgentRunSnapshot.cs
//
// Serializable form of an AgentRunViewModel. The live view model
// (AgentRunViewModel) is tied to the UI thread and holds live state
// like a DispatcherQueueTimer for the live-elapsed clock; it can't
// be persisted as-is. The snapshot captures everything the chat
// history needs to reconstruct the visualization on next launch —
// header, status, terminal state, phase list, counter strip, and a
// frozen elapsed-time value so the rendered duration matches what
// the user saw at the moment the run finished.
//
// Snapshots are derived from the view model at save time (see
// AgentRunViewModel.CreateSnapshot) and reconstructed back into a
// view model at load time (AgentRunViewModel.FromSnapshot). The
// JSON shape is intentionally flat and uses only primitive types so
// it round-trips cleanly through System.Text.Json with no custom
// converters.

using System;
using System.Collections.Generic;

namespace Vantage.Models;

public sealed class AgentRunSnapshot
{
    /// <summary>Heading line shown in the agent-run header card.</summary>
    public string HeaderTitle { get; set; } = "Working on your task";

    /// <summary>Subheading / current action blurb.</summary>
    public string StatusText { get; set; } = "";

    /// <summary>Compact evidence/safety line shown on the run card.</summary>
    public string EvidenceSummary { get; set; } = "";

    /// <summary>True once the run has ended (success / failure / cancel).</summary>
    public bool IsFinished { get; set; }

    /// <summary>"done in 5 steps" / "failed at step 3: …" / "Stopped."</summary>
    public string? TerminationLabel { get; set; }

    /// <summary>"done" | "fail" — drives the termination-card icon + color.</summary>
    public string? TerminationKind { get; set; }

    /// <summary>Number of completed phases. Backs the progress bar.</summary>
    public int StepsCompleted { get; set; }

    /// <summary>Run start time (UTC).</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Total run duration in milliseconds, captured at finish. The
    /// reconstructed view model freezes its ElapsedLabel to this
    /// value rather than computing "now - StartedAt" (which would
    /// drift into the past on every launch).
    /// </summary>
    public long FinalDurationMs { get; set; }

    /// <summary>Ordered list of phases (the stepper rows).</summary>
    public List<PhaseSnapshot> Phases { get; set; } = new();

    /// <summary>Counter strip (e.g. "8 click · 14 key · 2 wait").</summary>
    public List<CounterSnapshot> Counters { get; set; } = new();
}

public sealed class PhaseSnapshot
{
    public int Index { get; set; }
    public PhaseKind Kind { get; set; }
    public PhaseKind? Counter { get; set; }
    public PhaseStatus Status { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class CounterSnapshot
{
    /// <summary>
    /// Store the Kind (not the rendered Glyph / Label) so the
    /// view model can re-derive them at restore time using the
    /// same helper methods — keeps the snapshot independent of
    /// the current glyph mapping.
    /// </summary>
    public PhaseKind Kind { get; set; }
    public int Count { get; set; }
}
