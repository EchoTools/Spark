// Ported from the Replay Analyser (EchoAnalyser.Core/Analysis/TeamAnalyser.cs). Logic is unchanged; only the
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

/// <summary>Where a player actually spent the match, regardless of what they were told to play.</summary>
public enum PlayerRole
{
    /// <summary>Sits on the goal. Rarely leaves the defensive third.</summary>
    Goalie,
    /// <summary>Plays behind the disc — the back half of the stack.</summary>
    Back,
    /// <summary>Covers the whole sheet, rotating between the two ends.</summary>
    Rotator,
    /// <summary>Lives in the attacking third — the front of the stack.</summary>
    Front,
}

public sealed class RoleAssignment
{
    public PlayerAnalysis Player { get; init; } = null!;
    public PlayerRole Role { get; init; }
    /// <summary>0 = own goal, 1 = the goal they attack.</summary>
    public double FieldProgress { get; init; }
    public double GoalieShare { get; init; }
    public double LastDefenderShare { get; init; }
    /// <summary>Plain-language reason, so the label can be checked against the numbers.</summary>
    public string Evidence { get; init; } = "";

    public string RoleName => Role switch
    {
        PlayerRole.Goalie => "Goalie",
        PlayerRole.Back => "Back stack",
        PlayerRole.Front => "Front stack",
        _ => "Rotator",
    };
}

/// <summary>How one team played: who filled which slot, and the shape and habits that came out of it.</summary>
public sealed class TeamProfile
{
    public TeamSide Side { get; init; }
    public string Name => Side == TeamSide.Blue ? "BLUE" : "ORANGE";
    public int Score { get; init; }
    public List<RoleAssignment> Roles { get; } = new();

    public IEnumerable<RoleAssignment> Front => Roles.Where(r => r.Role == PlayerRole.Front);
    public IEnumerable<RoleAssignment> Back =>
        Roles.Where(r => r.Role is PlayerRole.Back or PlayerRole.Goalie);

    /// <summary>Average field progress for the side — how high up the sheet they camp.</summary>
    public double PressHeight { get; init; }
    /// <summary>Share of live play with at least one player home. Higher is more disciplined.</summary>
    public double RotationScore { get; init; }
    /// <summary>Share of live play with nobody home at all.</summary>
    public double OpenBackShare { get; init; }
    public double Spacing { get; init; }
    public double AverageHold { get; init; }
    public double StunsPerMinute { get; init; }
    public double ShotAccuracy { get; init; }
    public double PossessionShare { get; init; }

    public string StackShape { get; set; } = "";
    /// <summary>True when the outfield all play at the same height — a rotation, not a stack.</summary>
    public bool IsFlatRotation { get; set; }
    public List<string> StyleTags { get; } = new();
    public string Summary { get; set; } = "";

    /// <summary>0-100 overall, from the parts of the game that decide matches.</summary>
    public double Rating { get; set; }
}

/// <summary>
/// Team-level reading of a match: the shape each side played, and where the game was won.
///
/// Everything is measured against the other team in the same match rather than against
/// absolutes, because "high press" only means anything relative to who you were playing.
/// </summary>
public static class TeamAnalyser
{
    /// <summary>Time on the goal, as a share of live play, that marks a dedicated goalie.</summary>
    private const double GoalieShareThreshold = 0.30;
    private const double LastDefenderThreshold = 0.45;

    public static TeamProfile BuildProfile(MatchAnalysis m, TeamSide side)
    {
        var team = side == TeamSide.Blue ? m.Blue : m.Orange;
        var players = Participating(team.Players);

        var profile = new TeamProfile
        {
            Side = side,
            Score = team.Score,
            PressHeight = Weighted(players, p => p.AverageFieldProgress),
            RotationScore = 1 - team.DefendersBackShare[0],
            OpenBackShare = team.DefendersBackShare[0],
            Spacing = team.AverageSpread,
            AverageHold = players.Count == 0 ? 0 : Weighted(players, p => p.AveragePossessionSeconds),
            StunsPerMinute = players.Sum(p => p.Stuns) /
                             Math.Max(1.0, players.Sum(p => p.SecondsPlayed) / 60.0),
            ShotAccuracy = team.ShotAccuracy,
            PossessionShare = team.PossessionShare,
            StackShape = "",
        };

        foreach (var r in AssignRoles(players)) profile.Roles.Add(r);
        profile.StackShape = Shape(profile);
        FlattenRoles(profile);
        return profile;
    }

    /// <summary>
    /// Once a side is judged a flat rotation, calling two of them "front stack" contradicts
    /// the shape we just reported. Collapse the outfield labels so the summary and the
    /// per-player rows tell the same story.
    /// </summary>
    private static void FlattenRoles(TeamProfile p)
    {
        if (!p.IsFlatRotation) return;

        for (int i = 0; i < p.Roles.Count; i++)
        {
            var r = p.Roles[i];
            if (r.Role == PlayerRole.Goalie) continue;
            p.Roles[i] = new RoleAssignment
            {
                Player = r.Player,
                Role = PlayerRole.Rotator,
                FieldProgress = r.FieldProgress,
                GoalieShare = r.GoalieShare,
                LastDefenderShare = r.LastDefenderShare,
                Evidence = $"split {r.Player.DefensiveThirdShare:P0} home / "
                         + $"{r.Player.OffensiveThirdShare:P0} away — the side rotated rather than stacked",
            };
        }
    }

    /// <summary>Build both profiles together so their style tags and ratings can be relative.</summary>
    public static (TeamProfile blue, TeamProfile orange) BuildBoth(MatchAnalysis m)
    {
        var blue = BuildProfile(m, TeamSide.Blue);
        var orange = BuildProfile(m, TeamSide.Orange);
        Compare(m, blue, orange);
        Compare(m, orange, blue);
        return (blue, orange);
    }

    /// <summary>
    /// Drop anyone who was in the lobby but not playing.
    ///
    /// An idle player parked in their own goal reads as a textbook goalie — 90%+ in the box,
    /// last man back almost always — and would hand the team a defensive shape it never had.
    /// Distance covered separates them cleanly: a real goalie still moves, an idle one does
    /// not, so anyone travelling a small fraction of what their team-mates did is excluded.
    /// </summary>
    private static List<PlayerAnalysis> Participating(IEnumerable<PlayerAnalysis> all)
    {
        var present = all.Where(p => p.SecondsPlayed > 20).ToList();
        if (present.Count < 2) return present;

        var distances = present.Select(p => p.DistanceTravelled).OrderBy(d => d).ToList();
        double median = distances[distances.Count / 2];
        if (median <= 0) return present;

        var active = present.Where(p =>
            p.DistanceTravelled >= median * 0.25 ||
            p.GoalPoints > 0 || p.Saves > 0 || p.Stuns > 0 || p.ShotsTaken > 0).ToList();

        return active.Count >= 2 ? active : present;
    }

    // ------------------------------------------------------------------ roles

    private static IEnumerable<RoleAssignment> AssignRoles(List<PlayerAnalysis> players)
    {
        if (players.Count == 0) yield break;

        // Rank by how far up the sheet each player lives; the labels then describe the
        // team's own distribution rather than an absolute idea of "high" or "deep".
        var ordered = players.OrderBy(p => p.AverageFieldProgress).ToList();

        foreach (var p in ordered)
        {
            double t = Math.Max(p.SecondsPlayed, 1);
            double goalieShare = p.SecondsInGoalieBox / t;
            double lastDefShare = p.SecondsAsLastDefender / t;
            double progress = p.AverageFieldProgress;

            PlayerRole role;
            string evidence;

            if (goalieShare >= GoalieShareThreshold && lastDefShare >= LastDefenderThreshold)
            {
                role = PlayerRole.Goalie;
                evidence = $"{goalieShare:P0} of live play inside the goalie box and last man back {lastDefShare:P0} of the time";
            }
            else if (progress <= 0.44 || lastDefShare >= LastDefenderThreshold)
            {
                role = PlayerRole.Back;
                evidence = $"average position {progress:P0} up the sheet, last man back {lastDefShare:P0} of the time";
            }
            else if (progress >= 0.56 && p.OffensiveThirdShare > p.DefensiveThirdShare)
            {
                role = PlayerRole.Front;
                evidence = $"{p.OffensiveThirdShare:P0} of live play in the attacking third against {p.DefensiveThirdShare:P0} at home";
            }
            else
            {
                role = PlayerRole.Rotator;
                evidence = $"split {p.DefensiveThirdShare:P0} home / {p.OffensiveThirdShare:P0} away — covers the whole sheet";
            }

            yield return new RoleAssignment
            {
                Player = p,
                Role = role,
                FieldProgress = progress,
                GoalieShare = goalieShare,
                LastDefenderShare = lastDefShare,
                Evidence = evidence,
            };
        }
    }

    private static string Shape(TeamProfile p)
    {
        if (p.Roles.Count == 0) return "not enough footage";

        int goalies = p.Roles.Count(r => r.Role == PlayerRole.Goalie);
        int back = p.Roles.Count(r => r.Role == PlayerRole.Back);
        int rot = p.Roles.Count(r => r.Role == PlayerRole.Rotator);
        int front = p.Roles.Count(r => r.Role == PlayerRole.Front);

        // A side whose outfield players all sit at the same height is not "nobody forward" —
        // it is a rotation, where whoever is nearest takes the front slot. Judging that by
        // absolute thresholds mislabels good rotating teams as passive.
        var outfield = p.Roles.Where(r => r.Role != PlayerRole.Goalie).Select(r => r.FieldProgress).ToList();
        bool flat = outfield.Count >= 2 && outfield.Max() - outfield.Min() < 0.14;
        p.IsFlatRotation = flat;

        if (flat)
            return goalies > 0
                ? $"{goalies} on goal, {outfield.Count} rotating in front"
                : $"{outfield.Count} rotating, no fixed stack";

        if (goalies > 0 && front > 0)
            return $"{goalies} on goal, {back + rot} through the middle, {front} up";
        if (goalies > 0)
        {
            var parts = new List<string> { $"{goalies} on goal" };
            if (back > 0) parts.Add($"{back} sitting back");
            if (rot > 0) parts.Add($"{rot} rotating");
            return string.Join(", ", parts);
        }
        if (rot == p.Roles.Count) return $"{rot} rotating, no fixed goalie";
        if (front == 0) return $"{back + rot} playing behind the disc";
        if (back + goalies == 0) return $"all {front + rot} pushed up, no dedicated back";
        return $"{front} up, {rot} rotating, {back + goalies} back";
    }

    // ------------------------------------------------------------------ comparison

    private static void Compare(MatchAnalysis m, TeamProfile us, TeamProfile them)
    {
        if (us.PressHeight > them.PressHeight + 0.05)
            us.StyleTags.Add("press high");
        else if (us.PressHeight < them.PressHeight - 0.05)
            us.StyleTags.Add("sit deep");

        if (us.OpenBackShare > them.OpenBackShare + 0.08)
            us.StyleTags.Add("leave the back open");
        else if (us.OpenBackShare < them.OpenBackShare - 0.08)
            us.StyleTags.Add("stay disciplined at the back");

        if (us.Spacing > them.Spacing + 2) us.StyleTags.Add("stretch the sheet wide");
        else if (us.Spacing < them.Spacing - 2) us.StyleTags.Add("play bunched together");

        if (us.AverageHold > them.AverageHold * 1.2) us.StyleTags.Add("carry the disc rather than move it");
        else if (us.AverageHold < them.AverageHold * 0.8) us.StyleTags.Add("move the disc quickly");

        if (us.StunsPerMinute > them.StunsPerMinute * 1.25) us.StyleTags.Add("play physically");
        else if (us.StunsPerMinute < them.StunsPerMinute * 0.75) us.StyleTags.Add("avoid contact");

        us.Rating = RateTeam(us, them);
        us.Summary = Describe(m, us, them);
    }

    /// <summary>
    /// A 0-100 read on how well the side played, weighted towards the things that decide
    /// matches: putting the disc in, keeping it, and not leaving the goal open.
    /// </summary>
    private static double RateTeam(TeamProfile us, TeamProfile them)
    {
        double scoreShare = us.Score + them.Score <= 0 ? 0.5 : (double)us.Score / (us.Score + them.Score);
        double accuracy = us.ShotAccuracy + them.ShotAccuracy <= 0
            ? 0.5
            : us.ShotAccuracy / (us.ShotAccuracy + them.ShotAccuracy);
        double possession = Math.Clamp(us.PossessionShare, 0, 1);
        double discipline = Math.Clamp(1 - us.OpenBackShare, 0, 1);

        return Math.Clamp(scoreShare * 0.4 + accuracy * 0.2 + possession * 0.2 + discipline * 0.2, 0, 1) * 100;
    }

    private static string Describe(MatchAnalysis m, TeamProfile us, TeamProfile them)
    {
        var goalie = us.Roles.FirstOrDefault(r => r.Role == PlayerRole.Goalie);
        var front = us.Front.Select(r => r.Player.Name).ToList();
        var back = us.Back.Select(r => r.Player.Name).ToList();

        string shape = $"{us.Name} played {us.StackShape}";

        // When everyone plays at the same height there is no front stack to name, and calling
        // two of them "up top" would contradict the shape we just reported.
        string names = "";
        if (!us.IsFlatRotation && front.Count > 0) names = $" with {Join(front)} up top";
        if (goalie != null)
            names += $"{(names.Length > 0 ? " and" : " with")} {goalie.Player.Name} on the goal";
        else if (!us.IsFlatRotation && back.Count > 0 && front.Count > 0)
            names += $" and {Join(back)} holding the back";

        string tags = us.StyleTags.Count > 0 ? $" They {Join(us.StyleTags)}." : "";
        string result = us.Score > them.Score
            ? $" They won it {us.Score}-{them.Score}"
            : us.Score < them.Score
                ? $" They lost it {us.Score}-{them.Score}"
                : $" It finished level at {us.Score}";

        return shape + names + "." + tags + result
             + $", converting {us.ShotAccuracy:P0} of their shots with {us.PossessionShare:P0} of the disc.";
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static double Weighted(List<PlayerAnalysis> players, Func<PlayerAnalysis, double> pick)
    {
        double total = players.Sum(p => p.SecondsPlayed);
        if (total <= 0) return 0;
        return players.Sum(p => pick(p) * p.SecondsPlayed) / total;
    }

    // ------------------------------------------------------------------ matchup

    /// <summary>
    /// What the opposition did to this team. This is the half of the analysis a team cannot
    /// get from their own stat line: which of their shapes was exploited, and by whom.
    /// </summary>
    public static List<Insight> BuildMatchup(MatchAnalysis m, TeamProfile us, TeamProfile them)
    {
        var insights = new List<Insight>();
        var conceded = m.Goals.Where(g => g.Side != us.Side).ToList();
        var scored = m.Goals.Where(g => g.Side == us.Side).ToList();

        // ---- were we open at the back when they scored? ----
        if (conceded.Count >= 3)
        {
            int open = conceded.Count(g => g.DefendersBack == 0);
            if (open >= conceded.Count * 0.5)
                insights.Add(new Insight
                {
                    Kind = InsightKind.Weakness, Area = InsightArea.TeamShape,
                    Title = "They scored into an empty back",
                    Detail = $"{open} of the {conceded.Count} goals {them.Name} scored went in with nobody "
                           + $"in your defensive third. {us.Name} had the back open {us.OpenBackShare:P0} of live play.",
                    Action = "Set a hard rule that one player stays below the disc. Losing an attacker costs less than conceding the counter.",
                    Evidence = $"{open}/{conceded.Count} with nobody home",
                    Confidence = 0.9,
                });
        }

        // ---- press mismatch ----
        double gap = them.PressHeight - us.PressHeight;
        if (Math.Abs(gap) > 0.08)
        {
            bool theyHigher = gap > 0;
            insights.Add(new Insight
            {
                Kind = theyHigher ? InsightKind.Weakness : InsightKind.Note,
                Area = InsightArea.TeamShape,
                Title = theyHigher ? "They pushed further up than you did" : "You played higher than they did",
                Detail = $"{them.Name} averaged {them.PressHeight:P0} up the sheet against {us.PressHeight:P0} for {us.Name}.",
                Action = theyHigher
                    ? "They are taking the space in front of your goal for free. Meet them at the midline instead of collecting the disc behind your own."
                    : "You held the higher ground. Keep an outlet behind the disc so the turnover does not become a breakaway.",
                Evidence = $"{them.PressHeight:P0} vs {us.PressHeight:P0}",
                Confidence = 0.75,
            });
        }

        // ---- who hurt us ----
        var theirBest = m.AllPlayers
            .Where(p => p.Side != us.Side && p.SecondsPlayed > 30)
            .OrderByDescending(p => p.GoalPoints)
            .ThenByDescending(p => p.Goals)
            .ThenByDescending(p => p.ShotAccuracy)
            .FirstOrDefault();
        if (theirBest is { GoalPoints: >= 4 })
        {
            var ourDeepest = us.Roles.OrderBy(r => r.FieldProgress).FirstOrDefault();
            insights.Add(new Insight
            {
                Kind = InsightKind.Counter, Area = InsightArea.Defence,
                Title = $"{theirBest.Name} did the damage",
                Detail = $"{theirBest.GoalPoints} of {them.Score} points, {theirBest.Goals} goals from "
                       + $"{theirBest.ShotsTaken} shots ({theirBest.ShotAccuracy:P0})."
                       + (ourDeepest != null ? $" Your deepest player was {ourDeepest.Player.Name}." : ""),
                Action = ourDeepest != null
                    ? $"Give {ourDeepest.Player.Name} help rather than leaving them one-on-one. A second body at the three-line forces the pass instead of the shot."
                    : "Assign a specific player to them rather than defending zonally.",
                Evidence = $"{theirBest.GoalPoints} of {them.Score} points",
                Confidence = 0.85,
            });
        }

        // ---- shape mismatch: front count ----
        int ourFront = us.Front.Count();
        int theirFront = them.Front.Count();
        if (theirFront > ourFront && theirFront >= 2)
            insights.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.TeamShape,
                Title = "Outnumbered in the attacking half",
                Detail = $"{them.Name} committed {theirFront} players forward against your {ourFront}. "
                       + $"Their shape was {them.StackShape}; yours was {us.StackShape}.",
                Action = "Either match their numbers and accept the trade, or collapse two defenders on the carrier and play the counter off the turnover.",
                Evidence = $"{theirFront} up vs {ourFront}",
                Confidence = 0.7,
            });

        // ---- conversion ----
        if (them.ShotAccuracy > us.ShotAccuracy * 1.6 && m.Orange.ShotsTaken + m.Blue.ShotsTaken > 10)
            insights.Add(new Insight
            {
                Kind = InsightKind.Weakness, Area = InsightArea.Finishing,
                Title = "They made their chances count and you did not",
                Detail = $"{them.Name} converted {them.ShotAccuracy:P0} of their shots against {us.ShotAccuracy:P0} for {us.Name}"
                       + (scored.Count > 0 || conceded.Count > 0
                            ? $" — {conceded.Count} goals conceded against {scored.Count} scored."
                            : "."),
                Action = "Stop shooting from range. Work one more pass to get inside the three-line where their goalie has no time to set.",
                Evidence = $"{them.ShotAccuracy:P0} vs {us.ShotAccuracy:P0}",
                Confidence = 0.8,
            });

        // ---- goal profile: where their scoring came from ----
        if (conceded.Count >= 3)
        {
            double avgDist = conceded.Average(g => g.DistanceThrown);
            bool longRange = avgDist > 18;
            insights.Add(new Insight
            {
                Kind = InsightKind.Counter, Area = InsightArea.Defence,
                Title = longRange ? "They beat you from distance" : "They walked it in",
                Detail = $"Their goals averaged {avgDist:F0}m out"
                       + (longRange ? " — long shots your goalie did not stop." : " — scored from inside, past the last defender."),
                Action = longRange
                    ? "Your goalie is starting too deep. Step out to cut the angle and make them come inside."
                    : "You are being beaten at the last line. Add a body between the three-line and the goal rather than chasing the disc high.",
                Evidence = $"{avgDist:F0}m average",
                Confidence = 0.7,
            });
        }

        insights.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return insights;
    }
}
