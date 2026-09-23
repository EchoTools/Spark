using System;
using System.Collections.Generic;
using System.Numerics;
using EchoVRAPI;
using Newtonsoft.Json.Linq;
using static Logger;

namespace Spark
{
	/// <summary>
	/// Builds a fixed sample match for the -uipreview flag.
	/// <para>
	/// The dashboard is only honest to look at when it has data in it: an idle window hides
	/// column alignment, row overflow, and how the bars scale. This lets the real WPF layout be
	/// checked against the design without waiting for a live game.
	/// </para>
	/// </summary>
	public static class UiPreviewData
	{
		private static readonly (string name, int ping, float loss, float speed)[] bluePlayers =
		{
			("he_is_the_cat", 133, 0.018f, 8.2f),
			("Hollow-", 55, 0.002f, 11.0f)
		};

		private static readonly (string name, int ping, float loss, float speed)[] orangePlayers =
		{
			("Aqua", 65, 0.001f, 12.4f),
			("GOJIRA", 76, 0.004f, 9.1f),
			("BagOchips", 44, 0.000f, 6.7f)
		};

		/// <summary>Builds the sample frame the preview renders from.</summary>
		public static Frame BuildFrame()
		{
			Frame frame = new Frame
			{
				sessionid = "8a2f41c9-0000-0000-0000-000000000000",
				sessionip = "127.0.0.1",
				game_clock_display = "05:00.00",
				game_clock = 300,
				game_status = "playing",
				blue_points = 2,
				orange_points = 5,
				blue_round_score = 1,
				orange_round_score = 0,
				total_round_count = 1,
				private_match = true,
				match_type = "Echo_Arena",
				// InArena gates the score, clock and disc readouts, and it keys off the map.
				map_name = "mpl_arena_a",
				pause = new Pause
				{
					paused_state = "unpaused",
					paused_requested_team = "none",
					unpaused_team = "none"
				},
				possession = new List<int> { 1, 0 },
				disc = new Disc { velocity = new List<float> { 6.1f, 2.4f, 7.9f } },
				last_throw = new LastThrow
				{
					total_speed = 18.05f,
					speed_from_arm = 12.00f,
					speed_from_wrist = 2.28f,
					speed_from_movement = 3.76f,
					arm_speed = 12.00f,
					rot_per_sec = 4.40f,
					pot_speed_from_rot = 3.32f,
					off_axis_spin_deg = 35.8f,
					wrist_align_to_throw_deg = 6.1f,
					throw_align_to_movement_deg = 22.4f
				},
				teams = new List<Team>
				{
					BuildTeam(Team.TeamColor.blue, bluePlayers),
					BuildTeam(Team.TeamColor.orange, orangePlayers),
					BuildTeam(Team.TeamColor.spectator, Array.Empty<(string, int, float, float)>())
				}
			};

			return frame;
		}

		/// <summary>
		/// Pushes sample rounds onto <see cref="Program.rounds"/> so Previous Rounds, Previous Goals
		/// and Joust Times have something to lay out. Previous Goals and Joust Times both read
		/// through <c>rounds</c>, so they can only be exercised this way.
		/// </summary>
		/// <summary>
		/// Logs a handful of representative Event Log lines, in the exact "{clock} - {message}" shape
		/// LoggerEvents.Log produces, so the reworked Event Log tab has real rows — including a couple
		/// of unclassified lines like the CameraWriteController diagnostics — to check the row styling
		/// and classifier against without waiting on a live match.
		/// </summary>
		public static void PopulateEventLog()
		{
			string sessionId = "8a2f41c9-0000-0000-0000-000000000000";
			string[] lines =
			{
				"00:41.20 - Joined game",
				"00:38.70 - he_is_the_cat used the left emote",
				"00:35.10 - Player Joined: korone.o_o",
				"00:33.90 - he_is_the_cat threw the disk at 18.05 m/s with their right hand",
				"00:30.40 - BagOchips scored at 9.20 m/s from 11.60 m away, assisted by Mist!",
				"00:30.40 - Goal angle: 42.30 deg, from the front",
				"00:30.40 - ORANGE: 5  BLUE: 2",
				"00:27.80 - Hollow- made a save",
				"00:24.00 - Aqua stunned he_is_the_cat",
				"00:21.30 - orange team requested a pause (GOJIRA, 2.10 m)",
				"00:18.00 - Player Left: ToeSucker",
				"00:15.00 - Player 3 camera distance: 0.114 m.  Name: EugeneTheMexican",
				"00:12.00 - Correct player found."
			};

			foreach (string line in lines)
			{
				LogRow(LogType.File, sessionId, line);
			}
		}

		public static void PopulateRounds(Frame baseFrame)
		{
			(float clock, int points, string scorer, float speed, float distance, string team)[] goals =
			{
				(21f, 3, "BagOchips", 9.2f, 11.6f, "orange"),
				(86f, 2, "Mist", 11.3f, 4.7f, "orange"),
				(265f, 2, "XO", 16.0f, 3.4f, "blue"),
				(73f, 3, "twenty.5", 13.4f, 7.9f, "blue"),
				(115f, 2, "korone.o_o", 12.4f, 5.3f, "orange")
			};

			(string name, long millis, bool blue)[] jousts =
			{
				("Mist", 1700, false),
				("GOJIRA", 2450, false),
				("he_is_the_cat", 3000, true),
				("Aqua", 1780, false),
				("suhSharife Cooper", 2490, false),
				("Hollow-", 4750, true)
			};

			AccumulatedFrame round = new AccumulatedFrame(baseFrame)
			{
				matchTime = DateTime.UtcNow.AddMinutes(-12),
				finishReason = AccumulatedFrame.FinishReason.not_finished
			};

			foreach ((float clock, int points, string scorer, float speed, float distance, string team) goal in goals)
			{
				round.goals.Enqueue(new GoalData(
					round,
					new Player { name = goal.scorer },
					new LastScore
					{
						point_amount = goal.points,
						person_scored = goal.scorer,
						disc_speed = goal.speed,
						distance_thrown = goal.distance,
						team = goal.team
					},
					goal.clock,
					Vector2.Zero, 0f, false,
					goal.team == "blue" ? Team.TeamColor.blue : Team.TeamColor.orange,
					null, null, new List<Vector3>()));
			}

			foreach ((string name, long millis, bool blue) joust in jousts)
			{
				Team team = new Team { players = new List<Player>() };
				round.events.Enqueue(new EventData(
					round,
					EventContainer.EventType.joust_speed,
					0f,
					team,
					new Player { name = joust.name, team_color = joust.blue ? Team.TeamColor.blue : Team.TeamColor.orange },
					joust.millis,
					Vector3.Zero,
					Vector3.Zero));
			}

			Program.rounds.Enqueue(round);
		}

		/// <summary>
		/// Turns the sample frame into an Echo Combat match (-uipreviewcombat) so the Combat
		/// dashboard has something real to render: assigns userids, fills in kills/deaths/damage
		/// via CombatDataParser (the same store the live combat API feed writes to), and builds the
		/// raw session JSON UpdateCombatDashboard reads loadouts and the objective from.
		/// </summary>
		public static void PopulateCombatPreview(Frame frame)
		{
			frame.match_type = "Echo_Combat_Private";
			frame.map_name = "mpl_combat_dyson";
			frame.client_name = "he_is_the_cat";
			frame.blue_round_score = 1;
			frame.orange_round_score = 0;
			frame.total_round_count = 5;

			(string weapon, string ordnance, string tacmod, int kills, int assists, int deaths, int damage, float objTime)[] blueLoadouts =
			{
				("Comet", "Arc Mine", "Barrier", 12, 4, 7, 3120, 84f),
				("Pulsar", "Detonator", "Heal", 9, 6, 5, 2480, 61f)
			};
			(string weapon, string ordnance, string tacmod, int kills, int assists, int deaths, int damage, float objTime)[] orangeLoadouts =
			{
				("Comet", "Detonator", "Barrier", 11, 5, 8, 2960, 58f),
				("Meteor", "Arc Mine", "Repair", 8, 2, 10, 2610, 45f),
				("Pulsar", "Stun", "Heal", 7, 9, 6, 2050, 33f)
			};

			JArray teamsJson = new JArray();
			long nextUserId = 1001;

			void ApplyTeam(Team.TeamColor color, (string weapon, string ordnance, string tacmod, int kills, int assists, int deaths, int damage, float objTime)[] loadouts)
			{
				Team team = frame.teams[(int)color];
				JArray playersJson = new JArray();

				for (int i = 0; i < team.players.Count; i++)
				{
					Player player = team.players[i];
					player.userid = nextUserId++;

					var loadout = i < loadouts.Length ? loadouts[i] : loadouts[loadouts.Length - 1];
					CombatDataParser.CurrentCombatStats[player.userid] = new CombatStats
					{
						kills = loadout.kills,
						assists = loadout.assists,
						deaths = loadout.deaths,
						damage = loadout.damage,
						objective_time = loadout.objTime
					};

					playersJson.Add(new JObject
					{
						["Weapon"] = loadout.weapon,
						["Ordnance"] = loadout.ordnance,
						["TacMod"] = loadout.tacmod
					});
				}

				teamsJson.Add(new JObject { ["players"] = playersJson });
			}

			ApplyTeam(Team.TeamColor.blue, blueLoadouts);
			ApplyTeam(Team.TeamColor.orange, orangeLoadouts);

			JObject json = new JObject
			{
				["blue_round_score"] = frame.blue_round_score,
				["orange_round_score"] = frame.orange_round_score,
				["total_round_count"] = frame.total_round_count,
				["map_name"] = frame.map_name,
				["contested"] = false,
				["blue_points"] = 62,
				["orange_points"] = 38,
				["teams"] = teamsJson
			};
			Program.lastJSON = json.ToString();

			CombatDataParser.KillFeed.Clear();
			CombatDataParser.KillFeed.AddRange(new[]
			{
				new LastKill { killer = "he_is_the_cat", killed = "BagOchips", killed_with = "Comet" },
				new LastKill { killer = "GOJIRA", killed = "Hollow-", killed_with = "Pulsar" },
				new LastKill { killer = "Aqua", killed = "he_is_the_cat", killed_with = "Comet" }
			});
		}

		private static Team BuildTeam(Team.TeamColor color, (string name, int ping, float loss, float speed)[] roster)
		{
			Team team = new Team
			{
				team = color == Team.TeamColor.blue ? "BLUE TEAM"
					: color == Team.TeamColor.orange ? "ORANGE TEAM" : "SPECTATORS",
				players = new List<Player>()
			};

			foreach ((string name, int ping, float loss, float speed) entry in roster)
			{
				team.players.Add(new Player
				{
					name = entry.name,
					ping = entry.ping,
					packetlossratio = entry.loss,
					team_color = color,
					// Split the speed across the axes so Length() comes back close to the value above.
					velocity = new List<float> { entry.speed * 0.7f, entry.speed * 0.4f, entry.speed * 0.58f }
				});
			}

			return team;
		}
	}
}
