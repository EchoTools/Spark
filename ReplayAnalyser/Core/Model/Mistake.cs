// Ported from the Replay Analyser (EchoAnalyser.Core/Model/Mistake.cs). Logic is unchanged; only the
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

namespace Spark.ReplayAnalyser.Model;

public enum MistakeKind
{
    /// <summary>Hand was on a free disc and came away without it, with nobody else taking it.</summary>
    MissedGrab,
    /// <summary>In range of a free disc and an opponent took it instead.</summary>
    LostContest,
    /// <summary>Carried the disc into contact and was stunned out of possession.</summary>
    CarriedIntoStun,
    /// <summary>Gave the disc to the other team inside your own third.</summary>
    TurnoverAtHome,
}

/// <summary>One identifiable error, with the moment and the place it happened.</summary>
public sealed class Mistake
{
    public MistakeKind Kind { get; init; }
    public TimeSpan Time { get; init; }
    public string Player { get; init; } = "";
    public long UserId { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 DiscPosition { get; init; }
    /// <summary>How close the hand got, for grab errors.</summary>
    public float Distance { get; init; }
    public string Detail { get; init; } = "";

    public string Label => Kind switch
    {
        MistakeKind.MissedGrab => "Missed grab",
        MistakeKind.LostContest => "Lost the contest",
        MistakeKind.CarriedIntoStun => "Carried into contact",
        MistakeKind.TurnoverAtHome => "Turnover at home",
        _ => Kind.ToString(),
    };
}
