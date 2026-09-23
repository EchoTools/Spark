// Ported from the Replay Analyser (EchoAnalyser.Core/Model/MatchAnalysis.cs). Logic is unchanged; only the
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
using Spark.ReplayAnalyser.Analysis;

namespace Spark.ReplayAnalyser.Model;

public enum MatchEventKind
{
    Goal, Save, Stun, Steal, Interception, Block, Pass, Catch,
    ShotTaken, Assist, PossessionGained, PossessionLost, Throw, Turnover,
}

public sealed class MatchEvent
{
    public MatchEventKind Kind { get; init; }
    public TimeSpan Time { get; init; }
    public float GameClock { get; init; }
    public string Player { get; init; } = "";
    public long UserId { get; init; }
    public TeamSide Side { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 DiscPosition { get; init; }

    /// <summary>Free-form detail: goal type, the player involved on the other end of a pass, etc.</summary>
    public string? Detail { get; init; }
    public float Value { get; init; }
    public string? SecondaryPlayer { get; init; }

    public string Label => Kind switch
    {
        MatchEventKind.Goal => $"Goal — {Player}",
        MatchEventKind.Save => $"Save — {Player}",
        MatchEventKind.Stun => $"Stun — {Player}",
        MatchEventKind.Steal => $"Steal — {Player}",
        MatchEventKind.Interception => $"Interception — {Player}",
        MatchEventKind.Block => $"Block — {Player}",
        MatchEventKind.Turnover => $"Turnover — {Player}",
        _ => $"{Kind} — {Player}",
    };
}

/// <summary>A single continuous stretch of one player holding the disc.</summary>
public sealed class PossessionSpan
{
    public string Player { get; init; } = "";
    public long UserId { get; init; }
    public TeamSide Side { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; set; }
    public Vector3 StartPosition { get; init; }
    public Vector3 EndPosition { get; set; }
    public double Seconds => (End - Start).TotalSeconds;
    /// <summary>How the span ended: "pass", "shot", "turnover", "end of match", or "unknown".</summary>
    public string Outcome { get; set; } = "unknown";
    public bool EndedInShot => Outcome == "shot";
    public bool EndedInTurnover => Outcome == "turnover";
}

public sealed class ThrowRecord
{
    public string Player { get; init; } = "";
    public TeamSide Side { get; init; }
    public TimeSpan Time { get; init; }
    public float TotalSpeed { get; init; }
    public float ArmSpeed { get; init; }
    public float SpeedFromMovement { get; init; }
    public float SpeedFromWrist { get; init; }
    public float OffAxisSpinDeg { get; init; }
    public float WristThrowPenalty { get; init; }
    public float OffAxisPenalty { get; init; }
    public float ThrowMovePenalty { get; init; }
    public float RotPerSec { get; init; }
    public Vector3 Position { get; init; }

    /// <summary>Fraction of potential speed lost to the three penalty terms.</summary>
    public float TotalPenalty => WristThrowPenalty + OffAxisPenalty + ThrowMovePenalty;
}

public sealed class GoalRecord
{
    public TimeSpan Time { get; init; }
    public TeamSide Side { get; init; }
    public string Scorer { get; init; } = "";
    public string? Assist { get; init; }
    public int Points { get; init; }
    public string GoalType { get; init; } = "";
    public float DiscSpeed { get; init; }
    public float DistanceThrown { get; init; }
    public int BlueScore { get; init; }
    public int OrangeScore { get; init; }

    /// <summary>How many defenders the conceding side had in their own third as this went in.</summary>
    public int DefendersBack { get; init; }
}

/// <summary>Occupancy counts over a 2D grid, used for the heatmaps.</summary>
public sealed class Heatmap
{
    public int Width { get; }
    public int Height { get; }
    public float[,] Cells { get; }
    public float Max { get; private set; }

    public Heatmap(int width, int height)
    {
        Width = width; Height = height;
        Cells = new float[width, height];
    }

    public void Add(float normX, float normY, float weight = 1f)
    {
        int x = (int)Math.Clamp(normX * Width, 0, Width - 1);
        int y = (int)Math.Clamp(normY * Height, 0, Height - 1);
        Cells[x, y] += weight;
        if (Cells[x, y] > Max) Max = Cells[x, y];
    }

    public float Normalized(int x, int y) => Max <= 0 ? 0 : Cells[x, y] / Max;

    /// <summary>
    /// The value at the given percentile of the occupied cells. Scaling a heatmap by its
    /// single hottest cell flattens everything else into the noise floor — one long stall in
    /// front of goal should not erase the rest of the picture — so the renderer scales by a
    /// high percentile instead and lets the very hottest cells clip.
    /// </summary>
    public float Percentile(double fraction)
    {
        var values = new List<float>();
        foreach (var v in Cells) if (v > 0) values.Add(v);
        if (values.Count == 0) return 0;
        values.Sort();
        int i = (int)Math.Clamp(fraction * (values.Count - 1), 0, values.Count - 1);
        return values[i];
    }
}

public sealed class PlayerAnalysis
{
    public string Name { get; set; } = "";
    public long UserId { get; set; }
    public TeamSide Side { get; set; }
    public int Level { get; set; }
    public int Number { get; set; }

    // ---- raw scoreboard ----
    /// <summary>
    /// The game's own <c>points</c> counter. In public matches this equals the points the
    /// player put on the board; in private matches it is a personal score that can total far
    /// more than the team scored, so <see cref="GoalPoints"/> is what the scoreboard uses.
    /// </summary>
    public int Points { get; set; }

    /// <summary>
    /// Points this player actually put on the board, summed from the goals attributed to
    /// them. This always reconciles with the team score.
    /// </summary>
    public int GoalPoints { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Saves { get; set; }
    public int Stuns { get; set; }
    public int Passes { get; set; }
    public int Catches { get; set; }
    public int Steals { get; set; }
    public int Blocks { get; set; }
    public int Interceptions { get; set; }
    public int ShotsTaken { get; set; }
    public float PossessionTime { get; set; }

    // ---- derived ----
    public double SecondsPlayed { get; set; }
    public double DistanceTravelled { get; set; }
    public double AverageSpeed { get; set; }
    public double MaxSpeed { get; set; }
    public int TimesStunned { get; set; }
    public double SecondsStunned { get; set; }
    public int Turnovers { get; set; }
    public int PossessionCount { get; set; }
    public double AveragePossessionSeconds =>
        PossessionCount == 0 ? 0 : PossessionTime / PossessionCount;
    public double ShotAccuracy => ShotsTaken == 0 ? 0 : (double)Goals / ShotsTaken;

    /// <summary>Share of match time spent in each third of the field, own goal first.</summary>
    public double DefensiveThirdShare { get; set; }
    public double NeutralShare { get; set; }
    public double OffensiveThirdShare { get; set; }

    public double AverageFieldProgress { get; set; }
    public double SecondsAsLastDefender { get; set; }
    public double SecondsInGoalieBox { get; set; }

    public Vector3 AveragePosition { get; set; }

    /// <summary>Errors we could identify frame by frame — see <see cref="Mistake"/>.</summary>
    public List<Mistake> Mistakes { get; } = new();

    public int MissedGrabs => Mistakes.Count(x => x.Kind == MistakeKind.MissedGrab);
    public int LostContests => Mistakes.Count(x => x.Kind == MistakeKind.LostContest);
    public int CarriedIntoStun => Mistakes.Count(x => x.Kind == MistakeKind.CarriedIntoStun);
    public int TurnoversAtHome => Mistakes.Count(x => x.Kind == MistakeKind.TurnoverAtHome);

    /// <summary>
    /// Chances to take a free disc where the hand actually reached it. The denominator for
    /// grab reliability — a miss only means something against how often they were in range.
    /// </summary>
    public int GrabChances { get; set; }
    public double GrabSuccessRate => GrabChances == 0 ? 0 : 1 - (double)MissedGrabs / GrabChances;

    public List<ThrowRecord> Throws { get; } = new();
    public List<PossessionSpan> Possessions { get; } = new();
    public List<MatchEvent> Events { get; } = new();

    /// <summary>
    /// This player's movement, as polylines in arena space. Each entry is one continuous
    /// stroke; a new one starts after a respawn, a stoppage, or a gap in the recording, so
    /// drawing them never joins two unrelated positions with a false straight line.
    /// </summary>
    public List<List<Vector3>> Tracks { get; } = new();

    /// <summary>Where this player was standing when they took each shot.</summary>
    public List<Vector3> ShotPositions { get; } = new();
    public List<Vector3> GoalPositions { get; } = new();

    /// <summary>Counts of passes made to each team-mate.</summary>
    public Dictionary<string, int> PassTargets { get; } = new();

    /// <summary>
    /// Handovers to a team-mate counted from the disc changing hands. Private matches report
    /// <c>passes</c> as zero for everybody, so this is what the UI falls back to.
    /// </summary>
    public int Handovers => PassTargets.Values.Sum();

    /// <summary>
    /// The pass count to reason about: the game's own figure where it has one, and our
    /// counted handovers where it does not.
    /// </summary>
    public int PassCount => Passes > 0 ? Passes : Handovers;

    /// <summary>
    /// True when the recording carries real throw telemetry for this player. Echo VR only
    /// fills <c>last_throw</c> for the client that recorded the replay, so for everyone else
    /// throw mechanics are simply unknown — which is different from being bad at throwing.
    /// </summary>
    public bool HasThrowTelemetry { get; set; }

    public double AverageThrowSpeed => Throws.Count == 0 ? 0 : Throws.Average(t => t.TotalSpeed);
    public double MaxThrowSpeed => Throws.Count == 0 ? 0 : Throws.Max(t => t.TotalSpeed);
    public double AverageThrowPenalty => Throws.Count == 0 ? 0 : Throws.Average(t => t.TotalPenalty);

    public double PointsPerMinute => SecondsPlayed <= 0 ? 0 : GoalPoints / (SecondsPlayed / 60.0);
    public double StunsPerMinute => SecondsPlayed <= 0 ? 0 : Stuns / (SecondsPlayed / 60.0);
    public double PossessionShare { get; set; }

    public int AveragePing { get; set; }
}

public sealed class TeamAnalysis
{
    public TeamSide Side { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public List<PlayerAnalysis> Players { get; } = new();

    public float PossessionSeconds { get; set; }
    public double PossessionShare { get; set; }
    public int Goals => Players.Sum(p => p.Goals);
    public int Saves => Players.Sum(p => p.Saves);
    public int Stuns => Players.Sum(p => p.Stuns);
    public int Turnovers => Players.Sum(p => p.Turnovers);
    public int ShotsTaken => Players.Sum(p => p.ShotsTaken);
    public double ShotAccuracy => ShotsTaken == 0 ? 0 : (double)Goals / ShotsTaken;

    /// <summary>Mean distance between team-mates — low means clustered, high means stretched.</summary>
    public double AverageSpread { get; set; }
    /// <summary>Share of playing time with 0, 1, 2, 3+ players back on defence.</summary>
    public double[] DefendersBackShare { get; set; } = new double[4];
}

/// <summary>One sampled point on a time-series chart.</summary>
public readonly record struct TimePoint(double Seconds, double Value);

public sealed class MatchAnalysis
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public string Format { get; set; } = "";
    public string MapName { get; set; } = "";
    public string MatchType { get; set; } = "";
    public bool PrivateMatch { get; set; }
    public string SessionId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
    public int FrameCount { get; set; }
    public double SampleRateHz { get; set; }

    /// <summary>Seconds of actual live play — excludes round starts, goal celebrations and pauses.</summary>
    public double LiveSeconds { get; set; }
    public TimeSpan LiveTime => TimeSpan.FromSeconds(LiveSeconds);

    /// <summary>Seconds spent in each <c>game_status</c>, so the UI can show where the clock went.</summary>
    public Dictionary<string, double> StatusSeconds { get; } = new();

    /// <summary>The player whose client recorded this replay, if we could identify it.</summary>
    public string? RecordingPlayer { get; set; }
    public bool IsPartial { get; set; }
    public List<string> Diagnostics { get; } = new();

    public int BlueScore { get; set; }
    public int OrangeScore { get; set; }

    /// <summary>
    /// How many rounds this file contains. Spark keeps recording across consecutive games,
    /// so one capture can hold several, and the scores here are the totals over all of them.
    /// </summary>
    public int RoundsRecorded { get; set; } = 1;
    public TeamAnalysis Blue { get; set; } = new() { Side = TeamSide.Blue, Name = "BLUE" };
    public TeamAnalysis Orange { get; set; } = new() { Side = TeamSide.Orange, Name = "ORANGE" };

    public List<GoalRecord> Goals { get; } = new();
    /// <summary>Every identified error in the match, both sides.</summary>
    public List<Mistake> Mistakes { get; } = new();
    public List<MatchEvent> Events { get; } = new();
    public List<PossessionSpan> Possessions { get; } = new();

    /// <summary>Score difference (blue minus orange) sampled through the match.</summary>
    public List<TimePoint> ScoreDifferential { get; } = new();
    public List<TimePoint> BlueScoreSeries { get; } = new();
    public List<TimePoint> OrangeScoreSeries { get; } = new();
    /// <summary>Rolling share of possession held by blue, 0..1.</summary>
    public List<TimePoint> PossessionSeries { get; } = new();
    /// <summary>Disc position along the length of the arena, showing territorial swing.</summary>
    public List<TimePoint> DiscFieldPosition { get; } = new();

    /// <summary>The disc's path through the match, in the same segmented form as player tracks.</summary>
    public List<List<Vector3>> DiscTracks { get; } = new();

    public IEnumerable<PlayerAnalysis> AllPlayers => Blue.Players.Concat(Orange.Players);

    public PlayerAnalysis? FindPlayer(string name) =>
        AllPlayers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsArena => MapName == "mpl_arena_a";

    public string ScoreLine => $"{BlueScore} – {OrangeScore}";
}
