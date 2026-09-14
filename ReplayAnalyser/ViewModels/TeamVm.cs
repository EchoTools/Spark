// Ported from the Replay Analyser (EchoAnalyser.App/ViewModels/TeamVm.cs). Unchanged apart from
// theme tones in place of brushes.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Spark.ReplayAnalyser.Analysis;
using Spark.ReplayAnalyser.Controls;
using Spark.ReplayAnalyser.Model;

namespace Spark.ReplayAnalyser.ViewModels;

/// <summary>One player's slot in the team shape, ready to bind.</summary>
public sealed class RoleVm
{
    public RoleVm(RoleAssignment r, Tone tone)
    {
        Source = r;
        Tone = tone;
    }

    public RoleAssignment Source { get; }
    public Tone Tone { get; }
    public string Name => Source.Player.Name;
    public string RoleName => Source.RoleName;
    public string Evidence => Source.Evidence;

    public string FieldProgressText => $"{Source.FieldProgress:P0}";
    public string GoalieShareText => $"{Source.GoalieShare:P0}";
    public string LastBackText => $"{Source.LastDefenderShare:P0}";
    public string StatLine =>
        $"{Source.Player.GoalPoints} pts · {Source.Player.Goals}G {Source.Player.Assists}A · "
        + $"{Source.Player.Saves} SV · {Source.Player.Stuns} STN";

    /// <summary>Bar width, 0..1, showing how far up the sheet they live.</summary>
    public double Position => Math.Clamp(Source.FieldProgress, 0, 1);
}

/// <summary>Display wrapper for a whole team's profile.</summary>
public sealed class TeamVm
{
    public TeamVm(TeamProfile p)
    {
        Profile = p;
        Tone = p.Side == TeamSide.Blue ? Tone.Blue : Tone.Orange;
        Roles = p.Roles.OrderBy(r => r.FieldProgress).Select(r => new RoleVm(r, Tone)).ToList();
        Tags = p.StyleTags.Select(Capitalise).ToList();
    }

    public TeamProfile Profile { get; }
    public Tone Tone { get; }
    public string Name => Profile.Name;
    public string StackShape => Profile.StackShape;
    public string Summary => Profile.Summary;
    public string ScoreText => Profile.Score.ToString();
    public string RatingText => $"{Profile.Rating:F0}";

    public IReadOnlyList<RoleVm> Roles { get; }
    public IReadOnlyList<string> Tags { get; }

    public string PressText => $"{Profile.PressHeight:P0}";
    public string BackOpenText => $"{Profile.OpenBackShare:P0}";
    public string SpacingText => $"{Profile.Spacing:F1} m";
    public string HoldText => $"{Profile.AverageHold:F1} s";
    public string StunsText => $"{Profile.StunsPerMinute:F1} /min";
    public string AccuracyText => $"{Profile.ShotAccuracy:P0}";
    public string PossessionText => $"{Profile.PossessionShare:P0}";

    private static string Capitalise(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
