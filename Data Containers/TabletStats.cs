using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Spark
{
	[Serializable]
	public class TabletStats
	{
		public bool IsValid()
		{
			return player_name != null;
		}

		public static bool IsValid(JToken serverprofile)
		{
			int version = serverprofile["_version"] != null ? (int)serverprofile["_version"] : -1;
			if (version is > 0 and < 4)
			{
				Logger.LogRow(Logger.LogType.Error, $"Version of file is {version}");
			}

			return version > 0;
		}

		public TabletStats(string serverprofile)
		{
			try
			{
				if (string.IsNullOrEmpty(serverprofile)) return;

				raw = serverprofile;

				JToken data;
				try
				{
					data = JsonConvert.DeserializeObject<JToken>(serverprofile);
				}
				catch (Exception ex)
				{
					Logger.LogRow(Logger.LogType.Error, $"Error deserializing tablet stats file\n{serverprofile}\n{ex}");
					return;
				}

				if (data == null) return;
				if (!IsValid(data)) return;

				discord_id = DiscordOAuth.DiscordUserID;

				var xplatformidToken = data["xplatformid"];
				if (xplatformidToken != null)
				{
					var xplatformid = (string)xplatformidToken;
					if (xplatformidToken != null && xplatformidToken.Type == JTokenType.String)if (!string.IsNullOrEmpty(xplatformid))
					{
						var parts = xplatformid.Split("-");
						if (parts.Length > 0 && long.TryParse(parts.Last(), out long parsedId))
						{
							player_id = parsedId;
						}
					}
				}
				
				player_name = data["displayname"]?.Value<string>();
				
				if (data["social"] != null)
				{
					var social = data["social"];
					ghosted_count = social["ghostcount"]?.Value<int>() ?? 0;
					muted_count = social["mutecount"]?.Value<int>() ?? 0;
				}

				update_time = data["updatetime"]?.Value<int>() ?? 0;
				
				var creationTimeToken = data["creationtime"] ?? data["createtime"];
				if (creationTimeToken != null)
				{
					creation_time = creationTimeToken.Value<int>();
				}

				purchased_combat = data["purchasedcombat"]?.Value<int>() ?? 0;
				
				if (data["stats"]?["arena"] == null) return;
				
				var arena = data["stats"]["arena"];
				var levelToken = arena["Level"];
				if (levelToken != null)
				{
					level = levelToken["val"]?.Value<int>() ?? 0;
				}
				
				if (level > 1)
				{
					highest_stuns = arena["HighestStuns"]?["val"]?.Value<int>() ?? 0;
					goal_score_percentage = arena["GoalScorePercentage"]?["val"]?.Value<float>() ?? 0;
					two_point_goals = arena["TwoPointGoals"]?["val"]?.Value<int>() ?? 0;
					highest_saves = arena["HighestSaves"]?["val"]?.Value<int>() ?? 0;
					
					var avgPointsToken = arena["AveragePointsPerGame"];
					if (avgPointsToken != null)
					{
						float val = avgPointsToken["val"]?.Value<float>() ?? 0;
						int cnt = avgPointsToken["cnt"]?.Value<int>() ?? 0;
						avg_points_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					stuns = arena["Stuns"]?["val"]?.Value<int>() ?? 0;
					stun_percentage = arena["StunPercentage"]?["val"]?.Value<float>() ?? 0;
					arena_wins = arena["ArenaWins"]?["val"]?.Value<int>() ?? 0;
					arena_win_percentage = arena["ArenaWinPercentage"]?["val"]?.Value<float>() ?? 0;
					shots_on_goal_against = arena["ShotsOnGoalAgainst"]?["val"]?.Value<int>() ?? 0;
					shots_on_goal = arena["ShotsOnGoal"]?["val"]?.Value<int>() ?? 0;
					hat_tricks = arena["HatTricks"]?["val"]?.Value<int>() ?? 0;
					highest_points = arena["HighestPoints"]?["val"]?.Value<int>() ?? 0;
					possession_time = arena["PossessionTime"]?["val"]?.Value<float>() ?? 0;
					blocks = arena["Blocks"]?["val"]?.Value<int>() ?? 0;
					bounce_goals = arena["BounceGoals"]?["val"]?.Value<int>() ?? 0;
					
					var stunsPerGameToken = arena["StunsPerGame"];
					if (stunsPerGameToken != null)
					{
						float val = stunsPerGameToken["val"]?.Value<float>() ?? 0;
						int cnt = stunsPerGameToken["cnt"]?.Value<int>() ?? 0;
						stuns_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					highest_arena_mvp_streak = arena["HighestArenaMVPStreak"]?["val"]?.Value<int>() ?? 0;
					arena_ties = arena["ArenaTies"]?["val"]?.Value<int>() ?? 0;
					
					var savesPerGameToken = arena["SavesPerGame"];
					if (savesPerGameToken != null)
					{
						float val = savesPerGameToken["val"]?.Value<float>() ?? 0;
						int cnt = savesPerGameToken["cnt"]?.Value<int>() ?? 0;
						saves_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					catches = arena["Catches"]?["val"]?.Value<int>() ?? 0;
					goal_save_percentage = arena["GoalSavePercentage"]?["val"]?.Value<float>() ?? 0;
					
					var goalsPerGameToken = arena["GoalsPerGame"];
					if (goalsPerGameToken != null)
					{
						float val = goalsPerGameToken["val"]?.Value<float>() ?? 0;
						int cnt = goalsPerGameToken["cnt"]?.Value<int>() ?? 0;
						goals_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					current_arena_mvp_streak = arena["CurrentArenaMVPStreak"]?["val"]?.Value<int>() ?? 0;
					jousts_won = arena["JoustsWon"]?["val"]?.Value<int>() ?? 0;
					passes = arena["Passes"]?["val"]?.Value<int>() ?? 0;
					three_point_goals = arena["ThreePointGoals"]?["val"]?.Value<int>() ?? 0;
					
					var assistsPerGameToken = arena["AssistsPerGame"];
					if (assistsPerGameToken != null)
					{
						float val = assistsPerGameToken["val"]?.Value<float>() ?? 0;
						int cnt = assistsPerGameToken["cnt"]?.Value<int>() ?? 0;
						assists_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					current_arena_win_streak = arena["CurrentArenaWinStreak"]?["val"]?.Value<int>() ?? 0;
					assists = arena["Assists"]?["val"]?.Value<int>() ?? 0;
					clears = arena["Clears"]?["val"]?.Value<int>() ?? 0;
					
					var avgTopSpeedToken = arena["AverageTopSpeedPerGame"];
					if (avgTopSpeedToken != null)
					{
						float val = avgTopSpeedToken["val"]?.Value<float>() ?? 0;
						int cnt = avgTopSpeedToken["cnt"]?.Value<int>() ?? 0;
						average_top_speed_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					var avgPossessionToken = arena["AveragePossessionTimePerGame"];
					if (avgPossessionToken != null)
					{
						float val = avgPossessionToken["val"]?.Value<float>() ?? 0;
						int cnt = avgPossessionToken["cnt"]?.Value<int>() ?? 0;
						average_possession_time_per_game = cnt > 0 ? val / cnt : 0;
					}
					
					arena_losses = arena["ArenaLosses"]?["val"]?.Value<int>() ?? 0;
					top_speeds_total = arena["TopSpeedsTotal"]?["val"]?.Value<float>() ?? 0;
					arena_mvps = arena["ArenaMVPs"]?["val"]?.Value<int>() ?? 0;
					arena_mvp_percentage = arena["ArenaMVPPercentage"]?["val"]?.Value<float>() ?? 0;
					punches_recieved = arena["PunchesReceived"]?["val"]?.Value<int>() ?? 0;
					block_percentage = arena["BlockPercentage"]?["val"]?.Value<float>() ?? 0;
					points = arena["Points"]?["val"]?.Value<int>() ?? 0;
					saves = arena["Saves"]?["val"]?.Value<int>() ?? 0;
					interceptions = arena["Interceptions"]?["val"]?.Value<int>() ?? 0;
					goals = arena["Goals"]?["val"]?.Value<int>() ?? 0;
					steals = arena["Steals"]?["val"]?.Value<int>() ?? 0;
				}
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, "Error in TabletStats constructor\n" + e);
				player_name = null;
				return;
			}
		}

		public string discord_id;
		public string raw;

		public long player_id;
		public string player_name;
		public int ghosted_count;
		public int muted_count;
		public int update_time;
		public int creation_time;
		public int purchased_combat;
		public int highest_stuns;
		public int level;
		public float goal_score_percentage;
		public int two_point_goals;
		public int highest_saves;
		public float avg_points_per_game;
		public int stuns;
		public float stun_percentage;
		public int arena_wins;
		public float arena_win_percentage;
		public int shots_on_goal_against;
		public int shots_on_goal;
		public int hat_tricks;
		public int highest_points;
		public float possession_time;
		public int blocks;
		public int bounce_goals;
		public float stuns_per_game;
		public int highest_arena_mvp_streak;
		public int arena_ties;
		public float saves_per_game;
		public int catches;
		public float goal_save_percentage;
		public float goals_per_game;
		public int current_arena_mvp_streak;
		public int jousts_won;
		public int passes;
		public int three_point_goals;
		public float assists_per_game;
		public int current_arena_win_streak;
		public int assists;
		public int clears;
		public float average_top_speed_per_game;
		public float average_possession_time_per_game;
		public int arena_losses;
		public float top_speeds_total;
		public int arena_mvps;
		public float arena_mvp_percentage;
		public int punches_recieved;
		public float block_percentage;
		public int points;
		public int saves;
		public int interceptions;
		public int goals;
		public int steals;
	}
}