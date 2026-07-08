// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Pulse.cs
//
// Animated status-dot Storyboard that runs while the agent is working.
// Toggled by SetResponding — small ambient signal that something is
// happening without needing a banner.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Vantage;

public sealed partial class MainWindow
{
    private Storyboard? _pulseStoryboard;

    private void StartRunStatusPulse()
    {
        if (_pulseStoryboard is { }) return;
        if (RunStatusPulse is null) return;
        var sb = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
        };
        var fadeOut = new DoubleAnimation
        {
            From = 1.0, To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(1100)),
            AutoReverse = false,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fadeOut, RunStatusPulse);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        sb.Children.Add(fadeOut);
        sb.Begin();
        _pulseStoryboard = sb;
    }

    private void StopRunStatusPulse()
    {
        if (_pulseStoryboard is null) return;
        try { _pulseStoryboard.Stop(); } catch { }
        _pulseStoryboard = null;
        if (RunStatusPulse is not null) RunStatusPulse.Opacity = 0.5;
    }
}
