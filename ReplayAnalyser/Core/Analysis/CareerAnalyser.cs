// Ported from the Replay Analyser (EchoAnalyser.Core/Analysis/CareerAnalyser.cs). Logic is unchanged; only the
// namespace, explicit usings and nullable context differ, so fixes can be diffed against
// the original.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spark.ReplayAnalyser.Model;

namespace Spark.ReplayAnalyser.Analysis;

/// <summary>How a single measured habit compares to the people the player actually played with.</summary>
public sealed class CareerMetric
{
    public string Name { get; init; } = "";
    /// <summary>What the number means, in the player's terms.</summary>
    public string Description { get; init; } = "";
    public double Value { get; init; }
    /// <summary>The same figure for everyone else in the same lobbies.</summary>
    public double PeerValue { get; init; }
    /// <summary>True when a smaller number is the better one.</summary>
    public bool LowerIsBetter { get; init; }
    public string Format { get; init; } = "0.00";

    /// <summary>Signed edge over peers, normalised: +1 means twice as good, -1 twice as bad.</summary>
    public double Edge
    {
        get
        {
            if (PeerValue <= 0 && Value <= 0) return 0;
            if (PeerValue <= 0) return LowerIsBetter ? -1 : 1;
            double ratio = Value / PeerValue;
            double signed = LowerIsBetter ? 1 - ratio : ratio - 1;
            return Math.Clamp(signed, -1.5, 1.5);
        }
    }

    /// <summary>Change from the earliest third of their history to the most recent third.</summary>
    public double EarlyValue { get; init; }
    public double RecentValue { get; init; }

    public double TrendEdge
    {
        get
        {
            if (EarlyValue <= 0) return 0;
            double ratio = RecentValue / EarlyValue;
            double signed = LowerIsBetter ? 1 - ratio : ratio - 1;
            return Math.Clamp(signed, -1.5, 1.5);
        }
    }

    public string Verdict => Edge switch
    {
        >= 0.35 => "Strong",
        >= 0.12 => "Above the room",
        > -0.12 => "About average",
        > -0.35 => "Below the room",
        _ => "Weak",
    };

    public string TrendText => Math.Abs(TrendEdge) < 0.12
        ? "steady"
        : TrendEdge > 0 ? "improving" : "slipping";

    public string ValueText => Value.ToString(Format);
    public string PeerText => PeerValue.ToString(Format);
}

/// <summary>Everything we can say about one player across their whole indexed library.</summary>
public sealed class CareerProfile
{
    public string Player { get; init; } = "";
    public int Matches { get; init; }
    public double LiveMinutes { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }

    public int GoalPoints { get; init; }
    public int Goals { get; init; }
    public int Assists { get; init; }
    public int Saves { get; init; }
    public int Stuns { get; init; }
    public int MissedGrabs { get; init; }
    public int GrabChances { get; init; }
    public int CarriedIntoStun { get; init; }
    public int TurnoversAtHome { get; init; }
    public int Turnovers { get; init; }

    public List<CareerMetric> Metrics { get; } = new();
    public List<Insight> Insights { get; } = new();
    public string Summary { get; set; } = "";
    /// <summary>Matches in the order they were played, for charting form.</summary>
    public List<MatchSummary> Timeline { get; } = new();

    public IEnumerable<CareerMetric> Strengths =>
        Metrics.Where(x => x.Edge >= 0.12).OrderByDescending(x => x.Edge);
    public IEnumerable<CareerMetric> Weaknesses =>
        Metrics.Where(x => x.Edge <= -0.12).OrderBy(x => x.Edge);
}

/// <summary>
/// Reads a player's whole indexed library and says what they are good at, what they are not,
/// and what has changed.
///
/// Every judgement is made against the other players in the same lobbies rather than against
/// fixed numbers. Someone in high-level scrims will look worse on absolute stats than someone
/// farming beginners, and the point of the report is to be useful to both.
/// </summary>
public static class CareerAnalyser
{
    /// <summary>Minimum live seconds in a match before it counts towards the career view.</summary>
    private const double MinSecondsPerMatch = 60;

    public static IReadOnlyList<string> KnownPlayers(LibraryIndex index, int minMatches = 3)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in index.Entries.Values.Where(e => e.Ok))
            foreach (var p in m.Players.Where(p => p.SecondsPlayed >= MinSecondsPerMatch))
                counts[p.Name] = counts.GetValueOrDefault(p.Name) + 1;

        return counts.Where(kv => kv.Value >= minMatches)
                     .OrderByDescending(kv => kv.Value)
                     .Select(kv => kv.Key)
                     .ToList();
    }

    public static CareerProfile? Build(LibraryIndex index, string player)
    {
        var rows = new List<(MatchSummary match, PlayerSummary me, List<PlayerSummary> peers)>();

        foreach (var m in index.Entries.Values.Where(e => e.Ok).OrderBy(e => e.Recorded))
        {
            var me = m.Players.FirstOrDefault(p =>
                string.Equals(p.Name, player, StringComparison.OrdinalIgnoreCase) &&
                p.SecondsPlayed >= MinSecondsPerMatch);
            if (me == null) continue;

            var peers = m.Players
                .Where(p => p.SecondsPlayed >= MinSecondsPerMatch && p.Name != me.Name)
                .ToList();
            rows.Add((m, me, peers));
        }

        if (rows.Count == 0) return null;

        double liveSeconds = rows.Sum(r => r.me.SecondsPlayed);
        var profile = new CareerProfile
        {
            Player = rows[^1].me.Name,
            Matches = rows.Count,
            LiveMinutes = liveSeconds / 60.0,
            FirstSeen = rows[0].match.Recorded,
            LastSeen = rows[^1].match.Recorded,
            GoalPoints = rows.Sum(r => r.me.GoalPoints),
            Goals = rows.Sum(r => r.me.Goals),
            Assists = rows.Sum(r => r.me.Assists),
            Saves = rows.Sum(r => r.me.Saves),
            Stuns = rows.Sum(r => r.me.Stuns),
            MissedGrabs = rows.Sum(r => r.me.MissedGrabs),
            GrabChances = rows.Sum(r => r.me.GrabChances),
            CarriedIntoStun = rows.Sum(r => r.me.CarriedIntoStun),
            TurnoversAtHome = rows.Sum(r => r.me.TurnoversAtHome),
            Turnovers = rows.Sum(r => r.me.Turnovers),
        };
        profile.Timeline.AddRange(rows.Select(r => r.match));

        BuildMetrics(profile, rows);
        BuildInsights(profile);
        profile.Summary = Describe(profile);
        return profile;
    }

    // ------------------------------------------------------------------ metrics

    private static void BuildMetrics(
        CareerProfile profile,
        List<(MatchSummary match, PlayerSummary me, List<PlayerSummary> peers)> rows)
    {
        // The first and last thirds of their history, for the trend.
        int third = Math.Max(1, rows.Count / 3);
        var early = rows.Take(third).ToList();
        var recent = rows.Skip(Math.Max(0, rows.Count - third)).ToList();

        void Add(string name, string description, Func<PlayerSummary, double> pick,
                 bool lowerIsBetter = false, string format = "0.00")
        {
            profile.Metrics.Add(new CareerMetric
            {
                Name = name,
                Description = description,
                LowerIsBetter = lowerIsBetter,
                Format = format,
                Value = WeightedMean(rows.Select(r => (pick(r.me), r.me.SecondsPlayed))),
                PeerValue = WeightedMean(rows.SelectMany(r => r.peers.Select(p => (pick(p), p.SecondsPlayed)))),
                EarlyValue = WeightedMean(early.Select(r => (pick(r.me), r.me.SecondsPlayed))),
                RecentValue = WeightedMean(recent.Select(r => (pick(r.me), r.me.SecondsPlayed))),
            });
        }

        Add("Scoring", "Points you put on the board per minute of live play",
            p => p.PointsPerMinute);
        Add("Finishing", "Share of your shots that go in",
            p => p.ShotsTaken == 0 ? 0 : (double)p.Goals / p.ShotsTaken, format: "0%");
        Add("Playmaking", "Assists per minute",
            p => p.SecondsPlayed <= 0 ? 0 : p.Assists / (p.SecondsPlayed / 60.0));
        Add("Distribution", "Handovers to team-mates per minute",
            p => p.SecondsPlayed <= 0 ? 0 : Math.Max(p.Passes, p.Handovers) / (p.SecondsPlayed / 60.0));
        Add("Saves", "Saves per minute",
            p => p.SecondsPlayed <= 0 ? 0 : p.Saves / (p.SecondsPlayed / 60.0));
        Add("Physicality", "Stuns landed per minute",
            p => p.StunsPerMinute);
        Add("Work rate", "Metres skated per minute",
            p => p.SecondsPlayed <= 0 ? 0 : p.DistanceTravelled / (p.SecondsPlayed / 60.0), format: "0");
        Add("Hands", "Share of reachable loose discs you actually take",
            p => p.GrabSuccessRate, format: "0%");
        Add("Missed grabs", "Loose discs within reach that you did not take, per minute",
            p => p.MissedGrabsPerMinute, lowerIsBetter: true);
        Add("Disc security", "Turnovers per minute",
            p => p.TurnoversPerMinute, lowerIsBetter: true);
        Add("Turnovers at home", "Giveaways inside your own third, per minute",
            p => p.SecondsPlayed <= 0 ? 0 : p.TurnoversAtHome / (p.SecondsPlayed / 60.0),
            lowerIsBetter: true);
        Add("Carrying into contact", "Times stunned out of possession, per minute",
            p => p.SecondsPlayed <= 0 ? 0 : p.CarriedIntoStun / (p.SecondsPlayed / 60.0),
            lowerIsBetter: true);
    }

    private static double WeightedMean(IEnumerable<(double value, double weight)> items)
    {
        double sum = 0, weight = 0;
        foreach (var (v, w) in items)
        {
            if (w <= 0 || double.IsNaN(v)) continue;
            sum += v * w;
            weight += w;
        }
        return weight <= 0 ? 0 : sum / weight;
    }

    // ------------------------------------------------------------------ insights

    private static void BuildInsights(CareerProfile p)
    {
        foreach (var m in p.Strengths.Take(3))
            p.Insights.Add(new Insight
            {
                Kind = InsightKind.Strength,
                Area = AreaFor(m.Name),
                Title = $"{m.Name} is your edge",
                Detail = $"{m.ValueText} against {m.PeerText} for everyone else you play with — "
                       + $"{Describe(m.Edge)}. This has been {m.TrendText} over your history.",
                Evidence = $"{m.ValueText} vs {m.PeerText}",
                Confidence = 0.5 + Math.Min(0.45, Math.Abs(m.Edge) / 2),
            });

        foreach (var m in p.Weaknesses.Take(3))
            p.Insights.Add(new Insight
            {
                Kind = InsightKind.Weakness,
                Area = AreaFor(m.Name),
                Title = $"{m.Name} is costing you",
                Detail = $"{m.ValueText} against {m.PeerText} for everyone else in your lobbies — "
                       + $"{Describe(m.Edge)}. This has been {m.TrendText}.",
                Action = ActionFor(m.Name),
                Evidence = $"{m.ValueText} vs {m.PeerText}",
                Confidence = 0.5 + Math.Min(0.45, Math.Abs(m.Edge) / 2),
            });

        // Direction of travel matters as much as the level.
        var slipping = p.Metrics.Where(m => m.TrendEdge <= -0.2).OrderBy(m => m.TrendEdge).FirstOrDefault();
        if (slipping != null)
            p.Insights.Add(new Insight
            {
                Kind = InsightKind.Improvement,
                Area = AreaFor(slipping.Name),
                Title = $"{slipping.Name} has gone backwards",
                Detail = $"Your first matches averaged {slipping.EarlyValue.ToString(slipping.Format)}; "
                       + $"your recent ones {slipping.RecentValue.ToString(slipping.Format)}.",
                Action = ActionFor(slipping.Name),
                Evidence = $"{slipping.EarlyValue.ToString(slipping.Format)} → {slipping.RecentValue.ToString(slipping.Format)}",
                Confidence = 0.65,
            });

        var climbing = p.Metrics.Where(m => m.TrendEdge >= 0.25).OrderByDescending(m => m.TrendEdge).FirstOrDefault();
        if (climbing != null)
            p.Insights.Add(new Insight
            {
                Kind = InsightKind.Note,
                Area = AreaFor(climbing.Name),
                Title = $"{climbing.Name} is climbing",
                Detail = $"Up from {climbing.EarlyValue.ToString(climbing.Format)} to "
                       + $"{climbing.RecentValue.ToString(climbing.Format)} across your library. Keep doing whatever changed.",
                Evidence = $"{climbing.EarlyValue.ToString(climbing.Format)} → {climbing.RecentValue.ToString(climbing.Format)}",
                Confidence = 0.6,
            });

        if (p.GrabChances >= 30)
        {
            var hands = p.Metrics.First(m => m.Name == "Hands");
            p.Insights.Add(new Insight
            {
                Kind = hands.Edge >= 0 ? InsightKind.Note : InsightKind.Improvement,
                Area = InsightArea.Possession,
                Title = "Hands, in raw numbers",
                Detail = $"{p.GrabChances - p.MissedGrabs} of {p.GrabChances} loose discs taken when one was "
                       + $"within reach ({hands.ValueText}); the room manages {hands.PeerText}.",
                Action = hands.Edge < 0
                    ? "Meet the disc with the hand already open rather than reaching late. Most misses are a hand arriving after the disc has passed."
                    : null,
                Evidence = $"{p.MissedGrabs} missed",
                Confidence = 0.7,
            });
        }

        p.Insights.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
    }

    private static string Describe(double edge) => Math.Abs(edge) switch
    {
        >= 0.5 => edge > 0 ? "far ahead of the room" : "far behind the room",
        >= 0.25 => edge > 0 ? "clearly better" : "clearly worse",
        >= 0.12 => edge > 0 ? "a little better" : "a little worse",
        _ => "about the same",
    };

    private static InsightArea AreaFor(string metric) => metric switch
    {
        "Scoring" or "Finishing" => InsightArea.Finishing,
        "Playmaking" or "Distribution" => InsightArea.Passing,
        "Saves" => InsightArea.Defence,
        "Physicality" or "Carrying into contact" => InsightArea.Physicality,
        "Work rate" => InsightArea.Movement,
        "Hands" or "Missed grabs" => InsightArea.Possession,
        _ => InsightArea.Discipline,
    };

    private static string? ActionFor(string metric) => metric switch
    {
        "Scoring" => "Get closer before releasing. Most low-scoring records are shot selection, not shot power.",
        "Finishing" => "Stop taking the first shot available. One more pass to get inside the three-line converts far better.",
        "Playmaking" => "Look up before you carry. The pass out and back beats driving into a set defence.",
        "Distribution" => "Use your team-mates as a release valve instead of holding until you are covered.",
        "Saves" => "Start a step off the line so you can read the throw rather than committing early.",
        "Physicality" => "Body the carrier rather than chasing the disc — a stun buys your team more than a missed intercept.",
        "Work rate" => "You are covering less ground than the room. Reset your position after every exchange instead of watching the play.",
        "Hands" => "Meet the disc with the hand already open. Reaching late is what turns a catch into a bobble.",
        "Missed grabs" => "Set your hand where the disc will be, not where it is. Most misses are a late reach.",
        "Disc security" => "Move the disc earlier. Your giveaways are coming from holding it into pressure.",
        "Turnovers at home" => "Clear it long from your own third rather than trying to beat the first forechecker.",
        "Carrying into contact" => "Release before contact. Being stunned on the disc hands them the counter for free.",
        _ => null,
    };

    private static string Describe(CareerProfile p)
    {
        var best = p.Strengths.FirstOrDefault();
        var worst = p.Weaknesses.FirstOrDefault();

        string span = p.FirstSeen.Year > 2000
            ? $"{p.FirstSeen:d MMM} to {p.LastSeen:d MMM}"
            : "your library";

        string line = $"{p.Player} across {p.Matches} matches ({p.LiveMinutes:F0} minutes of live play, {span}): "
                    + $"{p.GoalPoints} points from {p.Goals} goals, {p.Assists} assists, {p.Saves} saves and {p.Stuns} stuns.";

        if (best != null) line += $" Your clearest edge is {best.Name.ToLowerInvariant()}.";
        if (worst != null) line += $" The biggest thing to fix is {worst.Name.ToLowerInvariant()}.";
        return line;
    }
}
