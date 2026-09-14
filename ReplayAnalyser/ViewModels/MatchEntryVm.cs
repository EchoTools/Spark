// Ported from the Replay Analyser (EchoAnalyser.App/ViewModels/MatchEntryVm.cs). Unchanged, plus
// the separate score and error fields the Spark library rows colour individually.
#nullable enable

using System;
using System.IO;
using System.Linq;
using Spark.ReplayAnalyser.Model;

namespace Spark.ReplayAnalyser.ViewModels;

/// <summary>
/// A replay file in the library list. Starts as bare file metadata and gains a scoreline,
/// duration and roster once the folder has been indexed.
/// </summary>
public sealed class MatchEntryVm : ObservableObject
{
    private MatchSummary? _summary;

    public MatchEntryVm(FileInfo file, MatchSummary? summary)
    {
        File = file;
        _summary = summary;
    }

    public FileInfo File { get; }
    public string FullPath => File.FullName;
    public string FileName => File.Name;
    public string Extension => File.Extension.TrimStart('.').ToLowerInvariant();
    public double SizeMb => File.Length / 1048576.0;

    public MatchSummary? Summary
    {
        get => _summary;
        set
        {
            _summary = value;
            Raise(nameof(Summary));
            Raise(nameof(IsIndexed));
            Raise(nameof(ScoreLine));
            Raise(nameof(HasScore));
            Raise(nameof(BlueScoreText));
            Raise(nameof(OrangeScoreText));
            Raise(nameof(SubTitle));
            Raise(nameof(Recorded));
            Raise(nameof(RosterLine));
            Raise(nameof(HasError));
            Raise(nameof(DurationText));
        }
    }

    public bool IsIndexed => _summary != null;
    public bool HasError => _summary is { Ok: false };

    /// <summary>
    /// Spark names its files rec_YYYY-MM-DD_HH-mm-ss, so the timestamp is readable before
    /// the file has ever been opened.
    /// </summary>
    public DateTime Recorded
    {
        get
        {
            if (_summary is { Recorded.Year: > 2000 }) return _summary.Recorded;
            var name = Path.GetFileNameWithoutExtension(File.Name);
            int i = name.IndexOf("20", StringComparison.Ordinal);
            if (i >= 0 && DateTime.TryParseExact(
                    name.Substring(i, Math.Min(19, name.Length - i)),
                    "yyyy-MM-dd_HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out var d))
                return d;
            return File.LastWriteTime;
        }
    }

    public string ScoreLine => _summary is { Ok: true } s ? $"{s.BlueScore} – {s.OrangeScore}" : "–";
    public bool HasScore => _summary is { Ok: true };
    public string BlueScoreText => _summary is { Ok: true } s ? s.BlueScore.ToString() : "";
    public string OrangeScoreText => _summary is { Ok: true } s ? s.OrangeScore.ToString() : "";

    public string DurationText => _summary is { Ok: true } s
        ? TimeSpan.FromSeconds(s.DurationSeconds).ToString(@"m\:ss")
        : $"{SizeMb:F0} MB";

    public string SubTitle
    {
        get
        {
            if (_summary == null) return $".{Extension} — not indexed yet";
            if (!_summary.Ok) return _summary.Error ?? "could not be read";
            string map = _summary.MapName switch
            {
                "mpl_arena_a" => "Arena",
                "mpl_combat_dyson" => "Combat · Dyson",
                "mpl_combat_combustion" => "Combat · Combustion",
                "mpl_combat_fission" => "Combat · Fission",
                "mpl_combat_gauss" => "Combat · Surge",
                "mpl_lobby_b2" => "Lobby",
                _ => string.IsNullOrEmpty(_summary.MapName) ? "unknown map" : _summary.MapName,
            };
            string kind = _summary.PrivateMatch ? "private" : "public";
            return $"{map} · {kind} · {TimeSpan.FromSeconds(_summary.LiveSeconds):m\\:ss} live";
        }
    }

    public string RosterLine => _summary is { Ok: true } s && s.Players.Count > 0
        ? string.Join(", ", s.Players.OrderByDescending(p => p.SecondsPlayed).Take(8).Select(p => p.Name))
        : "";

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (_summary == null) return false;
        if (_summary.MapName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return _summary.Players.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
