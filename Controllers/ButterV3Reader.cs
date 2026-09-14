// Ported verbatim from the Replay Analyser (EchoAnalyser.Core/Parsing/ButterV3Reader.cs) so
// Spark can repair the same butter files that tool can already read. Kept byte-for-byte apart
// from this header and the result type below, so fixes can be diffed against the original.
//
// Nullable is enabled for this file alone: the source is annotated, and Spark does not turn
// nullable on project-wide.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using EchoVRAPI;

namespace Spark;

/// <summary>
/// A fault-tolerant reader for the .butter v3 replay container.
///
/// The stock ButterReplays decoder is all-or-nothing: it decodes the whole file in one
/// pass and throws away every frame if any single frame is malformed. Roughly half of a
/// real capture library trips it, most often on <c>IndexOutOfRangeException</c> from the
/// fixed-size player table in the header — a player who joins after recording started has
/// an index past the end of that table.
///
/// This reader keeps the same wire format but recovers instead of aborting:
///   * chunks are decoded independently (each opens with a keyframe, so they are
///     self-contained) and a bad chunk costs only that chunk;
///   * the player table grows on demand, so late joiners decode as real players;
///   * a truncated tail — common when Spark is killed mid-match — keeps everything
///     before the truncation.
/// </summary>
public static class ButterV3Reader
{
    private const ushort KeyframeMarker = 0xFEFC;
    private const ushort DeltaMarker = 0xFEFE;

    public static ReplayReadResult Read(byte[] bytes, string? sourcePath = null)
    {
        var result = new ReplayReadResult { SourcePath = sourcePath, Format = ReplayFormat.Butter };
        if (bytes.Length < 32)
        {
            result.Diagnostics.Add("File too small to contain a butter header.");
            return result;
        }

        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);

        byte formatVersion = br.ReadByte();
        result.FormatVersion = formatVersion;
        if (formatVersion != 3)
        {
            result.Diagnostics.Add($"Unsupported butter format version {formatVersion} (only v3 is supported).");
            return result;
        }

        var header = new ButterHeaderV3();
        uint[] chunkSizes;
        try
        {
            header.KeyframeInterval = br.ReadUInt16();
            header.Compression = (ButterCompression)br.ReadByte();
            header.ClientName = ReadCString(br);
            header.SessionId = ReadSessionId(br);
            header.SessionIp = ReadIpAddress(br);

            int playerCount = br.ReadByte();
            for (int i = 0; i < playerCount; i++) header.Names.Add(ReadCString(br));
            for (int i = 0; i < playerCount; i++) header.UserIds.Add(br.ReadInt64());
            for (int i = 0; i < playerCount; i++) header.Numbers.Add(br.ReadByte());
            for (int i = 0; i < playerCount; i++) header.Levels.Add(br.ReadByte());

            header.TotalRoundCount = br.ReadByte();
            byte roundScores = br.ReadByte();
            header.BlueRoundScore = roundScores & 0x0F;
            header.OrangeRoundScore = (roundScores >> 4) & 0x0F;

            byte mapByte = br.ReadByte();
            header.PrivateMatch = (mapByte & 1) == 1;
            header.MapName = MapNameFromIndex(mapByte >> 1);
            header.MatchType = MatchTypeFor(header.MapName, header.PrivateMatch);

            uint chunkCount = br.ReadUInt32();
            if (chunkCount > 2_000_000)
            {
                result.Diagnostics.Add($"Implausible chunk count {chunkCount}; refusing to read.");
                return result;
            }
            chunkSizes = new uint[chunkCount];
            for (int i = 0; i < chunkCount; i++) chunkSizes[i] = br.ReadUInt32();
        }
        catch (Exception ex)
        {
            result.Diagnostics.Add($"Header unreadable: {ex.GetType().Name}: {ex.Message}");
            return result;
        }

        result.ClientName = header.ClientName;
        result.SessionId = header.SessionId;

        var frames = new List<Frame>();
        Frame? previous = null;   // last frame decoded, for delta reconstruction

        for (int c = 0; c < chunkSizes.Length; c++)
        {
            uint size = chunkSizes[c];
            if (size == 0) continue;
            if (ms.Position + size > ms.Length)
            {
                result.TruncatedTail = true;
                result.Diagnostics.Add(
                    $"Chunk {c}/{chunkSizes.Length} claims {size:N0} bytes but only {ms.Length - ms.Position:N0} remain — file truncated (recording likely interrupted).");
                break;
            }

            byte[] raw = br.ReadBytes((int)size);
            byte[] payload;
            try
            {
                payload = Decompress(raw, header.Compression);
            }
            catch (Exception ex)
            {
                result.FailedChunks++;
                result.Diagnostics.Add($"Chunk {c}: decompression failed ({ex.GetType().Name}).");
                previous = null; // next keyframe re-syncs
                continue;
            }

            // Each chunk opens with a keyframe, so a failure here is contained: we keep the
            // frames decoded so far in this chunk and re-sync on the next chunk's keyframe.
            int beforeCount = frames.Count;
            try
            {
                DecodeChunk(payload, header, frames, ref previous);
            }
            catch (Exception ex)
            {
                result.FailedChunks++;
                result.Diagnostics.Add(
                    $"Chunk {c}: stopped after {frames.Count - beforeCount} frame(s) — {ex.GetType().Name}: {ex.Message}");
                previous = null;
            }
        }

        result.Frames = frames;
        result.RecoveredPlayerSlots = header.SynthesizedSlots;
        if (header.SynthesizedSlots > 0)
            result.Diagnostics.Add(
                $"{header.SynthesizedSlots} player slot(s) were referenced but missing from the header (mid-match joins); they were recovered as synthetic entries.");
        return result;
    }

    private static void DecodeChunk(byte[] payload, ButterHeaderV3 header, List<Frame> frames, ref Frame? previous)
    {
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms);

        while (ms.Position < ms.Length - 1)
        {
            ushort marker = br.ReadUInt16();
            if (marker != KeyframeMarker && marker != DeltaMarker)
                throw new InvalidDataException($"Lost frame sync (marker 0x{marker:X4}).");

            bool isKeyframe = marker == KeyframeMarker;
            if (isKeyframe) previous = null;
            if (!isKeyframe && previous == null)
                throw new InvalidDataException("Delta frame with no preceding keyframe.");

            Frame f = DecodeFrame(br, header, isKeyframe, previous);
            frames.Add(f);
            previous = f;
        }
    }

    private static Frame DecodeFrame(BinaryReader br, ButterHeaderV3 h, bool isKeyframe, Frame? prev)
    {
        var f = new Frame
        {
            recorded_time = isKeyframe
                ? DateTimeOffset.FromUnixTimeMilliseconds(br.ReadInt64()).LocalDateTime
                : prev!.recorded_time.AddMilliseconds(br.ReadUInt16()),
            client_name = h.ClientName,
            sessionid = h.SessionId,
            sessionip = h.SessionIp,
            total_round_count = h.TotalRoundCount,
            blue_round_score = h.BlueRoundScore,
            orange_round_score = h.OrangeRoundScore,
            private_match = h.PrivateMatch,
            map_name = h.MapName,
            match_type = h.MatchType,
        };
        f.game_clock = br.ReadSingle() + (isKeyframe ? 0f : prev!.game_clock);
        f.game_clock_display = FormatClock(f.game_clock);

        var mask = Bits(br.ReadByte());

        // ---- game state -------------------------------------------------------
        if (mask[0])
        {
            f.game_status = GameStatusFromByte(br.ReadByte());
            // Arena scores are whole points in a byte; Combat scores are fractional and use a
            // half. On any other map (lobby, tutorial) no score is written at all.
            if (IsArena(f.map_name))
            {
                f.blue_points = br.ReadByte();
                f.orange_points = br.ReadByte();
            }
            else if (IsCombat(f.map_name))
            {
                f.blue_points = (float)ReadHalf(br);
                f.orange_points = (float)ReadHalf(br);
            }
            var sh = Bits(br.ReadByte());
            f.left_shoulder_pressed = sh[0];
            f.right_shoulder_pressed = sh[1];
            f.left_shoulder_pressed2 = sh[2];
            f.right_shoulder_pressed2 = sh[3];
        }
        else
        {
            f.game_status = prev?.game_status;
            f.blue_points = prev?.blue_points ?? 0f;
            f.orange_points = prev?.orange_points ?? 0f;
            f.left_shoulder_pressed = prev?.left_shoulder_pressed ?? false;
            f.right_shoulder_pressed = prev?.right_shoulder_pressed ?? false;
            f.left_shoulder_pressed2 = prev?.left_shoulder_pressed2 ?? false;
            f.right_shoulder_pressed2 = prev?.right_shoulder_pressed2 ?? false;
        }

        // ---- pause / restart requests ----------------------------------------
        if (mask[1])
        {
            byte b = br.ReadByte();
            f.blue_team_restart_request = (b & 1) > 0;
            f.orange_team_restart_request = (b & 2) > 0;
            f.pause = new Pause
            {
                paused_requested_team = TeamFromIndex((byte)((b & 0x0C) >> 2)),
                unpaused_team = TeamFromIndex((byte)((b & 0x30) >> 4)),
                paused_state = PausedStateFromByte(br.ReadByte()),
                paused_timer = (float)ReadHalf(br),
                unpaused_timer = (float)ReadHalf(br),
            };
        }
        else f.pause = prev?.pause;

        // ---- last score -------------------------------------------------------
        if (mask[2])
        {
            byte b = br.ReadByte();
            f.last_score = new LastScore
            {
                team = TeamFromIndex((byte)(b & 3)),
                point_amount = (b & 4) > 0 ? 3 : 2,
                goal_type = GoalTypeFromIndex((b & 0xF8) >> 3),
                person_scored = h.NameAt(br.ReadByte()),
                assist_scored = h.NameAt(br.ReadByte()),
                disc_speed = (float)ReadHalf(br),
                distance_thrown = (float)ReadHalf(br),
            };
        }
        else f.last_score = prev?.last_score;

        // ---- last throw -------------------------------------------------------
        if (mask[3])
        {
            f.last_throw = new LastThrow
            {
                arm_speed = (float)ReadHalf(br),
                total_speed = (float)ReadHalf(br),
                off_axis_spin_deg = (float)ReadHalf(br),
                wrist_throw_penalty = (float)ReadHalf(br),
                rot_per_sec = (float)ReadHalf(br),
                pot_speed_from_rot = (float)ReadHalf(br),
                speed_from_arm = (float)ReadHalf(br),
                speed_from_movement = (float)ReadHalf(br),
                speed_from_wrist = (float)ReadHalf(br),
                wrist_align_to_throw_deg = (float)ReadHalf(br),
                throw_align_to_movement_deg = (float)ReadHalf(br),
                off_axis_penalty = (float)ReadHalf(br),
                throw_move_penalty = (float)ReadHalf(br),
            };
        }
        else f.last_throw = prev?.last_throw;

        // ---- local VR player --------------------------------------------------
        if (mask[4])
        {
            var (pos, rot) = ReadPose(br);
            pos += prev?.player is { } pp ? ToVec(pp.vr_position) : Vector3.Zero;
            f.player = new VRPlayer
            {
                vr_position = ToList(pos),
                vr_forward = ToList(Forward(rot)),
                vr_left = ToList(Left(rot)),
                vr_up = ToList(Up(rot)),
            };
        }
        else f.player = prev?.player;

        // ---- disc -------------------------------------------------------------
        if (mask[5])
        {
            var (pos, rot) = ReadPose(br);
            pos += prev?.disc is { } pd ? ToVec(pd.position) : Vector3.Zero;
            f.disc = new Disc
            {
                position = ToList(pos),
                forward = ToList(Forward(rot)),
                left = ToList(Left(rot)),
                up = ToList(Up(rot)),
                velocity = new List<float>
                {
                    (float)ReadHalf(br) + (prev?.disc?.velocity is { Count: > 0 } v0 ? v0[0] : 0f),
                    (float)ReadHalf(br) + (prev?.disc?.velocity is { Count: > 1 } v1 ? v1[1] : 0f),
                    (float)ReadHalf(br) + (prev?.disc?.velocity is { Count: > 2 } v2 ? v2[2] : 0f),
                },
            };
        }
        else f.disc = prev?.disc;

        // ---- combat payload ---------------------------------------------------
        if (mask[6])
        {
            f.contested = br.ReadBoolean();
            f.payload_checkpoint = br.ReadByte();
            f.payload_defenders = br.ReadByte();
            f.payload_distance = (float)ReadHalf(br);
            f.payload_speed = (float)ReadHalf(br);
        }
        else
        {
            f.contested = prev?.contested ?? false;
            f.payload_checkpoint = prev?.payload_checkpoint ?? 0;
            f.payload_defenders = prev?.payload_defenders ?? 0;
            f.payload_distance = prev?.payload_distance ?? 0f;
            f.payload_speed = prev?.payload_speed ?? 0f;
        }

        // ---- rule changes -----------------------------------------------------
        if (mask[7])
        {
            f.rules_changed_at = br.ReadInt64();
            f.rules_changed_by = ReadCString(br);
        }
        else
        {
            f.rules_changed_at = prev?.rules_changed_at ?? 0;
            f.rules_changed_by = prev?.rules_changed_by;
        }

        // ---- teams ------------------------------------------------------------
        var teamMask = Bits(br.ReadByte());
        f.teams = new List<Team> { new(), new(), new() };
        f.teams[0].possession = teamMask[0];
        f.teams[1].possession = teamMask[1];

        for (int t = 0; t < 3; t++)
        {
            f.teams[t].stats = teamMask[t + 2]
                ? ReadStats(br)
                : prev?.teams?[t].stats ?? new Stats();

            int count = br.ReadByte();
            var players = new List<Player>(count);
            for (int i = 0; i < count; i++)
                players.Add(DecodePlayer(br, h, f, prev));
            f.teams[t].players = players;
            f.teams[t].team = t switch { 0 => "BLUE TEAM", 1 => "ORANGE TEAM", _ => "SPECTATORS" };
        }

        // Trailing skeleton block. Even when the poses are not needed, the bytes have to be
        // consumed exactly or every subsequent frame in the chunk loses sync.
        byte boneHeader = br.ReadByte();
        bool hasBones = (boneHeader & 1) == 1;
        int bonePlayerCount = boneHeader >> 1;
        if (hasBones) ReadBones(br, bonePlayerCount, prev, f);

        return f;
    }

    private static Player DecodePlayer(BinaryReader br, ButterHeaderV3 h, Frame f, Frame? prev)
    {
        byte idx = br.ReadByte();
        var p = new Player
        {
            name = h.NameAt(idx),
            playerid = br.ReadByte(),
            level = h.LevelAt(idx),
            number = h.NumberAt(idx),
            userid = h.UserIdAt(idx),
        };

        Player? old = prev?.GetPlayer(p.userid);

        var m = Bits(br.ReadByte());
        p.possession = m[0];
        p.blocking = m[1];
        p.stunned = m[2];
        p.invulnerable = m[3];
        p.is_emote_playing = m[4];

        if (m[5])
        {
            p.stats = ReadStats(br);
            if (old?.stats != null) p.stats += old.stats;
        }
        else p.stats = old?.stats ?? new Stats();

        bool inArena = IsArena(f.map_name);
        if (m[6])
        {
            p.ping = br.ReadInt16() + (old?.ping ?? 0);
            p.packetlossratio = (float)ReadHalf(br) + (old?.packetlossratio ?? 0f);
            if (inArena)
            {
                p.holding_left = HoldingFromByte(br.ReadByte());
                p.holding_right = HoldingFromByte(br.ReadByte());
            }
        }
        else
        {
            p.ping = old?.ping ?? 0;
            p.packetlossratio = old?.packetlossratio ?? 0f;
            if (inArena)
            {
                p.holding_left = old?.holding_left ?? "none";
                p.holding_right = old?.holding_right ?? "none";
            }
        }

        p.velocity = m[7]
            ? ToList(ReadVector3Half(br) + (old?.velocity is { Count: 3 } ov ? ToVec(ov) : Vector3.Zero))
            : old?.velocity ?? new List<float> { 0, 0, 0 };

        var pm = Bits(br.ReadByte());
        p.head = new Transform();
        p.body = new Transform();
        p.lhand = new Transform();
        p.rhand = new Transform();

        p.head.position = pm[0]
            ? ToList(ReadVector3Half(br) + (old?.head?.Position ?? Vector3.Zero))
            : old?.head?.position ?? new List<float> { 0, 0, 0 };
        p.head.Rotation = pm[1] ? ReadSmallestThree(br) : old?.head?.Rotation ?? Quaternion.Identity;

        p.body.position = pm[2]
            ? ToList(ReadVector3Half(br) + (old?.body?.Position ?? Vector3.Zero))
            : old?.body?.position ?? new List<float> { 0, 0, 0 };
        p.body.Rotation = pm[3] ? ReadSmallestThree(br) : old?.body?.Rotation ?? Quaternion.Identity;

        p.lhand.position = pm[4]
            ? ToList(ReadVector3Half(br) + (old?.lhand?.Position ?? Vector3.Zero))
            : old?.lhand?.position ?? new List<float> { 0, 0, 0 };
        p.lhand.Rotation = pm[5] ? ReadSmallestThree(br) : old?.lhand?.Rotation ?? Quaternion.Identity;

        p.rhand.position = pm[6]
            ? ToList(ReadVector3Half(br) + (old?.rhand?.Position ?? Vector3.Zero))
            : old?.rhand?.position ?? new List<float> { 0, 0, 0 };
        p.rhand.Rotation = pm[7] ? ReadSmallestThree(br) : old?.rhand?.Rotation ?? Quaternion.Identity;

        if (IsCombat(f.map_name))
        {
            byte loadout = br.ReadByte();
            p.Weapon = WeaponNames[loadout & 3];
            p.Ordnance = OrdnanceNames[(loadout & 0x0C) >> 2];
            p.TacMod = TacModNames[(loadout & 0x30) >> 4];
            p.Arm = ((loadout & 0x40) >> 6) == 0 ? "Left" : "Right";
        }

        return p;
    }

    private const int BoneCount = 23;

    /// <summary>
    /// Per bone-player: a 3-byte position mask and a 3-byte rotation mask, then only the
    /// bones whose bit is set, stored as deltas against the previous frame.
    /// </summary>
    private static void ReadBones(BinaryReader br, int count, Frame? prev, Frame f)
    {
        var previousBones = prev?.bones?.user_bones;
        var bones = new Bones { user_bones = new BonePlayer[count] };

        for (int p = 0; p < count; p++)
        {
            var bp = new BonePlayer { bone_o = new float[92], bone_t = new float[69] };
            bones.user_bones[p] = bp;

            var posMask = Bits(br.ReadBytes(3));
            var rotMask = Bits(br.ReadBytes(3));
            BonePlayer? old = previousBones != null && p < previousBones.Length ? previousBones[p] : null;

            for (int b = 0; b < BoneCount; b++)
            {
                Vector3 basePos = old?.GetPosition(b) ?? Vector3.Zero;
                bp.SetPosition(b, posMask[b]
                    ? basePos + ReadVector3FixedPrecision(br, -2f, 2f, 14)
                    : basePos);
            }
            for (int b = 0; b < BoneCount; b++)
            {
                Quaternion baseRot = old?.GetRotation(b) ?? Quaternion.Identity;
                bp.SetRotation(b, rotMask[b] ? ReadSmallestThree(br) * baseRot : baseRot);
            }
        }
        f.bones = bones;
    }

    private static List<bool> Bits(byte[] bytes)
    {
        var list = new List<bool>(bytes.Length * 8);
        foreach (byte b in bytes) list.AddRange(Bits(b));
        return list;
    }

    private static Vector3 ReadVector3FixedPrecision(BinaryReader br, float min, float max, int bits) =>
        new(ReadFixedPrecisionFloat(br, min, max, bits),
            ReadFixedPrecisionFloat(br, min, max, bits),
            ReadFixedPrecisionFloat(br, min, max, bits));

    private static float ReadFixedPrecisionFloat(BinaryReader br, float min, float max, int bits)
    {
        double v = ReadVarInt(br);
        v += Math.Pow(2.0, bits - 1);
        v /= Math.Pow(2.0, bits);
        v *= max - min;
        v += min;
        return (float)v;
    }

    private static long ReadVarInt(BinaryReader br)
    {
        ulong n = ReadVarUInt(br);
        return (long)((n >> 1) ^ (0UL - (n & 1)));
    }

    /// <summary>SQLite4-style variable length integer.</summary>
    private static ulong ReadVarUInt(BinaryReader br)
    {
        byte a0 = br.ReadByte();
        if (a0 < 241) return a0;
        byte a1 = br.ReadByte();
        if (a0 <= 248) return (ulong)(240 + ((a0 - 241L) << 8) + a1);
        byte a2 = br.ReadByte();
        if (a0 == 249) return 2288UL + ((ulong)a1 << 8) + a2;
        byte a3 = br.ReadByte();
        if (a0 == 250) return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16);
        byte a4 = br.ReadByte();
        if (a0 == 251) return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24);
        byte a5 = br.ReadByte();
        if (a0 == 252) return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32);
        byte a6 = br.ReadByte();
        if (a0 == 253) return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32) + ((ulong)a6 << 40);
        byte a7 = br.ReadByte();
        if (a0 == 254) return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32) + ((ulong)a6 << 40) + ((ulong)a7 << 48);
        byte a8 = br.ReadByte();
        return a1 + ((ulong)a2 << 8) + ((ulong)a3 << 16) + ((ulong)a4 << 24) + ((ulong)a5 << 32) + ((ulong)a6 << 40) + ((ulong)a7 << 48) + ((ulong)a8 << 56);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Decompress(byte[] raw, ButterCompression format) => format switch
    {
        ButterCompression.None => raw,
        ButterCompression.Gzip => Gunzip(raw),
        ButterCompression.Zstd3 or ButterCompression.Zstd7 or ButterCompression.Zstd15
            or ButterCompression.Zstd22 or ButterCompression.Zstd7Dict => Unzstd(raw),
        _ => throw new NotSupportedException($"Unknown compression format {(byte)format}."),
    };

    private static byte[] Gunzip(byte[] raw)
    {
        using var src = new MemoryStream(raw);
        using var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    private static byte[] Unzstd(byte[] raw)
    {
        using var d = new ZstdNet.Decompressor();
        return d.Unwrap(raw);
    }

    private static List<bool> Bits(byte b)
    {
        var list = new List<bool>(8);
        for (int i = 0; i < 8; i++) list.Add((b & (1 << i)) != 0);
        return list;
    }

    private static string ReadCString(BinaryReader br, int max = 1024)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < max; i++)
        {
            byte b = br.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static string ReadSessionId(BinaryReader br)
    {
        string s = Convert.ToHexString(br.ReadBytes(16));
        return s.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
    }

    private static string ReadIpAddress(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
    }

    private static double ReadHalf(BinaryReader br) => (double)BitConverter.ToHalf(br.ReadBytes(2));

    private static Vector3 ReadVector3Half(BinaryReader br) =>
        new((float)ReadHalf(br), (float)ReadHalf(br), (float)ReadHalf(br));

    private static (Vector3, Quaternion) ReadPose(BinaryReader br) =>
        (ReadVector3Half(br), ReadSmallestThree(br));

    /// <summary>
    /// Smallest-three quaternion decode. The dropped-component index lives in the low two
    /// bits; the three kept components follow as 10-bit values scaled to ±1/√2.
    /// </summary>
    private static Quaternion ReadSmallestThree(BinaryReader br)
    {
        uint packed = br.ReadUInt32();
        uint index = packed & 3;
        float a = Uncompress((packed & 0x00000FFCu) >> 2);
        float b = Uncompress((packed & 0x003FF000u) >> 12);
        float c = Uncompress((packed & 0xFFC00000u) >> 22);
        float d = MathF.Sqrt(MathF.Max(0f, 1f - a * a - b * b - c * c));
        return index switch
        {
            0 => new Quaternion(d, a, b, c),
            1 => new Quaternion(a, d, b, c),
            2 => new Quaternion(a, b, d, c),
            _ => new Quaternion(a, b, c, d),
        };

        static float Uncompress(uint v) => (float)(v / 1023.0 * 1.41421356 - 0.70710678);
    }

    /// <summary>
    /// Stats are written in alphabetical field order — 10 single-byte counters, then a
    /// half-precision possession time, then a 16-bit stun count. 14 bytes total.
    /// </summary>
    /// <summary>
    /// Stats are stored as a delta against the previous frame, and the writer casts that
    /// difference straight to a byte — so a counter that goes *down* (a player rejoining, a
    /// round resetting the scoreboard) wraps round to 255 rather than clamping. Reading these
    /// back as unsigned turns a -1 into +255 and permanently inflates the running total, so
    /// every wrapped field has to come back signed.
    ///
    /// The two exceptions are written differently: <c>stuns</c> is clamped non-negative by the
    /// writer before being stored as a 16-bit value, and <c>possession_time</c> is a half,
    /// which carries its own sign.
    /// </summary>
    private static Stats ReadStats(BinaryReader br) => new()
    {
        assists = br.ReadSByte(),
        blocks = br.ReadSByte(),
        catches = br.ReadSByte(),
        goals = br.ReadSByte(),
        interceptions = br.ReadSByte(),
        passes = br.ReadSByte(),
        points = br.ReadSByte(),
        saves = br.ReadSByte(),
        steals = br.ReadSByte(),
        shots_taken = br.ReadSByte(),
        possession_time = (float)ReadHalf(br),
        stuns = br.ReadUInt16(),
    };

    private static Vector3 ToVec(List<float>? l) =>
        l is { Count: >= 3 } ? new Vector3(l[0], l[1], l[2]) : Vector3.Zero;

    private static List<float> ToList(Vector3 v) => new() { v.X, v.Y, v.Z };

    private static Vector3 Forward(Quaternion q) => Vector3.Transform(new Vector3(0, 0, 1), q);
    private static Vector3 Left(Quaternion q) => Vector3.Transform(new Vector3(1, 0, 0), q);
    private static Vector3 Up(Quaternion q) => Vector3.Transform(new Vector3(0, 1, 0), q);

    private static string FormatClock(float time)
    {
        int minutes = (int)time / 60;
        int seconds = (int)time % 60;
        int hundredths = (int)((time - (int)time) * 100f);
        return $"{minutes:D2}:{seconds:D2}.{hundredths:D2}";
    }

    /// <summary>Matches EchoVRAPI.Frame.InArena, which keys off the map rather than match type.</summary>
    private static bool IsArena(string? mapName) => mapName == "mpl_arena_a";

    private static readonly string[] CombatMaps =
        { "mpl_combat_fission", "mpl_combat_combustion", "mpl_combat_dyson", "mpl_combat_gauss" };

    private static bool IsCombat(string? mapName) => mapName != null && CombatMaps.Contains(mapName);

    private static string TeamFromIndex(byte i) => i switch
    {
        0 => "blue",
        1 => "orange",
        2 => "spectator",
        _ => "none",
    };

    private static string PausedStateFromByte(byte b) => b switch
    {
        0 => "unpaused",
        1 => "paused",
        2 => "unpausing",
        3 => "pausing",
        _ => "unpaused",
    };

    private static string GameStatusFromByte(byte b) => b switch
    {
        1 => "",
        2 => "pre_match",
        3 => "round_start",
        4 => "playing",
        5 => "score",
        6 => "round_over",
        7 => "post_match",
        8 => "pre_sudden_death",
        9 => "sudden_death",
        10 => "post_sudden_death",
        _ => "NOT FOUND",
    };

    private static readonly string[] WeaponNames = { "rocket", "blaster", "scout", "assault" };
    private static readonly string[] OrdnanceNames = { "stun", "det", "burst", "arc" };
    private static readonly string[] TacModNames = { "sensor", "wraith", "heal", "shield" };

    private static readonly string[] GoalTypes =
    {
        "unknown", "BOUNCE_SHOT", "INSIDE_SHOT", "LONG_BOUNCE_SHOT", "LONG_SHOT",
        "SELF_GOAL", "SLAM_DUNK", "BUMPER_SHOT", "HEADBUTT",
    };

    private static string GoalTypeFromIndex(int i) =>
        i >= 0 && i < GoalTypes.Length ? GoalTypes[i].Replace("_", " ") : "unknown";

    private static readonly string[] MapNames =
    {
        "uncoded", "mpl_lobby_b2", "mpl_arena_a", "mpl_combat_fission",
        "mpl_combat_combustion", "mpl_combat_dyson", "mpl_combat_gauss", "mpl_tutorial_arena",
    };

    private static string MapNameFromIndex(int i) =>
        i >= 0 && i < MapNames.Length ? MapNames[i] : "uncoded";

    private static string MatchTypeFor(string map, bool isPrivate) => map switch
    {
        "mpl_arena_a" => isPrivate ? "Echo_Arena_Private" : "Echo_Arena",
        "mpl_combat_fission" or "mpl_combat_combustion" or "mpl_combat_dyson" or "mpl_combat_gauss"
            => isPrivate ? "Echo_Combat_Private" : "Echo_Combat",
        _ => "Unknown",
    };

    private static string? HoldingFromByte(byte b) => b switch
    {
        255 => "none",
        254 => "geo",
        253 => "disc",
        252 => null,
        _ => b.ToString(),
    };
}

internal enum ButterCompression : byte
{
    None = 0,
    Gzip = 1,
    Zstd3 = 2,
    Zstd7 = 3,
    Zstd15 = 4,
    Zstd22 = 5,
    Zstd7Dict = 6,
}

/// <summary>
/// Header player table that grows on demand. The stock decoder throws when a frame
/// references a slot beyond the recorded roster — which is exactly what happens when
/// somebody joins after recording began. Synthesising the slot keeps the replay readable.
/// </summary>
internal sealed class ButterHeaderV3
{
    public ushort KeyframeInterval;
    public ButterCompression Compression;
    public string ClientName = "";
    public string SessionId = "";
    public string SessionIp = "";
    public int TotalRoundCount;
    public int BlueRoundScore;
    public int OrangeRoundScore;
    public bool PrivateMatch;
    public string MapName = "mpl_arena_a";
    public string MatchType = "Echo_Arena";

    public readonly List<string> Names = new();
    public readonly List<long> UserIds = new();
    public readonly List<byte> Numbers = new();
    public readonly List<byte> Levels = new();

    public int SynthesizedSlots { get; private set; }

    /// <summary>
    /// Slots are 1-based on the wire; index 0 means "no player". A frame may reference a
    /// slot past the recorded roster when somebody joined after recording began, so the
    /// table is grown with a stable synthetic identity instead of throwing.
    /// </summary>
    private bool Resolve(byte slot, out int i)
    {
        i = slot - 1;
        if (slot == 0) return false;
        while (Names.Count <= i)
        {
            Names.Add($"Unknown player {Names.Count + 1}");
            UserIds.Add(-1000L - Names.Count);
            Numbers.Add(0);
            Levels.Add(0);
            SynthesizedSlots++;
        }
        return true;
    }

    public string NameAt(byte slot) => Resolve(slot, out int i) ? Names[i] : "[INVALID]";
    public long UserIdAt(byte slot) => Resolve(slot, out int i) ? UserIds[i] : 0;
    public int NumberAt(byte slot) => Resolve(slot, out int i) ? Numbers[i] : -1;
    public int LevelAt(byte slot) => Resolve(slot, out int i) ? Levels[i] : -1;
}
