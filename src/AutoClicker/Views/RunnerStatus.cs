using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using AutoClicker.Core;

namespace AutoClicker.Views;

/// <summary>
/// What the menu and the overlay show about one runner. A plain value, so the
/// preview renderer can feed the windows made-up states without an engine.
/// <paramref name="Detail"/> replaces the default "rate · count" text while the
/// runner is working (the camera and the random macro have nothing like a rate).
/// </summary>
internal readonly record struct RunnerStatus(bool Running, bool Paused, bool Hold, long Count, double Cps, string? Detail = null)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static RunnerStatus Of(Runner runner, TimingSettings settings) =>
        new(runner.IsRunning, runner.IsPaused, settings.Mode == RunMode.Hold, runner.Count, runner.ActualCps);

    /// <summary>Camera: time left until the next nudge and how many were made.</summary>
    public static RunnerStatus OfCamera(Runner runner, CameraSettings settings, NudgeClock clock)
    {
        long count = runner.Count;
        double? left = clock.SecondsToNext(runner.IsRunning && !runner.IsPaused, count, settings);
        string? detail = left is null ? null
            : left < 1 ? $"сдвиг · {Number(count)}"
            : $"через ~{Math.Ceiling(left.Value):0} с · {Number(count)}";
        return new(runner.IsRunning, runner.IsPaused, false, count, 0, detail);
    }

    /// <summary>Random macro: it either wanders or it doesn't; show the number of moves.</summary>
    public static RunnerStatus OfRandomMove(Runner runner)
    {
        long count = runner.Count;
        return new(runner.IsRunning, runner.IsPaused, false, count, 0, $"работает · {Number(count)}");
    }

    public static string Number(long value) => value.ToString("N0", Ru);

    /// <summary>One short line. <paramref name="unit"/> is the per-second unit, e.g. "к/с".</summary>
    public string Describe(string unit, string idle, string held = "")
    {
        if (!Running) return idle;
        if (Paused) return "пауза — другое окно";
        if (Count == 0) return "запуск…";
        if (Detail is not null) return Detail;
        if (Hold) return held;
        return $"{Cps.ToString("0.0", Ru)} {unit} · {Number(Count)}";
    }

    public Brush Color(Brush idle, Brush running, Brush paused) =>
        !Running ? idle : Paused ? paused : running;
}

/// <summary>The four status suppliers the windows poll.</summary>
internal sealed record StatusSources(
    Func<RunnerStatus> Mouse,
    Func<RunnerStatus> Keyboard,
    Func<RunnerStatus> Camera,
    Func<RunnerStatus> RandomMove);

/// <summary>
/// Estimates when the camera runner will nudge next. The engine does not expose
/// its schedule, so this watches the counter: a nudge that has just been counted
/// started one nudge-length ago, and the next one starts an interval after that.
/// The random spread is unknown, hence the "~" in the text.
/// </summary>
internal sealed class NudgeClock
{
    private long _count = -1;
    private long _countedAt;

    public double? SecondsToNext(bool active, long count, CameraSettings s)
    {
        if (!active || count == 0)
        {
            _count = -1;
            return null;
        }

        long now = Stopwatch.GetTimestamp();
        if (count != _count)
        {
            _count = count;
            _countedAt = now;
        }

        double nudge = (2 * s.MoveMs + s.HoldMs) / 1000.0;
        double sinceCounted = (now - _countedAt) / (double)Stopwatch.Frequency;
        return Math.Max(0, s.IntervalMs / 1000.0 - nudge - sinceCounted);
    }
}
