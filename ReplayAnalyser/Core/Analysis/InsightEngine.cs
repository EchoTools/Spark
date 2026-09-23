// Ported from the Replay Analyser (EchoAnalyser.Core/Analysis/InsightEngine.cs). Logic is unchanged; only the
// namespace, explicit usings and nullable context differ, so fixes can be diffed against
// the original.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using Spark.ReplayAnalyser.Model;

namespace Spark.ReplayAnalyser.Analysis;

public enum InsightKind { Strength, Weakness, Improvement, Counter, Note }

public enum InsightArea
{
    Finishing, Possession, Passing, Defence, Positioning, Physicality,
    Mechanics, Movement, Discipline, TeamShape,
}

public sealed class Insight
{
    public InsightKind Kind { get; init; }
    public InsightArea Area { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>What to actually do about it.</summary>
    public string? Action { get; init; }
    /// <summary>0..1 — how strongly the data supports this, used for ordering.</summary>
    public double Confidence { get; init; } = 0.5;
    /// <summary>The number the insight is built on, for display next to the text.</summary>
    public string? Evidence { get; init; }
}

public sealed class PlayerReport
{
    public PlayerAnalysis Player { get; init; } = null!;
    public MatchAnalysis Match { get; init; } = null!;
    public List<Insight> Insights { get; } = new();

    public IEnumerable<Insight> Strengths => Insights.Where(i => i.Kind == InsightKind.Strength);
    public IEnumerable<Insight> Weaknesses => Insights.Where(i => i.Kind == InsightKind.Weakness);
    public IEnumerable<Insight> Improvements => Insights.Where(i => i.Kind == InsightKind.Improvement);
    public IEnumerable<Insight> Counters => Insights.Where(i => i.Kind == InsightKind.Counter);

    /// <summary>A 0-100 rating per area, for the radar chart.</summary>
    public Dictionary<InsightArea, double> AreaScores { get; } = new();
    public double OverallRating { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>
/// Rule-based coaching. Every rule compares a player against the other players in the same
/// match rather than against absolute numbers, so the advice stays meaningful whether the
/// lobby is casual or a scrim between ranked teams.
/// </summary>
public static class InsightEngine
{
    public static PlayerReport BuildReport(MatchAnalysis match, PlayerAnalysis player, bool asOpponent)
    {
        var report = new PlayerReport { Player = player, Match = match };
        var peers = match.AllPlayers.Where(p => p.UserId != player.UserId && p.SecondsPlayed > 30).ToList();
        var teammates = match.AllPlayers
            .Where(p => p.Side == player.Side && p.UserId != player.UserId && p.SecondsPlayed > 30).ToList();

        ScoreAreas(report, player, peers);

        if (asOpponent) BuildCounterPlan(report, match, player, peers);
        else BuildSelfReview(report, match, player, peers, teammates);

        report.Insights.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        report.Summary = BuildSummary(match, player, report, asOpponent);
        return report;
    }

    // ------------------------------------------------------------------ ratings

    private static void ScoreAreas(PlayerReport r, PlayerAnalysis p, List<PlayerAnalysis> peers)
    {
        double PerMin(double v) => p.SecondsPlayed <= 0 ? 0 : v / (p.SecondsPlayed / 60.0);

        double finishing = Rank(p.Goals, peers.Select(x => (double)x.Goals)) * 0.6
                         + Rank(p.ShotAccuracy, peers.Select(x => x.ShotAccuracy)) * 0.4;
        double possession = Rank(p.PossessionTime, peers.Select(x => (double)x.PossessionTime)) * 0.6
                          + (1 - Rank(PerMin(p.Turnovers), peers.Select(x => x.SecondsPlayed <= 0 ? 0 : x.Turnovers / (x.SecondsPlayed / 60.0)))) * 0.4;
        double passing = Rank(p.PassCount, peers.Select(x => (double)x.PassCount)) * 0.7
                       + Rank(p.Assists, peers.Select(x => (double)x.Assists)) * 0.3;
        double defence = Rank(p.Saves, peers.Select(x => (double)x.Saves)) * 0.4
                       + Rank(p.Blocks, peers.Select(x => (double)x.Blocks)) * 0.2
                       + Rank(p.Interceptions, peers.Select(x => (double)x.Interceptions)) * 0.2
                       + Rank(p.Steals, peers.Select(x => (double)x.Steals)) * 0.2;
        double physical = Rank(p.Stuns, peers.Select(x => (double)x.Stuns)) * 0.7
                        + (1 - Rank(p.TimesStunned, peers.Select(x => (double)x.TimesStunned))) * 0.3;
        double movement = Rank(p.AverageSpeed, peers.Select(x => x.AverageSpeed)) * 0.5
                        + Rank(p.DistanceTravelled, peers.Select(x => x.DistanceTravelled)) * 0.5;
        // Throw mechanics only exist for the client that recorded the replay, so rank them
        // against the other players who also have telemetry — usually nobody, in which case
        // the area is left off the chart entirely rather than invented.
        var throwPeers = peers.Where(x => x.HasThrowTelemetry && x.Throws.Count >= 3).ToList();
        bool hasMechanics = p.HasThrowTelemetry && p.Throws.Count >= 3;
        double mechanics = hasMechanics
            ? Rank(p.AverageThrowSpeed, throwPeers.Select(x => x.AverageThrowSpeed)) * 0.6
              + (1 - Rank(p.AverageThrowPenalty, throwPeers.Select(x => x.AverageThrowPenalty))) * 0.4
            : 0;
        // Balance rewards a player who contributes at both ends rather than camping.
        double positioning = 1 - Math.Abs(p.AverageFieldProgress - 0.5) * 2;
        double discipline = 1 - Rank(PerMin(p.Turnovers), peers.Select(x => x.SecondsPlayed <= 0 ? 0 : x.Turnovers / (x.SecondsPlayed / 60.0)));

        r.AreaScores[InsightArea.Finishing] = finishing * 100;
        r.AreaScores[InsightArea.Possession] = possession * 100;
        r.AreaScores[InsightArea.Passing] = passing * 100;
        r.AreaScores[InsightArea.Defence] = defence * 100;
        r.AreaScores[InsightArea.Physicality] = physical * 100;
        r.AreaScores[InsightArea.Movement] = movement * 100;
        if (hasMechanics) r.AreaScores[InsightArea.Mechanics] = mechanics * 100;
        r.AreaScores[InsightArea.Positioning] = Math.Clamp(positioning, 0, 1) * 100;
        r.AreaScores[InsightArea.Discipline] = discipline * 100;

        r.OverallRating = r.AreaScores.Values.Average();
    }

    /// <summary>
    /// The middle of this lobby for each stat a scouting read is built on.
    ///
    /// Absolute thresholds do not scout: in a lobby where everyone holds the disc four
    /// seconds, "slow to release" is true of all of them and therefore tells you nothing.
    /// A read has to say how this player differs from the others in the same game.
    /// </summary>
    private sealed class LobbyNorms
    {
        public double HoldSeconds { get; private init; }
        public double StunsPerMin { get; private init; }
        public double StunnedPerMin { get; private init; }
        public double ShotDistance { get; private init; }

        public static LobbyNorms From(MatchAnalysis m)
        {
            var pool = m.AllPlayers.Where(x => x.SecondsPlayed > 30).ToList();
            return new LobbyNorms
            {
                HoldSeconds = Median(pool.Where(x => x.PossessionCount >= 3)
                                         .Select(x => x.AveragePossessionSeconds)),
                StunsPerMin = Median(pool.Select(x => x.StunsPerMinute)),
                StunnedPerMin = Median(pool.Select(x => PerMinute(x.TimesStunned, x.SecondsPlayed))),
                ShotDistance = Median(pool.Where(x => x.ShotPositions.Count >= 2)
                                          .Select(x => AverageShotDistance(x))),
            };
        }
    }

    private static double PerMinute(double value, double seconds) =>
        seconds <= 0 ? 0 : value / (seconds / 60.0);

    private static double AverageShotDistance(PlayerAnalysis p) =>
        p.ShotPositions.Count == 0
            ? 0
            : p.ShotPositions.Average(v => Vector3.Distance(v, ArenaGeometry.AttackingGoal(p.Side)));

    private static double Median(IEnumerable<double> values)
    {
        var list = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        return list.Count % 2 == 1
            ? list[list.Count / 2]
            : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }

    /// <summary>Percentile of <paramref name="value"/> within the peer set, 0..1.</summary>
    private static double Rank(double value, IEnumerable<double> peers)
    {
        var list = peers.ToList();
        if (list.Count == 0) return 0.5;
        int below = list.Count(v => v < value);
        int equal = list.Count(v => Math.Abs(v - value) < 1e-9);
        return (below + equal * 0.5) / list.Count;
    }

    // ------------------------------------------------------------------ self review

    private static void BuildSelfReview(PlayerReport r, MatchAnalysis m, PlayerAnalysis p,
                                        List<PlayerAnalysis> peers, List<PlayerAnalysis> mates)
    {
        double minutes = Math.Max(p.SecondsPlayed / 60.0, 0.01);
        var opponents = m.AllPlayers.Where(x => x.Side != p.Side && x.SecondsPlayed > 30).ToList();

        // ---- finishing ----
        if (p.ShotsTaken >= 4)
        {
            double acc = p.ShotAccuracy;
            double peerAcc = peers.Count > 0 ? peers.Where(x => x.ShotsTaken >= 3).Select(x => x.ShotAccuracy).DefaultIfEmpty(0).Average() : 0;
            if (acc < 0.25 && acc < peerAcc * 0.7)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Weakness, Area = InsightArea.Finishing,
                    Title = "Shots are not converting",
                    Detail = $"You scored {p.Goals} from {p.ShotsTaken} shots ({acc:P0}). Everyone else in this match averaged {peerAcc:P0}.",
                    Action = "Take fewer contested shots from range. Carry to inside the three-line or pass to a team-mate with a clean lane before releasing.",
                    Evidence = $"{acc:P0} vs {peerAcc:P0} lobby average",
                    Confidence = 0.85,
                });
            else if (acc > 0.5 && p.Goals >= 2)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Strength, Area = InsightArea.Finishing,
                    Title = "Clinical in front of goal",
                    Detail = $"{p.Goals} goals from {p.ShotsTaken} shots ({acc:P0}) — well above the {peerAcc:P0} lobby average.",
                    Evidence = $"{acc:P0} conversion",
                    Confidence = 0.8,
                });
        }
        else if (p.PossessionTime > 20 && p.ShotsTaken <= 1)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Improvement, Area = InsightArea.Finishing,
                Title = "Holding the disc without threatening",
                Detail = $"You held the disc for {p.PossessionTime:F0}s but took only {p.ShotsTaken} shot(s).",
                Action = "When you win the disc in the neutral third, look at the goal first. A shot on target forces a save and a rebound; a slow carry lets the defence reset.",
                Evidence = $"{p.PossessionTime:F0}s held, {p.ShotsTaken} shots",
                Confidence = 0.7,
            });

        // ---- turnovers ----
        double toPerMin = p.Turnovers / minutes;
        double peerTo = peers.Count > 0 ? peers.Average(x => x.SecondsPlayed <= 0 ? 0 : x.Turnovers / (x.SecondsPlayed / 60.0)) : 0;
        if (p.Turnovers >= 3 && toPerMin > peerTo * 1.3)
        {
            var defensiveLosses = p.Events.Count(e => e.Kind == MatchEventKind.Turnover &&
                ArenaGeometry.ZoneFor(e.Position, p.Side) == ArenaZone.DefensiveThird);
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.Discipline,
                Title = "Giving the disc away too often",
                Detail = $"{p.Turnovers} turnovers ({toPerMin:F1}/min vs {peerTo:F1}/min for everyone else)"
                       + (defensiveLosses > 0 ? $", and {defensiveLosses} of them were in your own defensive third." : "."),
                Action = defensiveLosses > 0
                    ? "Stop trying to beat the first forechecker near your own goal. Clear it long or hand it to the goalie side team-mate."
                    : "Release earlier. Most losses come from holding one beat too long while a defender closes.",
                Evidence = $"{p.Turnovers} turnovers",
                Confidence = 0.85,
            });
        }

        // ---- possession style ----
        if (p.PossessionCount >= 5)
        {
            double avgHold = p.AveragePossessionSeconds;
            double peerHold = peers.Where(x => x.PossessionCount >= 3).Select(x => x.AveragePossessionSeconds).DefaultIfEmpty(0).Average();
            if (avgHold > peerHold * 1.6 && avgHold > 3)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Improvement, Area = InsightArea.Possession,
                    Title = "Holding the disc too long",
                    Detail = $"Your average possession lasts {avgHold:F1}s against {peerHold:F1}s for the rest of the lobby.",
                    Action = "Move it inside two seconds. Long carries let both defenders converge and turn a 3v2 into a 1v2.",
                    Evidence = $"{avgHold:F1}s average hold",
                    Confidence = 0.75,
                });
            else if (avgHold < peerHold * 0.6 && p.PassCount > 4)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Strength, Area = InsightArea.Possession,
                    Title = "Quick release",
                    Detail = $"You move the disc on in {avgHold:F1}s on average — faster than the {peerHold:F1}s lobby average, which keeps the defence rotating.",
                    Evidence = $"{avgHold:F1}s average hold",
                    Confidence = 0.65,
                });
        }

        // ---- passing ----
        if (mates.Count > 0)
        {
            // If nobody in the lobby moved the disc at all there is nothing to compare, and
            // "0 passes against a lobby average of 0" is not a coaching point.
            int passes = p.PassCount;
            double peerPasses = peers.Select(x => (double)x.PassCount).DefaultIfEmpty(0).Average();

            if (peerPasses > 0 && passes <= peerPasses * 0.5 && p.PossessionTime > 15)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Improvement, Area = InsightArea.Passing,
                    Title = "Playing too much alone",
                    Detail = $"{passes} passes against a lobby average of {peerPasses:F0}, despite {p.PossessionTime:F0}s of disc time.",
                    Action = "Use your team-mates as a release valve when the first look is covered — a pass out and back beats a contested carry.",
                    Evidence = $"{passes} passes",
                    Confidence = 0.7,
                });
            else if (passes > peerPasses * 1.5 && passes >= 6)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Strength, Area = InsightArea.Passing,
                    Title = "Connects the team",
                    Detail = $"{passes} passes — well clear of the {peerPasses:F0} lobby average"
                           + (p.PassTargets.Count > 0 ? $", most often to {p.PassTargets.OrderByDescending(kv => kv.Value).First().Key}." : "."),
                    Evidence = $"{passes} passes",
                    Confidence = 0.7,
                });
        }

        // ---- defence and shape ----
        if (p.OffensiveThirdShare > 0.55 && p.DefensiveThirdShare < 0.15)
        {
            int concededWhileUp = m.Goals.Count(g => g.Side != p.Side);
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.Positioning,
                Title = "Living in the offensive third",
                Detail = $"You spent {p.OffensiveThirdShare:P0} of live play in the attacking third and only {p.DefensiveThirdShare:P0} defending. Your team conceded {concededWhileUp} goal(s).",
                Action = "Set a trigger to recover: the moment a team-mate loses the disc past midfield, turn and skate back rather than chasing the strip.",
                Evidence = $"{p.OffensiveThirdShare:P0} time up front",
                Confidence = 0.8,
            });
        }
        else if (p.DefensiveThirdShare > 0.6)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Improvement, Area = InsightArea.Positioning,
                Title = "Anchored on your own goal",
                Detail = $"{p.DefensiveThirdShare:P0} of your live time was in the defensive third and {p.SecondsInGoalieBox:F0}s inside the goalie box.",
                Action = p.Saves > 3
                    ? "The goaltending is working, but step out to the neutral third after a save so your team has an outlet instead of a 2v3."
                    : "Sitting deep without saves means you are removing yourself from play. Push to midfield and contest the disc.",
                Evidence = $"{p.DefensiveThirdShare:P0} time back",
                Confidence = 0.7,
            });

        if (p.Saves >= 3)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Strength, Area = InsightArea.Defence,
                Title = "Reliable last line",
                Detail = $"{p.Saves} saves and {p.Blocks} block(s) — you were repeatedly the reason the goal stayed clean.",
                Evidence = $"{p.Saves} saves",
                Confidence = 0.75,
            });

        // ---- physicality ----
        if (p.TimesStunned >= 6 && p.TimesStunned > p.Stuns * 1.6)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.Physicality,
                Title = "Losing the contact battle",
                Detail = $"Stunned {p.TimesStunned} times while landing {p.Stuns}, costing roughly {p.SecondsStunned:F0}s frozen.",
                Action = "Approach off-angle instead of straight at a defender, and keep your arms in to block rather than swinging first and missing.",
                Evidence = $"{p.TimesStunned} stunned / {p.Stuns} landed",
                Confidence = 0.8,
            });
        else if (p.Stuns >= 5 && p.Stuns > p.TimesStunned * 1.4)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Strength, Area = InsightArea.Physicality,
                Title = "Wins the contact battle",
                Detail = $"{p.Stuns} stuns landed against {p.TimesStunned} taken — you controlled the physical exchanges.",
                Evidence = $"{p.Stuns} stuns",
                Confidence = 0.7,
            });

        // ---- throw mechanics ----
        if (p.HasThrowTelemetry && p.Throws.Count >= 5)
        {
            double avgPenalty = p.AverageThrowPenalty;
            double wrist = p.Throws.Average(t => t.WristThrowPenalty);
            double offAxis = p.Throws.Average(t => t.OffAxisSpinDeg);
            double speed = p.AverageThrowSpeed;
            double peerSpeed = peers.Where(x => x.HasThrowTelemetry && x.Throws.Count >= 3).Select(x => x.AverageThrowSpeed).DefaultIfEmpty(0).Average();

            if (wrist > 0.15)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Improvement, Area = InsightArea.Mechanics,
                    Title = "Wrist snap is costing you throw speed",
                    Detail = $"Your throws carry an average wrist penalty of {wrist:P0} over {p.Throws.Count} recorded throws.",
                    Action = "Release with the wrist aligned to the throw direction — flicking across the line of the throw is what the game penalises.",
                    Evidence = $"{wrist:P0} wrist penalty",
                    Confidence = 0.75,
                });
            if (offAxis > 25)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Improvement, Area = InsightArea.Mechanics,
                    Title = "Disc is leaving the hand off-axis",
                    Detail = $"Average off-axis spin of {offAxis:F0}° means the disc wobbles and bleeds speed in flight.",
                    Action = "Square your shoulders to the target before release rather than throwing across your body.",
                    Evidence = $"{offAxis:F0}° off-axis",
                    Confidence = 0.7,
                });
            if (peerSpeed > 0 && speed > peerSpeed * 1.15)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Strength, Area = InsightArea.Mechanics,
                    Title = "Heavy throw",
                    Detail = $"Average release of {speed:F1} m/s (peak {p.MaxThrowSpeed:F1}) against a {peerSpeed:F1} m/s lobby average.",
                    Evidence = $"{speed:F1} m/s average",
                    Confidence = 0.7,
                });
        }

        if (r.Insights.Count == 0)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Note, Area = InsightArea.Possession,
                Title = "Not enough live play to judge",
                Detail = $"Only {p.SecondsPlayed:F0}s of live time was recorded for this player, which is too little to draw conclusions from.",
                Confidence = 0.3,
            });
    }

    // ------------------------------------------------------------------ scouting

    private static void BuildCounterPlan(PlayerReport r, MatchAnalysis m, PlayerAnalysis p, List<PlayerAnalysis> peers)
    {
        double minutes = Math.Max(p.SecondsPlayed / 60.0, 0.01);
        var norms = LobbyNorms.From(m);

        // Where do they shoot from? Side bias is the single most actionable read.
        if (p.ShotPositions.Count >= 3)
        {
            double avgX = p.ShotPositions.Average(v => v.X);
            int left = p.ShotPositions.Count(v => v.X < -2);
            int right = p.ShotPositions.Count(v => v.X > 2);
            int total = p.ShotPositions.Count;
            if (left >= total * 0.6 || right >= total * 0.6)
            {
                string sideName = left > right ? "left" : "right";
                int count = Math.Max(left, right);
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Finishing,
                    Title = $"Shoots from the {sideName}",
                    Detail = $"{count} of {total} shots came from the {sideName} side of the arena (mean X {avgX:F1}m).",
                    Action = $"Shade your goalie to the {sideName} post and force them to cut back across the middle before releasing.",
                    Evidence = $"{count}/{total} shots {sideName}",
                    Confidence = 0.8,
                });
            }

            double avgDist = AverageShotDistance(p);
            if (avgDist > 35 && (norms.ShotDistance <= 0 || avgDist >= norms.ShotDistance * 1.2))
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Finishing,
                    Title = "Settles for long shots",
                    Detail = $"Their average shot is taken {avgDist:F0}m from goal, against "
                           + $"{norms.ShotDistance:F0}m for the rest of this lobby.",
                    Action = "Do not over-commit to pressuring them at range — sag into the lane and let the goalie read the long throw.",
                    Evidence = $"{avgDist:F0}m average shot distance",
                    Confidence = 0.75,
                });
            else if (avgDist < 18 && (norms.ShotDistance <= 0 || avgDist <= norms.ShotDistance * 0.8))
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Finishing,
                    Title = "Drives all the way in",
                    Detail = $"Their average shot is from just {avgDist:F0}m against {norms.ShotDistance:F0}m "
                           + "for this lobby — they want to get inside before releasing.",
                    Action = "Meet them early at the three-line with a body. If they get past the last block they will score.",
                    Evidence = $"{avgDist:F0}m average shot distance",
                    Confidence = 0.8,
                });
        }

        // Hold time tells you whether to pressure immediately or contain.
        if (p.PossessionCount >= 4)
        {
            double hold = p.AveragePossessionSeconds;
            if (hold > 3.5 && (norms.HoldSeconds <= 0 || hold >= norms.HoldSeconds * 1.25))
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Possession,
                    Title = "Slow to release",
                    Detail = $"They hold the disc {hold:F1}s per possession across {p.PossessionCount} possessions, "
                           + $"against {norms.HoldSeconds:F1}s for the rest of this lobby.",
                    Action = "Double them the moment they catch. They will not move it quickly enough to punish the second defender leaving.",
                    Evidence = $"{hold:F1}s per possession",
                    Confidence = 0.8,
                });
            else if (hold < 1.5 && (norms.HoldSeconds <= 0 || hold <= norms.HoldSeconds * 0.75))
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Passing,
                    Title = "One-touch player",
                    Detail = $"They release in {hold:F1}s on average — the danger is the pass, not the carry.",
                    Action = "Do not chase the catcher. Cover the passing lane to their most-used target instead.",
                    Evidence = $"{hold:F1}s per possession",
                    Confidence = 0.75,
                });
        }

        if (p.PassTargets.Count > 0)
        {
            var fav = p.PassTargets.OrderByDescending(kv => kv.Value).First();
            int totalPasses = p.PassTargets.Values.Sum();
            if (totalPasses >= 4 && fav.Value >= totalPasses * 0.55)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Passing,
                    Title = $"Predictable outlet to {fav.Key}",
                    Detail = $"{fav.Value} of their {totalPasses} handovers went to {fav.Key}.",
                    Action = $"Sit in the lane between them and {fav.Key}. Taking that option away forces a decision they clearly do not want to make.",
                    Evidence = $"{fav.Value}/{totalPasses} to {fav.Key}",
                    Confidence = 0.8,
                });
        }

        // Positional read: are they exploitable on the counter?
        if (p.OffensiveThirdShare > 0.5 && p.DefensiveThirdShare < 0.2)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Counter, Area = InsightArea.Positioning,
                Title = "Never recovers",
                Detail = $"They spend {p.OffensiveThirdShare:P0} of live play in their attacking third and only {p.DefensiveThirdShare:P0} back.",
                Action = "Break out fast the instant they commit. The space behind them is the whole game — one long outlet puts you 2v1.",
                Evidence = $"{p.OffensiveThirdShare:P0} up front",
                Confidence = 0.85,
            });
        else if (p.SecondsInGoalieBox > p.SecondsPlayed * 0.45)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Counter, Area = InsightArea.Defence,
                Title = "Dedicated goalie",
                Detail = $"They sat in the goalie box for {p.SecondsInGoalieBox:F0}s ({p.SecondsInGoalieBox / Math.Max(p.SecondsPlayed, 1):P0} of their live time) and made {p.Saves} saves.",
                Action = "Do not shoot into them from range. Draw them off the line with a drive, then feed the trailer for a tap in.",
                Evidence = $"{p.Saves} saves, {p.SecondsInGoalieBox:F0}s in box",
                Confidence = 0.8,
            });

        // Physical threat.
        double stunRate = p.Stuns / minutes;
        if (stunRate > 1.2 && p.Stuns >= 4 && (norms.StunsPerMin <= 0 || stunRate >= norms.StunsPerMin * 1.35))
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Counter, Area = InsightArea.Physicality,
                Title = "Aggressive on contact",
                Detail = $"{p.Stuns} stuns in {minutes:F1} minutes ({stunRate:F1}/min against "
                       + $"{norms.StunsPerMin:F1}/min for this lobby) — they hunt bodies, not just the disc.",
                Action = "Keep your hands up when entering their range and never approach in a straight line. Bait the swing, then go past them.",
                Evidence = $"{p.Stuns / minutes:F1} stuns/min",
                Confidence = 0.8,
            });

        double stunnedRate = PerMinute(p.TimesStunned, p.SecondsPlayed);
        if (p.TimesStunned >= 5 && (norms.StunnedPerMin <= 0 || stunnedRate >= norms.StunnedPerMin * 1.35))
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Counter, Area = InsightArea.Physicality,
                Title = "Vulnerable to contact",
                Detail = $"They were stunned {p.TimesStunned} times ({stunnedRate:F1}/min against "
                       + $"{norms.StunnedPerMin:F1}/min for this lobby), losing about {p.SecondsStunned:F0}s.",
                Action = "Lead with a stun when they carry. Taking them out of the play once is usually enough to spring a counter.",
                Evidence = $"{p.TimesStunned} times stunned",
                Confidence = 0.7,
            });

        // Mechanical read on their throw.
        if (p.HasThrowTelemetry && p.Throws.Count >= 5)
        {
            double speed = p.AverageThrowSpeed;
            double peerSpeed = peers.Where(x => x.HasThrowTelemetry && x.Throws.Count >= 3).Select(x => x.AverageThrowSpeed).DefaultIfEmpty(0).Average();
            if (peerSpeed > 0 && speed < peerSpeed * 0.85)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Mechanics,
                    Title = "Weak release",
                    Detail = $"Average throw speed {speed:F1} m/s against {peerSpeed:F1} m/s for the rest of the lobby.",
                    Action = "Their long shots hang. Play the goalie a step off the line and read the flight rather than committing early.",
                    Evidence = $"{speed:F1} m/s vs {peerSpeed:F1} m/s",
                    Confidence = 0.75,
                });
            else if (peerSpeed > 0 && speed > peerSpeed * 1.15)
                r.Insights.Add(new Insight
                {
                    Kind = InsightKind.Counter, Area = InsightArea.Mechanics,
                    Title = "Heavy shot",
                    Detail = $"Average {speed:F1} m/s, peaking at {p.MaxThrowSpeed:F1} m/s.",
                    Action = "Deny the wind-up instead of trying to react to it. Body them before they set their feet.",
                    Evidence = $"peak {p.MaxThrowSpeed:F1} m/s",
                    Confidence = 0.75,
                });
        }

        // Only one player on a side can be the one to key on. In a lobby where everybody
        // chipped in, saying "take this player out first" about each of them in turn is
        // worse than saying nothing, so this needs a real margin over their own team-mates.
        var sameTeam = m.AllPlayers
            .Where(x => x.Side == p.Side && x.UserId != p.UserId && x.SecondsPlayed > 30)
            .ToList();
        double bestMateRate = sameTeam.Count == 0 ? 0 : sameTeam.Max(x => x.PointsPerMinute);
        bool leadsTeam = sameTeam.Count == 0 || p.PointsPerMinute > bestMateRate;
        bool clearMargin = bestMateRate <= 0 || p.PointsPerMinute >= bestMateRate * 1.4;

        if (p.GoalPoints >= 4 && leadsTeam && clearMargin)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Note, Area = InsightArea.Finishing,
                Title = "Primary scoring threat",
                Detail = $"{p.GoalPoints} points ({p.Goals}G {p.Assists}A) at {p.PointsPerMinute:F1}/min — "
                       + (sameTeam.Count == 0
                            ? $"over {minutes:F1} minutes of live play."
                            : $"clear of the next-best on their team at {bestMateRate:F1}/min."),
                Action = "This is the player to take out of the game first. Assign your best defender and accept giving space elsewhere.",
                Evidence = $"{p.PointsPerMinute:F1} pts/min",
                Confidence = 0.9,
            });
        else if (p.GoalPoints >= 3 && sameTeam.Count > 0 && leadsTeam)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Note, Area = InsightArea.Finishing,
                Title = "Scores, but is not the one to key on",
                Detail = $"{p.GoalPoints} points at {p.PointsPerMinute:F1}/min against {bestMateRate:F1}/min for their "
                       + "best team-mate — the threat is spread across the team.",
                Action = "Do not build the defence around this one player. Hold your shape and cover the lanes rather than following them.",
                Evidence = $"{p.PointsPerMinute:F1} pts/min",
                Confidence = 0.6,
            });

        if (r.Insights.Count == 0)
            r.Insights.Add(new Insight
            {
                Kind = InsightKind.Note, Area = InsightArea.Possession,
                Title = "Too little footage to scout",
                Detail = $"Only {p.SecondsPlayed:F0}s of live play recorded — not enough to establish a tendency.",
                Confidence = 0.3,
            });
    }

    private static string BuildSummary(MatchAnalysis m, PlayerAnalysis p, PlayerReport r, bool asOpponent)
    {
        string best = r.AreaScores.Count == 0 ? "—" : r.AreaScores.OrderByDescending(kv => kv.Value).First().Key.ToString();
        string worst = r.AreaScores.Count == 0 ? "—" : r.AreaScores.OrderBy(kv => kv.Value).First().Key.ToString();
        double mins = p.SecondsPlayed / 60.0;

        if (asOpponent)
            return $"{p.Name} played {mins:F1} min for {(p.Side == TeamSide.Blue ? "BLUE" : "ORANGE")}, "
                 + $"scoring {p.GoalPoints} points on {p.ShotsTaken} shots with {p.Saves} saves and {p.Stuns} stuns. "
                 + $"Strongest in {best.ToLowerInvariant()}, weakest in {worst.ToLowerInvariant()} — attack that.";

        return $"{p.Name}: {p.GoalPoints} points ({p.Goals}G {p.Assists}A), {p.Saves} saves, {p.Stuns} stuns, "
             + $"{p.PossessionTime:F0}s on disc and {p.Turnovers} turnovers across {mins:F1} min of live play. "
             + $"Best area is {best.ToLowerInvariant()}; the clearest gain is in {worst.ToLowerInvariant()}.";
    }

    // ------------------------------------------------------------------ team level

    public static List<Insight> BuildTeamInsights(MatchAnalysis m, TeamSide side)
    {
        var team = side == TeamSide.Blue ? m.Blue : m.Orange;
        var other = side == TeamSide.Blue ? m.Orange : m.Blue;
        var list = new List<Insight>();

        if (team.PossessionShare > 0.6)
            list.Add(new Insight
            {
                Kind = InsightKind.Strength, Area = InsightArea.Possession,
                Title = "Controlled the disc",
                Detail = $"{team.Name} held possession {team.PossessionShare:P0} of the time.",
                Evidence = $"{team.PossessionShare:P0} possession", Confidence = 0.8,
            });
        else if (team.PossessionShare < 0.4)
            list.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.Possession,
                Title = "Chased the disc all match",
                Detail = $"Only {team.PossessionShare:P0} possession — the disc lived with {other.Name}.",
                Action = "Win the neutral more often: send one player to contest the reset and a second to cover the outlet rather than both chasing the carrier.",
                Evidence = $"{team.PossessionShare:P0} possession", Confidence = 0.8,
            });

        double noneBack = team.DefendersBackShare[0];
        if (noneBack > 0.35)
            list.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.TeamShape,
                Title = "Goal left unguarded",
                Detail = $"For {noneBack:P0} of live play {team.Name} had nobody in their defensive third.",
                Action = "Rotate so at least one player is always below the disc. An all-in press is only worth it when you actually have possession.",
                Evidence = $"{noneBack:P0} with nobody back", Confidence = 0.85,
            });

        double allBack = team.DefendersBackShare[3];
        if (allBack > 0.3)
            list.Add(new Insight
            {
                Kind = InsightKind.Improvement, Area = InsightArea.TeamShape,
                Title = "Over-committed to defence",
                Detail = $"All three players were in the defensive third for {allBack:P0} of live play.",
                Action = "Once the disc is cleared, push at least one player to midfield so you have an outlet instead of conceding possession again.",
                Evidence = $"{allBack:P0} with everyone back", Confidence = 0.7,
            });

        if (team.ShotsTaken >= 5)
        {
            if (team.ShotAccuracy < 0.2)
                list.Add(new Insight
                {
                    Kind = InsightKind.Weakness, Area = InsightArea.Finishing,
                    Title = "Wasteful in front of goal",
                    Detail = $"{team.Goals} goals from {team.ShotsTaken} shots ({team.ShotAccuracy:P0}).",
                    Action = "Work one more pass before shooting. Shots from outside the three-line against a set goalie are close to free possession for the other team.",
                    Evidence = $"{team.ShotAccuracy:P0} conversion", Confidence = 0.8,
                });
            else if (team.ShotAccuracy > 0.45)
                list.Add(new Insight
                {
                    Kind = InsightKind.Strength, Area = InsightArea.Finishing,
                    Title = "Efficient attack",
                    Detail = $"{team.Goals} goals from just {team.ShotsTaken} shots ({team.ShotAccuracy:P0}).",
                    Evidence = $"{team.ShotAccuracy:P0} conversion", Confidence = 0.75,
                });
        }

        if (team.AverageSpread > 45)
            list.Add(new Insight
            {
                Kind = InsightKind.Improvement, Area = InsightArea.TeamShape,
                Title = "Team is stretched thin",
                Detail = $"Average distance between team-mates was {team.AverageSpread:F0}m.",
                Action = "Play closer together through the neutral third so a turnover has support instead of leaving isolated 1v2s.",
                Evidence = $"{team.AverageSpread:F0}m spread", Confidence = 0.6,
            });
        else if (team.AverageSpread < 18 && team.AverageSpread > 0)
            list.Add(new Insight
            {
                Kind = InsightKind.Improvement, Area = InsightArea.TeamShape,
                Title = "Team is bunched up",
                Detail = $"Average distance between team-mates was only {team.AverageSpread:F0}m.",
                Action = "Spread the width. Three players in the same lane means one defender covers all of you.",
                Evidence = $"{team.AverageSpread:F0}m spread", Confidence = 0.6,
            });

        if (team.Turnovers > other.Turnovers * 1.5 && team.Turnovers >= 6)
            list.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.Discipline,
                Title = "Lost the turnover battle",
                Detail = $"{team.Turnovers} turnovers against {other.Turnovers} for {other.Name}.",
                Action = "Simplify under pressure — the cheap losses are what fed their counter-attacks.",
                Evidence = $"{team.Turnovers} vs {other.Turnovers}", Confidence = 0.8,
            });

        list.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return list;
    }
}
