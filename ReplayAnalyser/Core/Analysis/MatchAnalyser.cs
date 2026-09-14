// Ported from the Replay Analyser (EchoAnalyser.Core/Analysis/MatchAnalyser.cs). Logic is unchanged; only the
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
using Spark.ReplayAnalyser.Parsing;
using EchoVRAPI;

namespace Spark.ReplayAnalyser.Analysis;

/// <summary>
/// Turns a decoded replay into a <see cref="MatchAnalysis"/>.
///
/// Everything is derived in a single forward pass over the frames. The scoreboard counters
/// in the API are cumulative, so events are recovered by watching those counters step up;
/// positional metrics integrate over frame deltas.
/// </summary>
public static class MatchAnalyser
{
    /// <summary>
    /// How close a hand has to get to a loose disc before we call it a chance the player
    /// could have taken.
    ///
    /// Chosen from the data rather than guessed. Catches happen at a median of 0.28 m, and
    /// sweeping the threshold shows conversion falling from 100% at 0.45 m to 87% at 0.9 m and
    /// 68% at 1.5 m. Tight thresholds record nothing missable; loose ones count discs nobody
    /// could reach. Roughly an arm's length is where a miss starts to mean something.
    /// </summary>
    public static float ReachRange { get; set; } = 0.9f;

    /// <summary>Frames outside live play are ignored for positional and possession metrics.</summary>
    private static bool IsLive(Frame f) =>
        f.game_status is "playing" or "sudden_death";

    /// <summary>
    /// Whether this player physically has the disc in a hand.
    ///
    /// <c>Player.possession</c> is not this: it marks who *owns* the disc, staying true after a
    /// throw until somebody else takes it, so it reads as held for 97% of live play. The hands
    /// tell the truth — a disc is actually gripped about a third of the time — and that is the
    /// signal a carry time or a missed grab has to be built on.
    /// </summary>
    private static bool IsGripping(Player p) =>
        p.holding_left == "disc" || p.holding_right == "disc";

    public static MatchAnalysis Analyse(ReplayReadResult read, CancellationToken ct = default)
    {
        var m = new MatchAnalysis
        {
            FilePath = read.SourcePath ?? "",
            Format = read.Format.ToString(),
            IsPartial = read.IsPartial,
            FrameCount = read.Frames.Count,
            ClientName = read.ClientName ?? "",
            SessionId = read.SessionId ?? "",
        };
        m.Diagnostics.AddRange(read.Diagnostics);

        var frames = read.Frames;
        if (frames.Count == 0)
        {
            m.Diagnostics.Add("No frames could be decoded from this file.");
            return m;
        }

        var meta = frames.FirstOrDefault(f => !string.IsNullOrEmpty(f.map_name)) ?? frames[0];
        m.MapName = meta.map_name ?? "";
        m.MatchType = meta.match_type ?? "";
        m.PrivateMatch = meta.private_match;
        m.StartTime = frames[0].recorded_time;
        m.EndTime = frames[^1].recorded_time;
        double span = (m.EndTime - m.StartTime).TotalSeconds;
        m.SampleRateHz = span > 0 ? frames.Count / span : 0;

        var state = new AnalysisState(m);

        Frame? prev = null;
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            state.Step(f, prev);
            prev = f;
        }
        state.Finish(frames[^1]);
        return m;
    }

    /// <summary>Mutable bookkeeping carried across the frame walk.</summary>
    private sealed class AnalysisState
    {
        private readonly MatchAnalysis _m;
        private readonly Dictionary<long, PlayerAnalysis> _players = new();
        private readonly Dictionary<long, PlayerSnapshot> _last = new();
        private readonly Dictionary<long, PossessionSpan> _openPossession = new();

        private DateTime _t0;
        private bool _started;
        private string _lastScoreKey = "";
        private string _lastThrowKey = "";
        private long _lastHolderId;
        private double _liveSeconds;
        private double _blueHoldSeconds, _orangeHoldSeconds;
        private double _nextSampleAt;
        private readonly TrackBuilder _discTrack = new();
        private readonly Dictionary<long, TrackBuilder> _tracks = new();

        // ---- grab / mistake tracking ----
        /// <summary>
        /// Measured from real catches: the hand is within about 0.3 m when a player actually
        /// takes the disc, and three quarters of catches happen inside 0.45 m. Anything closer
        /// than this was genuinely takeable.
        /// </summary>
        private static float GrabRange => ReachRange;
        /// <summary>Below this the chance is over — the hand has left the disc.</summary>
        private static float GrabReleaseRange => ReachRange * 2f;
        /// <summary>You cannot re-grab your own throw immediately, so ignore this window.</summary>
        private const double SelfReleaseGrace = 0.75;
        /// <summary>How long after a chance we still count a catch as having converted it.</summary>
        private const double GrabResolveWindow = 0.8;

        private sealed class GrabChance
        {
            public TimeSpan Start;
            public float Closest = float.MaxValue;
            public Vector3 Position;
            public Vector3 Disc;
            public double InRangeSeconds;
        }

        private readonly Dictionary<long, GrabChance> _openChance = new();
        private readonly Dictionary<long, TimeSpan> _lastHeldAt = new();
        private readonly Dictionary<long, bool> _wasStunned = new();
        /// <summary>Chances waiting to see whether anyone converted them.</summary>
        private readonly List<(long userId, GrabChance chance)> _pendingChances = new();
        /// <summary>Who physically has the disc right now, 0 when it is loose.</summary>
        private long _grippingId;
        private int _runningBlue, _runningOrange;
        /// <summary>Points each side put on the board across the whole recording.</summary>
        private int _scoredBlue, _scoredOrange;
        private float _prevBoardBlue = -1, _prevBoardOrange = -1;
        private int _resets;
        /// <summary>Defenders each side had in their own third as of the previous frame.</summary>
        private int _lastBlueBack, _lastOrangeBack;
        /// <summary>When each player last registered a shot, so a shot is not read as a giveaway.</summary>
        private readonly Dictionary<long, TimeSpan> _lastShotAt = new();
        /// <summary>The most recently closed possession for each player, so we can label how it ended.</summary>
        private readonly Dictionary<long, PossessionSpan> _lastClosed = new();
        /// <summary>How many throws we managed to attribute to each player.</summary>
        private readonly Dictionary<long, int> _throwCounts = new();
        private const double SampleInterval = 2.0;   // seconds between time-series points

        public AnalysisState(MatchAnalysis m) => _m = m;

        public void Step(Frame f, Frame? prev)
        {
            if (!_started) { _t0 = f.recorded_time; _started = true; }
            TimeSpan t = f.recorded_time - _t0;
            double dt = prev == null ? 0 : (f.recorded_time - prev.recorded_time).TotalSeconds;
            if (dt is < 0 or > 1.0) dt = 0;   // clamp gaps from dropped frames

            bool live = IsLive(f);
            if (live) _liveSeconds += dt;
            if (dt > 0)
            {
                string status = string.IsNullOrEmpty(f.game_status) ? "unknown" : f.game_status;
                _m.StatusSeconds[status] = _m.StatusSeconds.GetValueOrDefault(status) + dt;
            }

            TrackScoreboard(f);
            RecordScore(f, t);
            RecordThrow(f, t);
            AccumulateDiscTrack(f, t, live);

            bool anyHolding = (f.teams ?? new List<Team>())
                .Any(tm => (tm.players ?? new List<Player>()).Any(IsGripping));
            _grippingId = 0;

            var holders = new List<PlayerAnalysis>();
            int blueBack = 0, orangeBack = 0;
            var bluePositions = new List<Vector3>();
            var orangePositions = new List<Vector3>();

            for (int ti = 0; ti < (f.teams?.Count ?? 0); ti++)
            {
                var team = f.teams![ti];
                var side = (TeamSide)ti;
                if (side == TeamSide.Spectator) continue;

                foreach (var p in team.players ?? new List<Player>())
                {
                    var pa = GetPlayer(p, side);
                    var pos = Vec(p.head?.position) ?? Vec(p.body?.position) ?? Vector3.Zero;

                    TrackOf(pa.UserId).Add(pos, t.TotalSeconds, live);

                    if (live)
                    {
                        pa.SecondsPlayed += dt;
                        AccumulatePosition(pa, p, pos, dt, side);
                        if (side == TeamSide.Blue) bluePositions.Add(pos); else orangePositions.Add(pos);
                        if (ArenaGeometry.ZoneFor(pos, side) == ArenaZone.DefensiveThird)
                        {
                            if (side == TeamSide.Blue) blueBack++; else orangeBack++;
                        }
                    }

                    DetectStatEvents(pa, p, t, f, pos, side);
                    DetectGrabErrors(pa, p, t, f, pos, live, anyHolding);
                    DetectCarryIntoStun(pa, p, t, pos);
                    TrackPossession(pa, p, t, pos, side, dt, live);
                    if (p.possession) holders.Add(pa);
                    if (IsGripping(p)) _grippingId = pa.UserId;

                    _last[pa.UserId] = new PlayerSnapshot(p, pos);
                }
            }

            if (live && dt > 0)
            {
                MarkLastDefender(f, TeamSide.Blue, dt);
                MarkLastDefender(f, TeamSide.Orange, dt);
                _lastBlueBack = blueBack;
                _lastOrangeBack = orangeBack;
            }

            if (live)
            {
                if (holders.Count > 0)
                {
                    var h = holders[0];
                    if (h.Side == TeamSide.Blue) _blueHoldSeconds += dt; else _orangeHoldSeconds += dt;
                    _lastHolderId = h.UserId;
                }
                TrackTeamShape(bluePositions, orangePositions, blueBack, orangeBack, dt);
                SampleSeries(f, t);
                ResolveChances(t, _grippingId);

            }
        }

        /// <summary>
        /// The player furthest back on a side owns the goal for that instant. Tracking who
        /// that is, and for how long, is what separates a nominated goalie from a team that
        /// simply rotates whoever happens to be deepest.
        /// </summary>
        private void MarkLastDefender(Frame f, TeamSide side, double dt)
        {
            PlayerAnalysis? deepest = null;
            float best = float.MaxValue;

            int ti = (int)side;
            if (f.teams == null || ti >= f.teams.Count) return;
            foreach (var p in f.teams[ti].players ?? new List<Player>())
            {
                var pos = Vec(p.head?.position) ?? Vec(p.body?.position);
                if (pos == null) continue;
                float progress = ArenaGeometry.FieldProgress(pos.Value, side);
                if (progress >= best) continue;

                long id = p.userid != 0 ? p.userid : HashName(p.name);
                if (!_players.TryGetValue(id, out var pa)) continue;
                best = progress;
                deepest = pa;
            }

            if (deepest != null) deepest.SecondsAsLastDefender += dt;
        }

        private void AccumulatePosition(PlayerAnalysis pa, Player p, Vector3 pos, double dt, TeamSide side)
        {
            if (dt <= 0) return;

            if (_last.TryGetValue(pa.UserId, out var prevSnap))
            {
                double d = Vector3.Distance(pos, prevSnap.Position);
                if (d < 30)   // ignore teleports on respawn
                {
                    pa.DistanceTravelled += d;
                    double speed = d / dt;
                    if (speed > pa.MaxSpeed && speed < 60) pa.MaxSpeed = speed;
                }
            }

            var zone = ArenaGeometry.ZoneFor(pos, side);
            switch (zone)
            {
                case ArenaZone.DefensiveThird: pa.DefensiveThirdShare += dt; break;
                case ArenaZone.Neutral: pa.NeutralShare += dt; break;
                default: pa.OffensiveThirdShare += dt; break;
            }
            pa.AverageFieldProgress += ArenaGeometry.FieldProgress(pos, side) * dt;
            if (ArenaGeometry.InGoalieBox(pos, side)) pa.SecondsInGoalieBox += dt;

            pa.AveragePosition += pos * (float)dt;

            if (p.stunned) pa.SecondsStunned += dt;
            if (p.ping > 0) pa.AveragePing = pa.AveragePing == 0 ? p.ping : (pa.AveragePing * 7 + p.ping) / 8;
        }

        /// <summary>
        /// Spot a hand that reached a loose disc and came away empty.
        ///
        /// A chance opens when a player who is not stunned gets a hand inside grab range of a
        /// disc nobody is holding, and closes when the hand leaves. It only counts as a miss if
        /// nobody took the disc: being beaten to a genuine fifty-fifty is a lost contest, not a
        /// whiff, and the two deserve different names. Their own throw is excluded for a moment
        /// afterwards, since the disc cannot be re-taken straight away.
        /// </summary>
        private void DetectGrabErrors(PlayerAnalysis pa, Player p, TimeSpan t, Frame f,
                                      Vector3 pos, bool live, bool anyHolding)
        {
            var discPos = Vec(f.disc?.position);
            if (IsGripping(p)) _lastHeldAt[pa.UserId] = t;

            if (!live || discPos == null)
            {
                _openChance.Remove(pa.UserId);
                return;
            }

            float reach = float.MaxValue;
            foreach (var hand in new[] { p.lhand, p.rhand })
            {
                var hp = Vec(hand?.position);
                if (hp == null) continue;
                reach = Math.Min(reach, Vector3.Distance(hp.Value, discPos.Value));
            }
            if (reach == float.MaxValue) return;

            bool eligible = !anyHolding && !p.stunned &&
                            (!_lastHeldAt.TryGetValue(pa.UserId, out var held) ||
                             (t - held).TotalSeconds > SelfReleaseGrace);

            if (eligible && reach <= GrabRange)
            {
                if (!_openChance.TryGetValue(pa.UserId, out var chance))
                {
                    chance = new GrabChance { Start = t, Position = pos, Disc = discPos.Value };
                    _openChance[pa.UserId] = chance;
                    pa.GrabChances++;
                }
                chance.Closest = Math.Min(chance.Closest, reach);
                chance.InRangeSeconds += 1.0 / Math.Max(1, _m.SampleRateHz);
            }
            else if (_openChance.TryGetValue(pa.UserId, out var open) &&
                     (reach > GrabReleaseRange || p.stunned || anyHolding))
            {
                _openChance.Remove(pa.UserId);
                // Too brief to have been a real opportunity — a hand passing through.
                if (open.InRangeSeconds >= 0.10)
                    _pendingChances.Add((pa.UserId, open));
            }
        }

        /// <summary>Being stunned out of possession is a carry that should have been released.</summary>
        private void DetectCarryIntoStun(PlayerAnalysis pa, Player p, TimeSpan t, Vector3 pos)
        {
            bool was = _wasStunned.TryGetValue(pa.UserId, out var w) && w;
            if (p.stunned && !was &&
                _lastHeldAt.TryGetValue(pa.UserId, out var held) &&
                (t - held).TotalSeconds <= 0.4)
            {
                Add(new Mistake
                {
                    Kind = MistakeKind.CarriedIntoStun,
                    Time = t, Player = pa.Name, UserId = pa.UserId, Position = pos,
                    Detail = "stunned out of possession instead of moving the disc on",
                }, pa);
            }
            _wasStunned[pa.UserId] = p.stunned;
        }

        /// <summary>
        /// Decide the pending chances now that we know who ended up with the disc. A chance is
        /// only a miss once enough time has passed for a catch to have shown up.
        /// </summary>
        private void ResolveChances(TimeSpan t, long holderId)
        {
            for (int i = _pendingChances.Count - 1; i >= 0; i--)
            {
                var (userId, chance) = _pendingChances[i];
                double age = (t - chance.Start).TotalSeconds;

                // They took it after all.
                if (holderId == userId && age <= GrabResolveWindow + 0.5)
                {
                    _pendingChances.RemoveAt(i);
                    continue;
                }
                if (age < GrabResolveWindow) continue;

                _pendingChances.RemoveAt(i);
                if (!_players.TryGetValue(userId, out var pa)) continue;

                bool takenByOther = holderId != 0 && holderId != userId;
                Add(new Mistake
                {
                    Kind = takenByOther ? MistakeKind.LostContest : MistakeKind.MissedGrab,
                    Time = chance.Start,
                    Player = pa.Name,
                    UserId = userId,
                    Position = chance.Position,
                    DiscPosition = chance.Disc,
                    Distance = chance.Closest,
                    Detail = takenByOther
                        ? $"hand {chance.Closest:F2}m from the disc and the other team took it"
                        : $"hand {chance.Closest:F2}m from a loose disc and did not take it",
                }, pa);
            }
        }

        private void Add(Mistake mistake, PlayerAnalysis pa)
        {
            _m.Mistakes.Add(mistake);
            pa.Mistakes.Add(mistake);
        }

        private void AccumulateDiscTrack(Frame f, TimeSpan t, bool live)
        {
            var d = Vec(f.disc?.position);
            _discTrack.Add(d ?? Vector3.Zero, t.TotalSeconds, live && d != null);
        }

        /// <summary>
        /// Collects a position stream into continuous strokes.
        ///
        /// Points are thinned as they arrive — a player drifting in a straight line does not
        /// need a vertex every frame — and a stroke is cut whenever the position jumps or the
        /// clock stops, so a respawn never draws a line across the whole arena.
        /// </summary>
        private sealed class TrackBuilder
        {
            private const double MinSeconds = 0.06;   // ~16 Hz is plenty for a drawn path
            private const double MinMetres = 0.25;
            private const double BreakSeconds = 0.5;
            private const double BreakMetres = 8;

            private readonly List<List<Vector3>> _segments = new();
            private List<Vector3>? _current;
            private Vector3 _last;
            private double _lastAt;

            public void Add(Vector3 p, double seconds, bool live)
            {
                if (!live) { _current = null; return; }

                if (_current != null &&
                    (seconds - _lastAt > BreakSeconds || Vector3.Distance(p, _last) > BreakMetres))
                    _current = null;

                if (_current == null)
                {
                    _current = new List<Vector3> { p };
                    _segments.Add(_current);
                    _last = p;
                    _lastAt = seconds;
                    return;
                }

                if (seconds - _lastAt < MinSeconds && Vector3.Distance(p, _last) < MinMetres) return;

                _current.Add(p);
                _last = p;
                _lastAt = seconds;
            }

            public void CopyTo(List<List<Vector3>> target)
            {
                foreach (var seg in _segments)
                    if (seg.Count > 1) target.Add(seg);
            }
        }

        private void TrackTeamShape(List<Vector3> blue, List<Vector3> orange, int blueBack, int orangeBack, double dt)
        {
            if (dt <= 0) return;
            _m.Blue.DefendersBackShare[Math.Min(blueBack, 3)] += dt;
            _m.Orange.DefendersBackShare[Math.Min(orangeBack, 3)] += dt;
            _m.Blue.AverageSpread += MeanPairDistance(blue) * dt;
            _m.Orange.AverageSpread += MeanPairDistance(orange) * dt;
        }

        private static double MeanPairDistance(List<Vector3> pts)
        {
            if (pts.Count < 2) return 0;
            double sum = 0; int n = 0;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++) { sum += Vector3.Distance(pts[i], pts[j]); n++; }
            return n == 0 ? 0 : sum / n;
        }

        private void SampleSeries(Frame f, TimeSpan t)
        {
            if (t.TotalSeconds < _nextSampleAt) return;
            _nextSampleAt = t.TotalSeconds + SampleInterval;

            double s = t.TotalSeconds;
            _m.BlueScoreSeries.Add(new TimePoint(s, f.blue_points));
            _m.OrangeScoreSeries.Add(new TimePoint(s, f.orange_points));
            _m.ScoreDifferential.Add(new TimePoint(s, f.blue_points - f.orange_points));

            double total = _blueHoldSeconds + _orangeHoldSeconds;
            _m.PossessionSeries.Add(new TimePoint(s, total <= 0 ? 0.5 : _blueHoldSeconds / total));

            var d = Vec(f.disc?.position);
            if (d != null)
                _m.DiscFieldPosition.Add(new TimePoint(s, d.Value.Z));
        }

        /// <summary>
        /// Follow the raw scoreboard so the headline score survives a recording that spans
        /// more than one round.
        ///
        /// A long capture often contains several games back to back. Reading the final frame
        /// then reports whatever the *current* round happens to sit at — one file here ends
        /// "2-0" after twelve goals — so the totals are accumulated from the board's upward
        /// steps instead, and a step downwards is counted as a round boundary rather than
        /// subtracted.
        /// </summary>
        private void TrackScoreboard(Frame f)
        {
            float blue = f.blue_points, orange = f.orange_points;
            if (_prevBoardBlue < 0)
            {
                _prevBoardBlue = blue;
                _prevBoardOrange = orange;
                // Whatever is already on the board when recording starts was scored earlier.
                _scoredBlue = (int)blue;
                _scoredOrange = (int)orange;
                return;
            }

            if (blue < _prevBoardBlue || orange < _prevBoardOrange) _resets++;
            else
            {
                _scoredBlue += (int)(blue - _prevBoardBlue);
                _scoredOrange += (int)(orange - _prevBoardOrange);
            }
            _prevBoardBlue = blue;
            _prevBoardOrange = orange;
        }

        /// <summary>Goals are announced through <c>last_score</c>; the key detects a fresh one.</summary>
        private void RecordScore(Frame f, TimeSpan t)
        {
            var ls = f.last_score;
            if (ls == null) return;
            string key = $"{ls.team}|{ls.person_scored}|{ls.disc_speed:F3}|{ls.distance_thrown:F3}|{ls.goal_type}|{ls.point_amount}";
            if (key == _lastScoreKey) return;
            bool first = _lastScoreKey.Length == 0;
            _lastScoreKey = key;
            // The very first frame already carries the previous goal; only count it if the
            // scoreboard is non-zero, which means play is genuinely underway.
            if (first && f.blue_points == 0 && f.orange_points == 0) return;

            var side = ls.team?.StartsWith("blue", StringComparison.OrdinalIgnoreCase) == true
                ? TeamSide.Blue : TeamSide.Orange;

            var record = new GoalRecord
            {
                Time = t,
                Side = side,
                Scorer = ls.person_scored ?? "",
                Assist = string.IsNullOrWhiteSpace(ls.assist_scored) || ls.assist_scored == "[INVALID]"
                    ? null : ls.assist_scored,
                Points = ls.point_amount,
                GoalType = (ls.goal_type ?? "").Replace('_', ' '),
                DiscSpeed = ls.disc_speed,
                DistanceThrown = ls.distance_thrown,
                DefendersBack = side == TeamSide.Blue ? _lastOrangeBack : _lastBlueBack,
                BlueScore = side == TeamSide.Blue ? _runningBlue + ls.point_amount : _runningBlue,
                OrangeScore = side == TeamSide.Orange ? _runningOrange + ls.point_amount : _runningOrange,
            };
            if (side == TeamSide.Blue) _runningBlue += ls.point_amount; else _runningOrange += ls.point_amount;
            _m.Goals.Add(record);

            var scorer = _m.FindPlayer(record.Scorer);
            var ev = new MatchEvent
            {
                Kind = MatchEventKind.Goal,
                Time = t,
                GameClock = f.game_clock,
                Player = record.Scorer,
                UserId = scorer?.UserId ?? 0,
                Side = side,
                Position = scorer?.AveragePosition ?? Vector3.Zero,
                DiscPosition = Vec(f.disc?.position) ?? Vector3.Zero,
                Detail = record.GoalType,
                Value = record.DiscSpeed,
                SecondaryPlayer = record.Assist,
            };
            _m.Events.Add(ev);
            scorer?.Events.Add(ev);
        }

        /// <summary>Throw mechanics arrive in <c>last_throw</c>, attributed to the last holder.</summary>
        private void RecordThrow(Frame f, TimeSpan t)
        {
            var lt = f.last_throw;
            if (lt == null || lt.total_speed <= 0) return;
            string key = $"{lt.total_speed:F4}|{lt.arm_speed:F4}|{lt.off_axis_spin_deg:F4}|{lt.rot_per_sec:F4}";
            if (key == _lastThrowKey) return;
            bool first = _lastThrowKey.Length == 0;
            _lastThrowKey = key;
            if (first) return;

            if (_lastHolderId == 0 || !_players.TryGetValue(_lastHolderId, out var pa)) return;
            var pos = _last.TryGetValue(_lastHolderId, out var snap) ? snap.Position : Vector3.Zero;

            var rec = new ThrowRecord
            {
                Player = pa.Name,
                Side = pa.Side,
                Time = t,
                TotalSpeed = lt.total_speed,
                ArmSpeed = lt.arm_speed,
                SpeedFromMovement = lt.speed_from_movement,
                SpeedFromWrist = lt.speed_from_wrist,
                OffAxisSpinDeg = lt.off_axis_spin_deg,
                WristThrowPenalty = lt.wrist_throw_penalty,
                OffAxisPenalty = lt.off_axis_penalty,
                ThrowMovePenalty = lt.throw_move_penalty,
                RotPerSec = lt.rot_per_sec,
                Position = pos,
            };
            pa.Throws.Add(rec);
            _throwCounts[pa.UserId] = _throwCounts.GetValueOrDefault(pa.UserId) + 1;
        }

        /// <summary>The API's per-player counters only ever increase, so a step up is an event.</summary>
        private void DetectStatEvents(PlayerAnalysis pa, Player p, TimeSpan t, Frame f, Vector3 pos, TeamSide side)
        {
            var s = p.stats;
            if (s == null) return;

            if (!_last.TryGetValue(pa.UserId, out var prevSnap))
            {
                Sync(pa, s);
                return;
            }
            var ps = prevSnap.Stats;
            if (ps == null) { Sync(pa, s); return; }

            var disc = Vec(f.disc?.position) ?? Vector3.Zero;

            void Emit(MatchEventKind kind, int count, string? detail = null)
            {
                for (int i = 0; i < count; i++)
                {
                    var ev = new MatchEvent
                    {
                        Kind = kind, Time = t, GameClock = f.game_clock,
                        Player = pa.Name, UserId = pa.UserId, Side = side,
                        Position = pos, DiscPosition = disc, Detail = detail,
                    };
                    _m.Events.Add(ev);
                    pa.Events.Add(ev);
                }
            }

            int d;
            if ((d = s.saves - ps.saves) > 0) Emit(MatchEventKind.Save, d);
            if ((d = s.stuns - ps.stuns) > 0) Emit(MatchEventKind.Stun, d);
            if ((d = s.steals - ps.steals) > 0) Emit(MatchEventKind.Steal, d);
            if ((d = s.interceptions - ps.interceptions) > 0) Emit(MatchEventKind.Interception, d);
            if ((d = s.blocks - ps.blocks) > 0) Emit(MatchEventKind.Block, d);
            if ((d = s.passes - ps.passes) > 0) Emit(MatchEventKind.Pass, d);
            if ((d = s.catches - ps.catches) > 0) Emit(MatchEventKind.Catch, d);
            if ((d = s.assists - ps.assists) > 0) Emit(MatchEventKind.Assist, d);
            if ((d = s.shots_taken - ps.shots_taken) > 0)
            {
                Emit(MatchEventKind.ShotTaken, d);
                pa.ShotPositions.Add(pos);
                _lastShotAt[pa.UserId] = t;
            }
            if ((d = s.goals - ps.goals) > 0) pa.GoalPositions.Add(pos);

            if (p.stunned && !prevSnap.Stunned) pa.TimesStunned++;

            Sync(pa, s);
        }

        private static void Sync(PlayerAnalysis pa, Stats s)
        {
            pa.Points = Math.Max(pa.Points, s.points);
            pa.Goals = Math.Max(pa.Goals, s.goals);
            pa.Assists = Math.Max(pa.Assists, s.assists);
            pa.Saves = Math.Max(pa.Saves, s.saves);
            pa.Stuns = Math.Max(pa.Stuns, s.stuns);
            pa.Passes = Math.Max(pa.Passes, s.passes);
            pa.Catches = Math.Max(pa.Catches, s.catches);
            pa.Steals = Math.Max(pa.Steals, s.steals);
            pa.Blocks = Math.Max(pa.Blocks, s.blocks);
            pa.Interceptions = Math.Max(pa.Interceptions, s.interceptions);
            pa.ShotsTaken = Math.Max(pa.ShotsTaken, s.shots_taken);
            pa.PossessionTime = Math.Max(pa.PossessionTime, s.possession_time);
        }

        private void TrackPossession(PlayerAnalysis pa, Player p, TimeSpan t, Vector3 pos,
                                     TeamSide side, double dt, bool live)
        {
            bool had = _openPossession.ContainsKey(pa.UserId);
            // Carry time is time with the disc in hand. Team possession share, tracked
            // separately, keeps using ownership, which is the usual meaning of the word.
            bool holding = IsGripping(p);

            if (holding && !had)
            {
                var span = new PossessionSpan
                {
                    Player = pa.Name, UserId = pa.UserId, Side = side,
                    Start = t, End = t, StartPosition = pos, EndPosition = pos,
                };
                _openPossession[pa.UserId] = span;
                pa.PossessionCount++;

                // A change of holder across teams while the disc was live is a turnover for
                // whoever had it last -- unless they had just shot, in which case the opponent
                // collecting the rebound is the normal course of play, not a giveaway.
                if (_lastHolderId != 0 && _lastHolderId != pa.UserId &&
                    _players.TryGetValue(_lastHolderId, out var previousHolder) &&
                    previousHolder.Side != side && live)
                {
                    bool justShot = _lastShotAt.TryGetValue(previousHolder.UserId, out var shotAt)
                                    && (t - shotAt).TotalSeconds <= ShotGraceSeconds;
                    LabelPriorSpan(previousHolder.UserId, justShot ? "shot" : "turnover");

                    if (!justShot)
                    {
                        previousHolder.Turnovers++;
                        if (ArenaGeometry.ZoneFor(pos, previousHolder.Side) == ArenaZone.DefensiveThird)
                            Add(new Mistake
                            {
                                Kind = MistakeKind.TurnoverAtHome,
                                Time = t, Player = previousHolder.Name, UserId = previousHolder.UserId,
                                Position = pos,
                                Detail = $"lost the disc to {pa.Name} inside your own third",
                            }, previousHolder);
                        var ev = new MatchEvent
                        {
                            Kind = MatchEventKind.Turnover, Time = t,
                            Player = previousHolder.Name, UserId = previousHolder.UserId,
                            Side = previousHolder.Side, Position = pos,
                            SecondaryPlayer = pa.Name,
                        };
                        _m.Events.Add(ev);
                        previousHolder.Events.Add(ev);
                    }
                }
                else if (_lastHolderId != 0 && _lastHolderId != pa.UserId &&
                         _players.TryGetValue(_lastHolderId, out var mate) && mate.Side == side)
                {
                    // Same-team handover: credit the passer.
                    mate.PassTargets[pa.Name] = mate.PassTargets.GetValueOrDefault(pa.Name) + 1;
                    LabelPriorSpan(mate.UserId, "pass");
                }
            }
            else if (!holding && had)
            {
                var span = _openPossession[pa.UserId];
                span.End = t;
                span.EndPosition = pos;
                _openPossession.Remove(pa.UserId);
                if (span.Seconds > 0.05)
                {
                    pa.Possessions.Add(span);
                    _m.Possessions.Add(span);
                    _lastClosed[pa.UserId] = span;
                }
            }
        }

        /// <summary>A shot taken this recently means the loss of the disc was the shot, not a giveaway.</summary>
        private const double ShotGraceSeconds = 2.0;

        /// <summary>
        /// Label how a player's last possession ended. The span is usually already closed by
        /// the time the next player picks the disc up, so check the closed one first.
        /// </summary>
        private void LabelPriorSpan(long userId, string outcome)
        {
            if (_openPossession.TryGetValue(userId, out var open)) open.Outcome = outcome;
            else if (_lastClosed.TryGetValue(userId, out var closed)) closed.Outcome = outcome;
        }

        private TrackBuilder TrackOf(long userId)
        {
            if (!_tracks.TryGetValue(userId, out var b)) _tracks[userId] = b = new TrackBuilder();
            return b;
        }

        private PlayerAnalysis GetPlayer(Player p, TeamSide side)
        {
            long id = p.userid != 0 ? p.userid : HashName(p.name);
            if (!_players.TryGetValue(id, out var pa))
            {
                pa = new PlayerAnalysis
                {
                    Name = p.name ?? "unknown",
                    UserId = id,
                    Side = side,
                    Level = p.level,
                    Number = p.number,
                };
                _players[id] = pa;
                (side == TeamSide.Blue ? _m.Blue : _m.Orange).Players.Add(pa);
            }
            // A player can switch teams mid-session; keep the most recent side.
            if (pa.Side != side)
            {
                var from = pa.Side == TeamSide.Blue ? _m.Blue : _m.Orange;
                var to = side == TeamSide.Blue ? _m.Blue : _m.Orange;
                from.Players.Remove(pa);
                if (!to.Players.Contains(pa)) to.Players.Add(pa);
                pa.Side = side;
            }
            return pa;
        }

        private static long HashName(string? name) =>
            string.IsNullOrEmpty(name) ? 0 : -Math.Abs((long)name.GetHashCode());

        public void Finish(Frame last)
        {
            // Totals across the whole recording, so a multi-round capture still adds up.
            _m.BlueScore = _scoredBlue;
            _m.OrangeScore = _scoredOrange;
            _m.RoundsRecorded = _resets + 1;
            _m.Blue.Score = _m.BlueScore;
            _m.Orange.Score = _m.OrangeScore;

            foreach (var span in _openPossession.Values)
            {
                span.Outcome = "end of match";
                if (span.Seconds > 0.05)
                {
                    _m.Possessions.Add(span);
                    var owner = _m.AllPlayers.FirstOrDefault(p => p.UserId == span.UserId);
                    owner?.Possessions.Add(span);
                }
            }

            _m.LiveSeconds = _liveSeconds;

            // Echo VR only records last_throw for the client that captured the replay, so the
            // player we attributed the most throws to is that client. Only they get real throw
            // mechanics; for everyone else the data is absent rather than poor.
            if (_throwCounts.Count > 0)
            {
                var best = _throwCounts.OrderByDescending(kv => kv.Value).First();
                if (_players.TryGetValue(best.Key, out var recorder))
                {
                    _m.RecordingPlayer = recorder.Name;
                    recorder.HasThrowTelemetry = true;
                }
            }

            CreditGoalsFromScoreboard();

            _discTrack.CopyTo(_m.DiscTracks);
            foreach (var p in _m.AllPlayers)
                if (_tracks.TryGetValue(p.UserId, out var b)) b.CopyTo(p.Tracks);

            double totalHold = _blueHoldSeconds + _orangeHoldSeconds;
            _m.Blue.PossessionSeconds = (float)_blueHoldSeconds;
            _m.Orange.PossessionSeconds = (float)_orangeHoldSeconds;
            _m.Blue.PossessionShare = totalHold <= 0 ? 0 : _blueHoldSeconds / totalHold;
            _m.Orange.PossessionShare = totalHold <= 0 ? 0 : _orangeHoldSeconds / totalHold;

            foreach (var team in new[] { _m.Blue, _m.Orange })
            {
                if (_liveSeconds > 0)
                {
                    team.AverageSpread /= _liveSeconds;
                    for (int i = 0; i < team.DefendersBackShare.Length; i++)
                        team.DefendersBackShare[i] /= _liveSeconds;
                }
                foreach (var p in team.Players) FinishPlayer(p, totalHold);
            }
        }

        /// <summary>
        /// Private matches leave every player's <c>goals</c> and <c>passes</c> counters at
        /// zero, even though the scoreboard moves. The game still announces who scored
        /// through <c>last_score</c>, so count those and take whichever source says more.
        /// </summary>
        private void CreditGoalsFromScoreboard()
        {
            var goals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var assists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var points = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in _m.Goals)
            {
                if (!string.IsNullOrWhiteSpace(g.Scorer))
                {
                    goals[g.Scorer] = goals.GetValueOrDefault(g.Scorer) + 1;
                    points[g.Scorer] = points.GetValueOrDefault(g.Scorer) + g.Points;
                }
                if (!string.IsNullOrWhiteSpace(g.Assist))
                    assists[g.Assist!] = assists.GetValueOrDefault(g.Assist!) + 1;
            }

            foreach (var p in _m.AllPlayers)
            {
                if (goals.TryGetValue(p.Name, out int g)) p.Goals = Math.Max(p.Goals, g);
                if (assists.TryGetValue(p.Name, out int a)) p.Assists = Math.Max(p.Assists, a);
                p.GoalPoints = points.GetValueOrDefault(p.Name);
            }
        }

        private static void FinishPlayer(PlayerAnalysis p, double totalHold)
        {
            double t = p.SecondsPlayed;
            if (t > 0)
            {
                p.AverageSpeed = p.DistanceTravelled / t;
                p.AveragePosition /= (float)t;
                p.AverageFieldProgress /= t;
                double zoneTotal = p.DefensiveThirdShare + p.NeutralShare + p.OffensiveThirdShare;
                if (zoneTotal > 0)
                {
                    p.DefensiveThirdShare /= zoneTotal;
                    p.NeutralShare /= zoneTotal;
                    p.OffensiveThirdShare /= zoneTotal;
                }
            }
            p.PossessionShare = totalHold <= 0 ? 0 : p.PossessionTime / totalHold;
        }

        private static Vector3? Vec(List<float>? l) =>
            l is { Count: >= 3 } ? new Vector3(l[0], l[1], l[2]) : null;

        private readonly record struct PlayerSnapshot(Player P, Vector3 Position)
        {
            public Stats? Stats => P.stats;
            public bool Stunned => P.stunned;
        }
    }
}
