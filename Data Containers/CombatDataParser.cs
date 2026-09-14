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

        /// <summary>Whether the feed has seen its first frame yet; sessionid alone can't say.</summary>
        private static bool killFeedStarted;

        /// <summary>The last kill added to the feed, as killer|victim|weapon.</summary>
        private static string lastFeedKillKey;

        /// <summary>
        /// The victim's death count when <see cref="lastFeedKillKey"/> was recorded, or -1 when their
        /// row couldn't be found. Lets a repeat of an identical kill be told apart from the API simply
        /// still holding the previous one.
        /// </summary>
        private static int lastFeedVictimDeaths = -1;

        /// <summary>The killer's kill count at the same moment, or -1 when there's no killer row.</summary>
        private static int lastFeedKillerKills = -1;

        public static void Parse(string rawSessionJson)
        {
            if (string.IsNullOrWhiteSpace(rawSessionJson)) return;

            try
            {
                JObject json = JObject.Parse(rawSessionJson);

                lock (ParseLock)
                {
                    CurrentCombatStats.Clear();

                    // Deaths by name, for telling a repeated identical kill from a held one below.
                    // last_kill identifies players by name only, so that's the key it has to be.
                    Dictionary<string, int> deathsByName = new Dictionary<string, int>();
                    Dictionary<string, int> killsByName = new Dictionary<string, int>();

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

                                        string playerName = player["name"]?.ToString();
                                        if (!string.IsNullOrEmpty(playerName) && !deathsByName.ContainsKey(playerName))
                                        {
                                            deathsByName[playerName] = cs.deaths;
                                            killsByName[playerName] = cs.kills;
                                        }
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

                    UpdateKillFeed(json["sessionid"]?.ToString(), json["last_kill"], deathsByName, killsByName);
                }
            }
            catch (Exception)
            {
                // Ignore parse errors, just means missing or malformed combat API data
            }
        }

        /// <summary>
        /// Adds last_kill to the feed when it describes a kill that hasn't been counted yet.
        /// Call under <see cref="ParseLock"/>.
        /// <para>
        /// The API holds its last kill until the next one, so this sees the same kill on every
        /// frame and has to notice when it changes. That alone would lose the second of two
        /// identical kills — A kills B, B respawns, A kills B again with the same weapon — because
        /// nothing in last_kill differs, so the players' counters are checked as well: if the
        /// victim's deaths and the killer's kills have both gone up while last_kill looks the same,
        /// that's a new kill. Both, not just deaths — a suicide or a fall adds a death without
        /// crediting anyone, and if the API leaves last_kill alone for those, deaths alone would
        /// replay the previous kill into the feed.
        /// </para>
        /// </summary>
        private static void UpdateKillFeed(string sessionId, JToken lastKill,
            Dictionary<string, int> deathsByName, Dictionary<string, int> killsByName)
        {
            string killer = lastKill?["killer"]?.ToString() ?? "";
            string killed = lastKill?["killed"]?.ToString() ?? "";
            string weapon = lastKill?["killed_with"]?.ToString() ?? "";

            bool isKill = !string.IsNullOrEmpty(killer) || !string.IsNullOrEmpty(killed);
            string key = isKill ? killer + "|" + killed + "|" + weapon : null;
            int victimDeaths = killed.Length > 0 && deathsByName.TryGetValue(killed, out int d) ? d : -1;
            int killerKills = killer.Length > 0 && killsByName.TryGetValue(killer, out int k) ? k : -1;

            if (!killFeedStarted || sessionId != killFeedSessionId)
            {
                // New match. Whatever last_kill already holds happened before we got here — possibly
                // in the previous match, since the API carries it over — so it's taken as the starting
                // point rather than shown as a fresh kill.
                killFeedStarted = true;
                killFeedSessionId = sessionId;
                KillFeed.Clear();
                lastFeedKillKey = key;
                lastFeedVictimDeaths = victimDeaths;
                lastFeedKillerKills = killerKills;
                return;
            }

            if (key == null) return;

            bool changed = key != lastFeedKillKey;
            bool victimDied = victimDeaths >= 0 && lastFeedVictimDeaths >= 0 && victimDeaths > lastFeedVictimDeaths;
            // No killer (a self or environment kill) means there's no kill count to require.
            bool killerScored = killer.Length == 0 ||
                                (killerKills >= 0 && lastFeedKillerKills >= 0 && killerKills > lastFeedKillerKills);
            bool repeated = !changed && victimDied && killerScored;

            // Keep the death count current even when nothing is added, so a later repeat is measured
            // against the latest figure and not the one from when the kill first appeared.
            if (!changed && !repeated)
            {
                if (victimDeaths >= 0) lastFeedVictimDeaths = victimDeaths;
                if (killerKills >= 0) lastFeedKillerKills = killerKills;
                return;
            }

            // A copy, not CurrentLastKill itself: that one object is overwritten every frame, so
            // storing it would turn every entry in the feed into whatever the latest kill is.
            KillFeed.Insert(0, new LastKill { killer = killer, killed = killed, killed_with = weapon });
            if (KillFeed.Count > KillFeedLimit)
            {
                KillFeed.RemoveRange(KillFeedLimit, KillFeed.Count - KillFeedLimit);
            }

            lastFeedKillKey = key;
            lastFeedVictimDeaths = victimDeaths;
            lastFeedKillerKills = killerKills;
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
