// Ported from the Replay Analyser (EchoAnalyser.Core/Analysis/ArenaGeometry.cs). Logic is unchanged; only the
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

namespace Spark.ReplayAnalyser.Analysis;

/// <summary>
/// Echo Arena's playing field in API/game space (metres).
///
/// All of these numbers were measured from recorded matches rather than assumed: the disc
/// stays inside Z ±40, X ±16, Y ±10, and the scoreboard ticks over when the disc reaches
/// Z ≈ ±36 at X ≈ 0, Y ≈ 0 — which is the goal ring.
///
/// Blue scores at +Z and orange scores at -Z, so each team defends the goal that carries
/// its own name.
/// </summary>
public static class ArenaGeometry
{
    /// <summary>Distance from centre to a goal ring along Z, measured from scoring frames.</summary>
    public const float GoalZ = 36f;

    /// <summary>Distance from centre to the end wall — the limit of the play space.</summary>
    public const float HalfLength = 40f;

    /// <summary>Radius of the scoring ring.</summary>
    public const float GoalRadius = 2.5f;

    /// <summary>Half-width (X) and half-height (Y) of the play space.</summary>
    public const float HalfWidth = 16f;
    public const float HalfHeight = 10f;

    /// <summary>The goal blue defends, at negative Z.</summary>
    public static readonly Vector3 BlueGoal = new(0f, 0f, -GoalZ);

    /// <summary>The goal orange defends, at positive Z.</summary>
    public static readonly Vector3 OrangeGoal = new(0f, 0f, GoalZ);

    /// <summary>The goal a team is trying to score in.</summary>
    public static Vector3 AttackingGoal(TeamSide side) =>
        side == TeamSide.Blue ? OrangeGoal : BlueGoal;

    /// <summary>The goal a team is defending.</summary>
    public static Vector3 DefendingGoal(TeamSide side) =>
        side == TeamSide.Blue ? BlueGoal : OrangeGoal;

    /// <summary>+1 if the team attacks toward +Z, -1 otherwise.</summary>
    public static float AttackDirection(TeamSide side) => side == TeamSide.Blue ? 1f : -1f;

    /// <summary>
    /// Where a position sits relative to a team's own goal, as a 0..1 value:
    /// 0 = on top of their own goal, 1 = on top of the goal they are attacking.
    /// Players can drift into the spawn tunnels beyond the walls, so this clamps.
    /// </summary>
    public static float FieldProgress(Vector3 position, TeamSide side)
    {
        float z = position.Z * AttackDirection(side);   // own goal now at -HalfLength
        return Math.Clamp((z + HalfLength) / (2f * HalfLength), 0f, 1f);
    }

    public static ArenaZone ZoneFor(Vector3 position, TeamSide side)
    {
        float p = FieldProgress(position, side);
        if (p < 1f / 3f) return ArenaZone.DefensiveThird;
        if (p > 2f / 3f) return ArenaZone.OffensiveThird;
        return ArenaZone.Neutral;
    }

    public static float DistanceToGoal(Vector3 position, Vector3 goal) =>
        Vector3.Distance(position, goal);

    /// <summary>True when a position is close enough to the goal to count as guarding it.</summary>
    public static bool InGoalieBox(Vector3 position, TeamSide defending) =>
        Vector3.Distance(position, DefendingGoal(defending)) < 12f;

    public static bool IsInOwnHalf(Vector3 position, TeamSide side) =>
        FieldProgress(position, side) < 0.5f;

    /// <summary>Normalised 0..1 X/Z coordinates for heat-mapping, Z first (arena runs along Z).</summary>
    public static (float x, float y) ToTopDownUv(Vector3 p) =>
        ((p.Z + HalfLength) / (2f * HalfLength), (p.X + HalfWidth) / (2f * HalfWidth));

    /// <summary>Normalised 0..1 Z/Y coordinates for the side-on view.</summary>
    public static (float x, float y) ToSideUv(Vector3 p) =>
        ((p.Z + HalfLength) / (2f * HalfLength), (p.Y + HalfHeight) / (2f * HalfHeight));
}

public enum ArenaZone { DefensiveThird, Neutral, OffensiveThird }

public enum TeamSide { Blue = 0, Orange = 1, Spectator = 2 }
