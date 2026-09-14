// Ported from the Replay Analyser (EchoAnalyser.App/ViewModels/PlayerVm.cs). Figures and text are
// unchanged; colours are theme tones instead of brushes, so the views can follow Spark's theme.
#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Spark.ReplayAnalyser.Analysis;
using Spark.ReplayAnalyser.Controls;
using Spark.ReplayAnalyser.Model;

namespace Spark.ReplayAnalyser.ViewModels;

/// <summary>Display wrapper around one player's analysis for the tables and detail panes.</summary>
public sealed class PlayerVm : ObservableObject
{
    public PlayerVm(PlayerAnalysis p, MatchAnalysis match)
    {
        Player = p;
        Match = match;
    }

    public PlayerAnalysis Player { get; }
    public MatchAnalysis Match { get; }

    public string Name => Player.Name;
    public TeamSide Side => Player.Side;
    public bool IsBlue => Player.Side == TeamSide.Blue;
    public string TeamName => IsBlue ? "BLUE" : "ORANGE";
    public Tone Tone => IsBlue ? Tone.Blue : Tone.Orange;

    private bool _isSelected;
    /// <summary>Whether this is the player the detail pane is showing, for highlighting their row.</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public int Points => Player.GoalPoints;
    public int PersonalScore => Player.Points;
    public int Goals => Player.Goals;
    public int Assists => Player.Assists;
    public int Saves => Player.Saves;
    public int Stuns => Player.Stuns;
    /// <summary>Private matches report zero passes, so fall back to handovers we counted.</summary>
    public int Passes => Player.Passes > 0 ? Player.Passes : Player.Handovers;
    public int Catches => Player.Catches;
    public int Steals => Player.Steals;
    public int Blocks => Player.Blocks;
    public int Interceptions => Player.Interceptions;
    public int Turnovers => Player.Turnovers;
    public int ShotsTaken => Player.ShotsTaken;
    public int TimesStunned => Player.TimesStunned;

    public string MinutesText => $"{Player.SecondsPlayed / 60.0:F1}";
    public string PossessionText => $"{Player.PossessionTime:F0}s";
    public string HoldText => $"{Player.AveragePossessionSeconds:F1}s";
    public string AccuracyText => Player.ShotsTaken == 0 ? "–" : $"{Player.ShotAccuracy:P0}";
    public string DistanceText => $"{Player.DistanceTravelled:F0}m";
    public string SpeedText => $"{Player.AverageSpeed:F1}";
    public string PointsPerMinText => $"{Player.PointsPerMinute:F1}";
    public string DefShareText => $"{Player.DefensiveThirdShare:P0}";
    public string NeutralShareText => $"{Player.NeutralShare:P0}";
    public string OffShareText => $"{Player.OffensiveThirdShare:P0}";

    public bool HasThrowData => Player.HasThrowTelemetry && Player.Throws.Count > 0;
    public string ThrowSpeedText => HasThrowData ? $"{Player.AverageThrowSpeed:F1} m/s" : "not recorded";
    public string ThrowPeakText => HasThrowData ? $"{Player.MaxThrowSpeed:F1} m/s" : "–";
    public string ThrowCountText => HasThrowData ? $"{Player.Throws.Count}" : "–";
    public string WristPenaltyText => HasThrowData
        ? $"{Player.Throws.Average(t => t.WristThrowPenalty):P0}" : "–";
    public string OffAxisText => HasThrowData
        ? $"{Player.Throws.Average(t => t.OffAxisSpinDeg):F0}°" : "–";

    /// <summary>This player's movement, in their team colour.</summary>
    public IReadOnlyList<TrackLayer> Tracks => _tracks ??= new[]
    {
        new TrackLayer { Paths = Player.Tracks, Tone = Tone },
    };
    private IReadOnlyList<TrackLayer>? _tracks;
    public IReadOnlyList<Vector3> ShotPositions => Player.ShotPositions;
    public IReadOnlyList<Vector3> GoalPositions => Player.GoalPositions;

    public string PassTargetsText => Player.PassTargets.Count == 0
        ? "no completed handovers recorded"
        : string.Join("   ", Player.PassTargets.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} ×{kv.Value}"));

    /// <summary>The scoreboard summary line used at the top of the detail pane.</summary>
    public string HeadlineText =>
        $"{Points} pts · {Goals}G {Assists}A · {Saves} saves · {Stuns} stuns · {MinutesText} min live";

    public string CompactStats =>
        $"{Points} pts   {Goals}G {Assists}A   {Saves} SV   {Stuns} STN   {Turnovers} TO   {MinutesText} min";
}

/// <summary>One coaching insight, ready to bind.</summary>
public sealed class InsightVm
{
    public InsightVm(Insight i)
    {
        Source = i;
        Tone = i.Kind switch
        {
            InsightKind.Strength => Tone.Good,
            InsightKind.Weakness => Tone.Bad,
            InsightKind.Improvement => Tone.Warn,
            InsightKind.Counter => Tone.Accent,
            _ => Tone.Dim,
        };
    }

    public Insight Source { get; }
    public string Kind => Source.Kind.ToString().ToUpperInvariant();
    public string Area => Source.Area.ToString();
    public string Title => Source.Title;
    public string Detail => Source.Detail;
    public string? Action => Source.Action;
    public string? Evidence => Source.Evidence;
    public bool HasAction => !string.IsNullOrEmpty(Source.Action);
    public bool HasEvidence => !string.IsNullOrEmpty(Source.Evidence);
    public Tone Tone { get; }
}

/// <summary>One row in the goal timeline.</summary>
public sealed class GoalVm
{
    public GoalVm(GoalRecord g)
    {
        Source = g;
        Tone = g.Side == TeamSide.Blue ? Tone.Blue : Tone.Orange;
    }

    public GoalRecord Source { get; }
    public string TimeText => Source.Time.ToString(@"m\:ss");
    public string Scorer => Source.Scorer;
    public string Team => Source.Side == TeamSide.Blue ? "BLUE" : "ORANGE";
    public string PointsText => $"{Source.Points}pt";
    public string TypeText => Source.GoalType;
    public string AssistText => string.IsNullOrEmpty(Source.Assist) ? "" : $"assist {Source.Assist}";
    public string SpeedText => $"{Source.DiscSpeed:F1} m/s";
    public string DistanceText => $"{Source.DistanceThrown:F1} m";
    public string ScoreText => $"{Source.BlueScore}–{Source.OrangeScore}";
    public Tone Tone { get; }

    /// <summary>Everything under the scorer's name on one line, skipping whatever wasn't recorded.</summary>
    public string DetailText => string.Join(" · ",
        new[] { TypeText, PointsText, AssistText, DistanceText }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// A whole-roster entry: every player in the lobby gets one, so a team can read its own
/// review without clicking through the roster one name at a time.
/// </summary>
public sealed class PlayerCardVm
{
    public PlayerCardVm(PlayerVm player, PlayerReport report)
    {
        Player = player;
        Summary = report.Summary;
        Rating = report.OverallRating;

        // Lead with what they did well, then the single clearest thing to work on.
        Highlights = report.Insights
            .Where(i => i.Kind == InsightKind.Strength)
            .OrderByDescending(i => i.Confidence).Take(2)
            .Select(i => new InsightVm(i)).ToList();
        Fixes = report.Insights
            .Where(i => i.Kind is InsightKind.Weakness or InsightKind.Improvement)
            .OrderByDescending(i => i.Confidence).Take(2)
            .Select(i => new InsightVm(i)).ToList();

        var best = report.AreaScores.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var worst = report.AreaScores.OrderBy(kv => kv.Value).FirstOrDefault();
        BestArea = report.AreaScores.Count == 0 ? "" : best.Key.ToString();
        WorstArea = report.AreaScores.Count == 0 ? "" : worst.Key.ToString();
    }

    public PlayerVm Player { get; }
    public string Name => Player.Name;
    public Tone Tone => Player.Tone;
    public string CompactStats => Player.CompactStats;
    public string Summary { get; }
    public double Rating { get; }
    public string RatingText => $"{Rating:F0}";
    public string BestArea { get; }
    public string WorstArea { get; }
    public bool HasBestArea => BestArea.Length > 0;
    public IReadOnlyList<InsightVm> Highlights { get; }
    public IReadOnlyList<InsightVm> Fixes { get; }
    public bool HasAnything => Highlights.Count > 0 || Fixes.Count > 0;
}
