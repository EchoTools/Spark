using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Spark
{
    public static class CombatDataParser
    {
        public static ConcurrentDictionary<long, CombatStats> CurrentCombatStats = new ConcurrentDictionary<long, CombatStats>();
        public static LastKill CurrentLastKill = new LastKill();
        public static object ParseLock = new object();

        /// <summary>
        /// Kills this session, newest first. The API only ever exposes the single most recent kill,
        /// which it holds until the next one, so the feed is built by noticing when that value
        /// changes — the same way the Arena side accumulates jousts out of per-frame events.
        /// <para>
        /// Read it under <see cref="ParseLock"/>: it is written from the frame-fetch thread and
        /// read from the UI thread.
        /// </para>
        /// </summary>
        public static readonly List<LastKill> KillFeed = new List<LastKill>();

        /// <summary>How many kills to keep. The dashboard shows about six at a time.</summary>
        private const int KillFeedLimit = 40;

        /// <summary>Session the feed belongs to, so a new match starts from an empty one.</summary>
        private static string killFeedSessionId;

        public static void Parse(string rawSessionJson)
        {
            if (string.IsNullOrWhiteSpace(rawSessionJson)) return;

            try
            {
                JObject json = JObject.Parse(rawSessionJson);

                lock (ParseLock)
                {
                    CurrentCombatStats.Clear();

                    if (json["teams"] is JArray teams)
                    {
                        foreach (var team in teams)
                        {
                            if (team["players"] is JArray players)
                            {
                                foreach (var player in players)
                                {
                                    if (player["userid"] == null) continue;
                                    long userId = player["userid"].ToObject<long>();
                                    var stats = player["stats"];
                                    
                                    if (stats != null)
                                    {
                                        CombatStats cs = new CombatStats();
                                        cs.kills = stats["kills"]?.ToObject<int>() ?? 0;
                                        cs.assists = stats["assists"]?.ToObject<int>() ?? 0;
                                        cs.deaths = stats["deaths"]?.ToObject<int>() ?? 0;
                                        cs.damage = stats["damage"]?.ToObject<float>() ?? 0;
                                        cs.damage_taken = stats["damage_taken"]?.ToObject<float>() ?? 0;
                                        cs.damage_taken_raw = stats["damage_taken_raw"]?.ToObject<float>() ?? 0;
                                        cs.eliminations = stats["eliminations"]?.ToObject<int>() ?? 0;
                                        cs.objective_eliminations = stats["objective_eliminations"]?.ToObject<int>() ?? 0;
                                        cs.objective_time = stats["objective_time"]?.ToObject<float>() ?? 0;
                                        cs.objective_damage = stats["objective_damage"]?.ToObject<float>() ?? 0;
                                        cs.hill_captures = stats["hill_captures"]?.ToObject<int>() ?? 0;
                                        cs.hill_defends = stats["hill_defends"]?.ToObject<int>() ?? 0;

                                        CurrentCombatStats[userId] = cs;
                                    }
                                }
                            }
                        }
                    }

                    if (json["last_kill"] != null)
                    {
                        CurrentLastKill.killer = json["last_kill"]["killer"]?.ToString() ?? "";
                        CurrentLastKill.killed = json["last_kill"]["killed"]?.ToString() ?? "";
                        CurrentLastKill.killed_with = json["last_kill"]["killed_with"]?.ToString() ?? "";
                    }
                }
            }
            catch (Exception)
            {
                // Ignore parse errors, just means missing or malformed combat API data
            }
        }

        public static CombatStats GetCombatStats(long userid)
        {
            if (CurrentCombatStats.TryGetValue(userid, out var stats))
            {
                return stats;
            }
            return new CombatStats();
        }
    }
}
