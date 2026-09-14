// Ported from the Replay Analyser (EchoAnalyser.Core/Analysis/ScoutingAnalyser.cs). Logic is unchanged; only the
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

/// <summary>What one player's record looks like when the two of you are on opposite teams.</summary>
public sealed class HeadToHead
{
    public string Viewer { get; init; } = "";
    public string Opponent { get; init; } = "";
    /// <summary>Matches you have both appeared in.</summary>
    public int Meetings { get; init; }
    public int AsOpponents { get; init; }
    public int AsTeammates { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Draws { get; init; }
    /// <summary>Their scoring rate in the matches they played against you.</summary>
    public double TheirPointsPerMinute { get; init; }
    /// <summary>Their scoring rate everywhere else, for comparison.</summary>
    public double TheirBaselinePointsPerMinute { get; init; }
    public string Summary { get; init; } = "";

    public bool HasFaced => AsOpponents > 0;
    public string Record => $"{Wins}W {Losses}L" + (Draws > 0 ? $" {Draws}D" : "");
}

/// <summary>A counter-plan on one player built from every indexed match they appear in.</summary>
public sealed class CareerScout
{
    public string Player { get; init; } = "";
    public int Matches { get; init; }
    public double LiveMinutes { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>A short label for how they play: goalie, presser, rotator.</summary>
    public string Style { get; set; } = "";
    /// <summary>The one-line brief that leads the report.</summary>
    public string Summary { get; set; } = "";

    public List<Insight> Insights { get; } = new();
    public List<CareerMetric> Metrics { get; } = new();
    public HeadToHead? Versus { get; set; }

    public int GoalPoints { get; init; }
    public int Goals { get; init; }
    public int Assists { get; init; }
    public int Saves { get; init; }
    public int Stuns { get; init; }

    public CareerMetric? Metric(string name) =>
        Metrics.FirstOrDefault(m => m.Name == name);
}

/// <summary>
/// Builds a scouting report on a player from their whole indexed history rather than one game.
///
/// A single match tells you what somebody did once; a season tells you what they will do
/// again. Every read here is still relative — measured against the players they actually
/// share lobbies with — so scouting somebody from a scrim pool and somebody from public
/// matches both produce advice you can act on.
/// </summary>
public static class ScoutingAnalyser
{
    /// <summary>Ignore appearances too short to say anything about.</summary>
    private const double MinSecondsPerMatch = 60;

    /// <summary>Below this many matches the report says so rather than pretending to be certain.</summary>
    private const int ThinEvidence = 4;

    public static CareerScout? Build(LibraryIndex index, string target, string? viewer = null)
    {
        var rows = Rows(index, target);
        if (rows.Count == 0) return null;

        double liveSeconds = rows.Sum(r => r.me.SecondsPlayed);
        var scout = new CareerScout
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
        };

        BuildMetrics(scout, rows);
        scout.Style = DescribeStyle(scout);
        if (!string.IsNullOrWhiteSpace(viewer) &&
            !string.Equals(viewer, target, StringComparison.OrdinalIgnoreCase))
            scout.Versus = BuildHeadToHead(index, target, viewer!);

        BuildCounters(scout, rows);
        scout.Summary = Describe(scout);
        return scout;
    }

    private static List<(MatchSummary match, PlayerSummary me, List<PlayerSummary> peers)> Rows(
        LibraryIndex index, string player)
    {
        var rows = new List<(MatchSummary, PlayerSummary, List<PlayerSummary>)>();
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
        return rows;
    }

    // ------------------------------------------------------------------ metrics

    private static void BuildMetrics(
        CareerScout scout,
        List<(MatchSummary match, PlayerSummary me, List<PlayerSummary> peers)> rows)
    {
        int third = Math.Max(1, rows.Count / 3);
        var early = rows.Take(third).ToList();
        var recent = rows.Skip(Math.Max(0, rows.Count - third)).ToList();

        void Add(string name, string description, Func<PlayerSummary, double> pick,
                 bool lowerIsBetter = false, string format = "0.00")
        {
            scout.Metrics.Add(new CareerMetric
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

        Add("Scoring", "Points on the board per minute", p => p.PointsPerMinute);
        Add("Finishing", "Share of their shots that go in",
            p => p.ShotsTaken == 0 ? 0 : (double)p.Goals / p.ShotsTaken, format: "0%");
        Add("Playmaking", "Assists per minute",
            p => PerMinute(p.Assists, p.SecondsPlayed));
        Add("Distribution", "Handovers to team-mates per minute",
            p => PerMinute(Math.Max(p.Passes, p.Handovers), p.SecondsPlayed));
        Add("Saves", "Saves per minute", p => PerMinute(p.Saves, p.SecondsPlayed));
        Add("Physicality", "Stuns landed per minute", p => p.StunsPerMinute);
        Add("Work rate", "Metres skated per minute",
            p => PerMinute(p.DistanceTravelled, p.SecondsPlayed), format: "0");
        Add("Hands", "Share of reachable loose discs they take", p => p.GrabSuccessRate, format: "0%");
        Add("Disc security", "Turnovers per minute", p => p.TurnoversPerMinute, lowerIsBetter: true);
        Add("Turnovers at home", "Giveaways inside their own third, per minute",
            p => PerMinute(p.TurnoversAtHome, p.SecondsPlayed), lowerIsBetter: true);
        Add("Carrying into contact", "Times stunned while on the disc, per minute",
            p => PerMinute(p.CarriedIntoStun, p.SecondsPlayed), lowerIsBetter: true);
        Add("Hold time", "Seconds they keep the disc each time they get it",
            p => p.AveragePossessionSeconds, format: "0.0");
        Add("Press height", "How far up the arena they sit, 0 is their own goal",
            p => p.AverageFieldProgress, format: "0%");
        Add("Goal cover", "Share of their time inside their own goalie box",
            p => p.SecondsPlayed <= 0 ? 0 : p.SecondsInGoalieBox / p.SecondsPlayed, format: "0%");

        // Throw mechanics only exist for whoever recorded the replay, so both sides of this
        // comparison have to be restricted to players who actually have telemetry. Without
        // that the peer figure collapses to zero and every recorder looks like a cannon.
        var mine = rows.Where(r => r.me.HasThrowTelemetry && r.me.AverageThrowSpeed > 0).ToList();
        var theirs = rows.SelectMany(r => r.peers.Where(p => p.HasThrowTelemetry && p.AverageThrowSpeed > 0))
                         .ToList();
        if (mine.Count > 0 && theirs.Count > 0)
            scout.Metrics.Add(new CareerMetric
            {
                Name = "Throw speed",
                Description = "Average release speed, among players whose throws were recorded",
                Format = "0.0",
                Value = WeightedMean(mine.Select(r => ((double)r.me.AverageThrowSpeed, r.me.SecondsPlayed))),
                PeerValue = WeightedMean(theirs.Select(p => ((double)p.AverageThrowSpeed, p.SecondsPlayed))),
                EarlyValue = WeightedMean(mine.Take(Math.Max(1, mine.Count / 3))
                    .Select(r => ((double)r.me.AverageThrowSpeed, r.me.SecondsPlayed))),
                RecentValue = WeightedMean(mine.Skip(Math.Max(0, mine.Count - Math.Max(1, mine.Count / 3)))
                    .Select(r => ((double)r.me.AverageThrowSpeed, r.me.SecondsPlayed))),
            });
    }

    private static double PerMinute(double value, double seconds) =>
        seconds <= 0 ? 0 : value / (seconds / 60.0);

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

    // ------------------------------------------------------------------ head to head

    private static HeadToHead BuildHeadToHead(LibraryIndex index, string target, string viewer)
    {
        int meetings = 0, asOpponents = 0, asTeammates = 0, wins = 0, losses = 0, draws = 0;
        double theirPoints = 0, theirSeconds = 0;
        double elsePoints = 0, elseSeconds = 0;

        foreach (var m in index.Entries.Values.Where(e => e.Ok))
        {
            var them = m.Players.FirstOrDefault(p =>
                string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase) &&
                p.SecondsPlayed >= MinSecondsPerMatch);
            if (them == null) continue;

            var you = m.Players.FirstOrDefault(p =>
                string.Equals(p.Name, viewer, StringComparison.OrdinalIgnoreCase) &&
                p.SecondsPlayed >= MinSecondsPerMatch);

            if (you == null)
            {
                elsePoints += them.GoalPoints;
                elseSeconds += them.SecondsPlayed;
                continue;
            }

            meetings++;
            if (you.Side == them.Side) { asTeammates++; continue; }

            asOpponents++;
            theirPoints += them.GoalPoints;
            theirSeconds += them.SecondsPlayed;

            // Sides are 0 = blue, 1 = orange.
            int yourScore = you.Side == 0 ? m.BlueScore : m.OrangeScore;
            int theirScore = you.Side == 0 ? m.OrangeScore : m.BlueScore;
            if (yourScore > theirScore) wins++;
            else if (yourScore < theirScore) losses++;
            else draws++;
        }

        double theirRate = theirSeconds <= 0 ? 0 : theirPoints / (theirSeconds / 60.0);
        double baseRate = elseSeconds <= 0 ? 0 : elsePoints / (elseSeconds / 60.0);

        string summary;
        if (asOpponents == 0)
            summary = meetings == 0
                ? $"You have no indexed matches with {target}."
                : $"You have only ever been on {target}'s team ({asTeammates} match(es)), never against them.";
        else
        {
            summary = $"You have faced {target} {asOpponents} time(s): {wins}W {losses}L"
                    + (draws > 0 ? $" {draws}D" : "") + ".";
            if (baseRate > 0 && theirRate > 0)
            {
                double delta = theirRate / baseRate - 1;
                if (Math.Abs(delta) >= 0.2)
                    summary += delta > 0
                        ? $" They score {delta:P0} faster against you than against everyone else — {theirRate:F2} vs {baseRate:F2} points per minute."
                        : $" They score {-delta:P0} slower against you than against everyone else — {theirRate:F2} vs {baseRate:F2} points per minute.";
            }
            if (asTeammates > 0) summary += $" You have also played {asTeammates} match(es) alongside them.";
        }

        return new HeadToHead
        {
            Viewer = viewer, Opponent = target,
            Meetings = meetings, AsOpponents = asOpponents, AsTeammates = asTeammates,
            Wins = wins, Losses = losses, Draws = draws,
            TheirPointsPerMinute = theirRate,
            TheirBaselinePointsPerMinute = baseRate,
            Summary = summary,
        };
    }

    // ------------------------------------------------------------------ counters

    private static void BuildCounters(
        CareerScout s,
        List<(MatchSummary match, PlayerSummary me, List<PlayerSummary> peers)> rows)
    {
        void Counter(InsightKind kind, InsightArea area, string title, string detail,
                     string? action, string? evidence, double confidence) =>
            s.Insights.Add(new Insight
            {
                Kind = kind, Area = area, Title = title, Detail = detail,
                Action = action, Evidence = evidence,
                Confidence = Math.Min(confidence, EvidenceCap(s.Matches)),
            });

        var scoring = s.Metric("Scoring")!;
        var finishing = s.Metric("Finishing")!;
        var hold = s.Metric("Hold time")!;
        var press = s.Metric("Press height")!;
        var goalCover = s.Metric("Goal cover")!;
        var physical = s.Metric("Physicality")!;
        var carrying = s.Metric("Carrying into contact")!;
        var hands = s.Metric("Hands")!;
        var security = s.Metric("Disc security")!;
        var atHome = s.Metric("Turnovers at home")!;
        var work = s.Metric("Work rate")!;
        var distribution = s.Metric("Distribution")!;
        var throwSpeed = s.Metric("Throw speed");

        // ---- how much of a threat are they at all ----
        if (scoring.Edge >= 0.3)
            Counter(InsightKind.Note, InsightArea.Finishing,
                "Primary scoring threat",
                $"{scoring.ValueText} points per minute against {scoring.PeerText} for the players around them, "
                + $"across {s.Matches} matches. This has been {scoring.TrendText}.",
                "Assign your best defender to them from the first whistle and accept giving up space elsewhere.",
                $"{scoring.ValueText} vs {scoring.PeerText} pts/min", 0.9);
        else if (scoring.Edge <= -0.3)
            Counter(InsightKind.Note, InsightArea.Finishing,
                "Not the one who beats you",
                $"{scoring.ValueText} points per minute against {scoring.PeerText} for the room. "
                + "They are not the player winning these games.",
                "Do not spend your best defender here. Cover their team-mates and let this one take the shot.",
                $"{scoring.ValueText} vs {scoring.PeerText} pts/min", 0.8);

        // ---- role ----
        double cover = goalCover.Value;
        if (cover > 0.3 && goalCover.Edge > 0.15)
            Counter(InsightKind.Counter, InsightArea.Defence,
                "Plays the goal",
                $"They spend {goalCover.ValueText} of their time in their own goalie box, against {goalCover.PeerText} for the room, "
                + $"and average {s.Metric("Saves")!.ValueText} saves per minute.",
                "Do not shoot into them from range — that is the shot they are set for. Drive to draw them off the line, then feed the trailer.",
                $"{goalCover.ValueText} in the box", 0.85);
        else if (press.Value > 0.55 && press.Edge > 0.1)
            Counter(InsightKind.Counter, InsightArea.Positioning,
                "Presses high and stays there",
                $"They average {press.ValueText} up the arena against {press.PeerText} for the room — they commit forward and rarely recover.",
                "Break out the instant they commit. The space behind them is the whole game: one long outlet turns it into a 2v1.",
                $"{press.ValueText} press height", 0.85);
        else if (press.Value < 0.4)
            Counter(InsightKind.Counter, InsightArea.Positioning,
                "Sits deep",
                $"They average {press.ValueText} up the arena against {press.PeerText} for the room — they hang back rather than joining the attack.",
                "Their half is where the game is won. Push numbers forward; they will not punish you on the counter.",
                $"{press.ValueText} press height", 0.75);

        // ---- what to do the moment they touch it ----
        if (hold.Edge > 0.25 && hold.Value > 2.5)
            Counter(InsightKind.Counter, InsightArea.Possession,
                "Holds the disc",
                $"They keep it {hold.ValueText}s per possession against {hold.PeerText}s for the room.",
                "Double them the moment they catch. They will not move it fast enough to punish the second defender leaving.",
                $"{hold.ValueText}s per possession", 0.85);
        else if (hold.Edge < -0.25 && distribution.Edge > 0)
            Counter(InsightKind.Counter, InsightArea.Passing,
                "One-touch player",
                $"They release in {hold.ValueText}s against {hold.PeerText}s for the room, and hand off "
                + $"{distribution.ValueText} times a minute against {distribution.PeerText}.",
                "Do not chase the catcher — you will never get there. Cover the passing lane and make them carry it instead.",
                $"{hold.ValueText}s per possession", 0.8);

        // ---- physical exchanges ----
        if (physical.Edge >= 0.3)
            Counter(InsightKind.Counter, InsightArea.Physicality,
                "Hunts bodies",
                $"{physical.ValueText} stuns per minute against {physical.PeerText} for the room, over {s.LiveMinutes:F0} minutes of play.",
                "Never approach them in a straight line, and keep your arms up entering their range. Bait the swing, then go past.",
                $"{physical.ValueText} stuns/min", 0.85);

        if (carrying.Edge <= -0.25)
            Counter(InsightKind.Counter, InsightArea.Physicality,
                "Carries into contact",
                $"They get stunned on the disc {carrying.ValueText} times a minute against {carrying.PeerText} for the room.",
                "Lead with the stun when they are carrying rather than reaching for the disc. They will skate into it.",
                $"{carrying.ValueText} stunned-on-disc/min", 0.8);

        // ---- hands and giveaways: where the free possessions come from ----
        if (hands.Edge <= -0.15)
            Counter(InsightKind.Counter, InsightArea.Possession,
                "Unreliable hands",
                $"They take {hands.ValueText} of the loose discs that come within reach; the room manages {hands.PeerText}.",
                "Contest every fifty-fifty with them rather than backing off — a meaningful share of those end up yours.",
                $"{hands.ValueText} of reachable discs", 0.8);

        if (atHome.Edge <= -0.25)
            Counter(InsightKind.Counter, InsightArea.Discipline,
                "Gives it away in their own third",
                $"{atHome.ValueText} giveaways per minute inside their own third, against {atHome.PeerText} for the room.",
                "Forecheck them. Put your first defender on them the moment they collect it behind their own line.",
                $"{atHome.ValueText} home giveaways/min", 0.85);
        else if (security.Edge <= -0.25)
            Counter(InsightKind.Counter, InsightArea.Discipline,
                "Loses the disc under pressure",
                $"{security.ValueText} turnovers per minute against {security.PeerText} for the room.",
                "Pressure rather than contain. They will cough it up if you close the first option.",
                $"{security.ValueText} turnovers/min", 0.8);

        // ---- the shot itself ----
        if (finishing.Edge >= 0.25 && finishing.Value > 0)
            Counter(InsightKind.Counter, InsightArea.Finishing,
                "Finishes what they get",
                $"{finishing.ValueText} of their shots go in, against {finishing.PeerText} for the room.",
                "Deny the shot rather than the carry. Once they are lined up the goalie is already beaten more often than not.",
                $"{finishing.ValueText} conversion", 0.8);
        else if (finishing.Edge <= -0.25 && finishing.PeerValue > 0)
            Counter(InsightKind.Counter, InsightArea.Finishing,
                "Wasteful shooter",
                $"Only {finishing.ValueText} of their shots go in, against {finishing.PeerText} for the room.",
                "Let them shoot from range. Every attempt they take from distance is possession back for you.",
                $"{finishing.ValueText} conversion", 0.75);

        if (throwSpeed is { Value: > 0, PeerValue: > 0 })
        {
            if (throwSpeed.Edge >= 0.15)
                Counter(InsightKind.Counter, InsightArea.Mechanics,
                    "Heavy release",
                    $"They release at {throwSpeed.ValueText} m/s against {throwSpeed.PeerText} m/s for the room.",
                    "Deny the wind-up instead of trying to react to the throw. Body them before they set.",
                    $"{throwSpeed.ValueText} m/s", 0.7);
            else if (throwSpeed.Edge <= -0.15)
                Counter(InsightKind.Counter, InsightArea.Mechanics,
                    "Light release",
                    $"They release at {throwSpeed.ValueText} m/s against {throwSpeed.PeerText} m/s for the room — their long throws hang.",
                    "Play the goalie a step off the line and read the flight. There is time to react to anything they throw from distance.",
                    $"{throwSpeed.ValueText} m/s", 0.7);
        }

        if (work.Edge <= -0.25)
            Counter(InsightKind.Counter, InsightArea.Movement,
                "Low work rate",
                $"{work.ValueText} metres per minute against {work.PeerText} for the room.",
                "Move the disc side to side. They do not cover ground to recover, so lateral play pulls them out of the play entirely.",
                $"{work.ValueText} m/min", 0.7);

        // ---- direction of travel: old tape can mislead ----
        var climbing = s.Metrics.Where(m => m.TrendEdge >= 0.3 && m.Name is "Scoring" or "Finishing" or "Physicality")
                                .OrderByDescending(m => m.TrendEdge).FirstOrDefault();
        if (climbing != null)
            Counter(InsightKind.Note, InsightArea.Finishing,
                $"{climbing.Name} is trending up",
                $"Their early matches averaged {climbing.EarlyValue.ToString(climbing.Format)}; recent ones "
                + $"{climbing.RecentValue.ToString(climbing.Format)}. They are better now than their overall record suggests.",
                "Scout the recent games, not the average. Whatever worked against them months ago may not any more.",
                $"{climbing.EarlyValue.ToString(climbing.Format)} → {climbing.RecentValue.ToString(climbing.Format)}", 0.7);

        // ---- your own record against them ----
        if (s.Versus is { HasFaced: true } h)
        {
            bool losing = h.Losses > h.Wins;
            Counter(losing ? InsightKind.Weakness : InsightKind.Strength, InsightArea.Possession,
                $"Your record against them: {h.Record}",
                h.Summary,
                losing
                    ? "They have your number so far. Change one specific thing from the reads above rather than playing the same game harder."
                    : null,
                $"{h.AsOpponents} meetings", 0.85);
        }

        if (s.Matches < ThinEvidence)
            Counter(InsightKind.Note, InsightArea.Possession,
                "Thin evidence",
                $"Only {s.Matches} indexed match(es) with {s.Player} in them ({s.LiveMinutes:F0} minutes). "
                + "Treat everything above as a starting read rather than a settled tendency.",
                "Index more replays containing them to firm this up.",
                $"{s.Matches} matches", 0.95);

        s.Insights.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
    }

    /// <summary>Caps how confident any read can be when it rests on very few matches.</summary>
    private static double EvidenceCap(int matches) => matches switch
    {
        <= 1 => 0.45,
        2 => 0.6,
        3 => 0.7,
        <= 6 => 0.8,
        _ => 1.0,
    };

    private static string DescribeStyle(CareerScout s)
    {
        double cover = s.Metric("Goal cover")?.Value ?? 0;
        double press = s.Metric("Press height")?.Value ?? 0.5;
        double stuns = s.Metric("Physicality")?.Edge ?? 0;
        double hold = s.Metric("Hold time")?.Edge ?? 0;

        string role = cover > 0.3 ? "goalie"
                    : press > 0.55 ? "presser"
                    : press < 0.4 ? "deep rotator"
                    : "rotator";

        string temper = stuns >= 0.3 ? "physical " : "";
        string disc = hold > 0.25 ? ", carries the disc" : hold < -0.25 ? ", moves it early" : "";
        return $"{temper}{role}{disc}";
    }

    /// <summary>
    /// Metrics that describe a style rather than a standard. How high somebody presses or how
    /// long they hold the disc is a tendency to plan around, not something they are good or
    /// bad at, so these never appear as a strength or a weakness.
    /// </summary>
    private static readonly HashSet<string> Descriptive =
        new() { "Hold time", "Press height", "Goal cover", "Work rate" };

    /// <summary>Reads naturally in "their strength is …" and "attack their …".</summary>
    private static string Phrase(string metric) => metric switch
    {
        "Scoring" => "scoring rate",
        "Saves" => "goaltending",
        "Disc security" => "disc security",
        "Turnovers at home" => "handling of the disc in their own third",
        "Carrying into contact" => "disc protection under contact",
        "Hands" => "hands",
        "Throw speed" => "throw power",
        _ => metric.ToLowerInvariant(),
    };

    private static string Describe(CareerScout s)
    {
        string span = s.FirstSeen.Year > 2000
            ? $"{s.FirstSeen:d MMM} to {s.LastSeen:d MMM}"
            : "your library";

        var quality = s.Metrics.Where(m => !Descriptive.Contains(m.Name)).ToList();
        var worst = quality.Where(m => m.Edge <= -0.12).OrderBy(m => m.Edge).FirstOrDefault();
        var best = quality.Where(m => m.Edge >= 0.12).OrderByDescending(m => m.Edge).FirstOrDefault();

        string line = $"{s.Player} across {s.Matches} indexed matches ({s.LiveMinutes:F0} minutes of live play, {span}): "
                    + $"{s.GoalPoints} points from {s.Goals} goals, {s.Assists} assists, {s.Saves} saves and {s.Stuns} stuns. "
                    + $"Plays as a {s.Style}.";

        if (best != null) line += $" Their strength is {Phrase(best.Name)}.";
        if (worst != null) line += $" Attack their {Phrase(worst.Name)}.";
        if (s.Versus is { HasFaced: true } h) line += $" You are {h.Record} against them.";
        return line;
    }
}
