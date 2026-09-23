// Ported from the Replay Analyser (EchoAnalyser.App/ViewModels/CareerVm.cs). Unchanged apart from
// theme tones in place of brushes.
#nullable enable

using System.Collections.Generic;
using System.Linq;
using Spark.ReplayAnalyser.Analysis;
using Spark.ReplayAnalyser.Controls;

namespace Spark.ReplayAnalyser.ViewModels;

/// <summary>One measured habit, ready to bind.</summary>
public sealed class MetricVm
{
    public MetricVm(CareerMetric m)
    {
        Source = m;
        VerdictTone = m.Edge switch
        {
            >= 0.12 => Tone.Good,
            > -0.12 => Tone.Dim,
            _ => Tone.Bad,
        };
        TrendTone = m.TrendEdge switch
        {
            >= 0.12 => Tone.Good,
            > -0.12 => Tone.Faint,
            _ => Tone.Bad,
        };
    }

    public CareerMetric Source { get; }
    public string Name => Source.Name;
    public string Description => Source.Description;
    public string ValueText => Source.ValueText;
    public string PeerText => Source.PeerText;
    public double Edge => Source.Edge;
    public string Verdict => Source.Verdict;
    public string TrendText => Source.TrendText;
    public Tone VerdictTone { get; }
    public Tone TrendTone { get; }
}

/// <summary>Display wrapper for a whole career profile.</summary>
public sealed class CareerVm
{
    public CareerVm(CareerProfile p)
    {
        Profile = p;
        Metrics = p.Metrics.OrderByDescending(m => m.Edge).Select(m => new MetricVm(m)).ToList();
        Insights = p.Insights.Select(i => new InsightVm(i)).ToList();
    }

    public CareerProfile Profile { get; }
    public string Player => Profile.Player;
    public string Summary => Profile.Summary;
    public IReadOnlyList<MetricVm> Metrics { get; }
    public IReadOnlyList<InsightVm> Insights { get; }

    public string MatchesText => $"{Profile.Matches}";
    public string MinutesText => $"{Profile.LiveMinutes:F0}";
    public string PointsText => $"{Profile.GoalPoints}";
    public string GoalsText => $"{Profile.Goals}";
    public string AssistsText => $"{Profile.Assists}";
    public string SavesText => $"{Profile.Saves}";
    public string StunsText => $"{Profile.Stuns}";

    public string MissedGrabsText => $"{Profile.MissedGrabs}";
    public string GrabChancesText => $"{Profile.GrabChances}";
    public string GrabRateText => Profile.GrabChances == 0
        ? "–"
        : $"{1 - (double)Profile.MissedGrabs / Profile.GrabChances:P0}";
    public string CarriedIntoStunText => $"{Profile.CarriedIntoStun}";
    public string TurnoversAtHomeText => $"{Profile.TurnoversAtHome}";
    public string TurnoversText => $"{Profile.Turnovers}";

    public string SpanText => Profile.FirstSeen.Year > 2000
        ? $"{Profile.FirstSeen:d MMM yyyy} – {Profile.LastSeen:d MMM yyyy}"
        : "";
}
