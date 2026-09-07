using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using EchoVRAPI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Spark
{
	/// <summary>
	/// Renders the dashboard from the design's own markup (resources/dashboard.html) in a WebView2,
	/// and feeds it the live frame.
	/// <para>
	/// The design was authored as HTML/CSS; hand-porting it to XAML is what let the two drift apart,
	/// so this hosts it directly instead. Only the dashboard body is web-rendered — the header,
	/// rails, tabs and every button stay native WPF.
	/// </para>
	/// </summary>
	public class DashboardWebHost
	{
		private readonly WebView2 view;
		private bool ready;
		private string lastPayload;

		public DashboardWebHost(WebView2 view)
		{
			this.view = view;
		}

		public bool Ready => ready;

		/// <summary>
		/// Raised every time the page finishes navigating — first load AND every reload (see
		/// <see cref="LiveWindow.RefreshDashboardClick"/>). A reload resets the page's own JS state
		/// back to its hardcoded default theme, so anything that only pushes state on change (rather
		/// than unconditionally) needs to know a reload happened and re-push, or the page is stuck
		/// showing stale defaults that the caller thinks it already sent.
		/// </summary>
		public event Action Loaded;

		/// <summary>Raised when the page's dashboard-item dropdown changes (Last Throw / Player Speeds).</summary>
		public event Action<int> DashItemChanged;

		/// <summary>Raised when the page's joust-order dropdown changes (Recent / Fastest).</summary>
		public event Action<int> JoustOrderChanged;

		public async void Start()
		{
			try
			{
				CoreWebView2Environment environment = await WebViewEnvironment.GetAsync();
				await view.EnsureCoreWebView2Async(environment);

				string page = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "dashboard.html");
				if (!File.Exists(page))
				{
					LogRowIfPossible($"Dashboard page missing at {page}; falling back to the native dashboard.");
					return;
				}

				// No context menu, no dev tools, no browser chrome — it's a panel, not a browser.
				view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
				view.CoreWebView2.Settings.AreDevToolsEnabled = false;
				view.CoreWebView2.Settings.IsStatusBarEnabled = false;
				view.CoreWebView2.Settings.IsZoomControlEnabled = false;

				view.CoreWebView2.WebMessageReceived += OnWebMessage;
				view.CoreWebView2.NavigationCompleted += (_, _) =>
				{
					ready = true;
					lastPayload = null; // force the next PushFrame through too, same reasoning as Loaded
					view.Visibility = Visibility.Visible;
					Loaded?.Invoke();
				};

				view.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
			}
			catch (Exception ex)
			{
				LogRowIfPossible($"Dashboard WebView2 failed to start; using the native dashboard.\n{ex}");
			}
		}

		private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
		{
			try
			{
				dynamic message = JsonConvert.DeserializeObject(e.WebMessageAsJson);
				string type = (string)message.type;
				int value = (int)message.value;

				if (type == "dashItem") DashItemChanged?.Invoke(value);
				else if (type == "joustOrder") JoustOrderChanged?.Invoke(value);
			}
			catch (Exception ex)
			{
				LogRowIfPossible($"Bad dashboard message.\n{ex}");
			}
		}

		/// <summary>Pushes the three theme colours so the page derives the same palette the app does.</summary>
		public void PushTheme(string dark, string mid, string light)
		{
			if (!ready) return;
			Execute($"applyTheme({Quote(dark)},{Quote(mid)},{Quote(light)})");
		}

		public void PushDashItem(int index)
		{
			if (!ready) return;
			Execute($"setDashItem({index})");
		}

		/// <summary>Sends one frame's worth of state, skipping the call when nothing changed.</summary>
		public void PushFrame(Frame frame)
		{
			if (!ready || frame == null) return;

			object payload = BuildPayload(frame);
			string json = JsonConvert.SerializeObject(payload);
			if (json == lastPayload) return;

			lastPayload = json;
			Execute($"applyData({json})");
		}

		private static object BuildPayload(Frame frame)
		{
			bool isCombat = frame.match_type != null &&
				frame.match_type.StartsWith("Echo_Combat", StringComparison.OrdinalIgnoreCase);
			object combat = isCombat ? BuildCombatPayload(frame) : null;

			List<object> players = new List<object>();
			int pingTotal = 0, pingCount = 0, worst = 0;
			float lossTotal = 0f;

			for (int t = 0; t < frame.teams.Count && t < 3; t++)
			{
				string team = t == 0 ? "blue" : t == 1 ? "orange" : "text-faint";
				foreach (Player player in frame.teams[t].players)
				{
					players.Add(new
					{
						name = player.name,
						team,
						ping = player.ping,
						speed = player.velocity.ToVector3().Length()
					});

					if (player.ping > 0)
					{
						pingTotal += player.ping;
						pingCount++;
						worst = Math.Max(worst, player.ping);
					}

					lossTotal += player.packetlossratio;
				}
			}

			LastThrow throwData = frame.last_throw;
			object throwPayload = throwData == null || throwData.total_speed <= 0 ? null : new
			{
				total = throwData.total_speed,
				arm = throwData.speed_from_arm,
				wrist = throwData.speed_from_wrist,
				move = throwData.speed_from_movement,
				armSpeed = throwData.arm_speed,
				rots = throwData.rot_per_sec,
				pot = throwData.pot_speed_from_rot,
				offAxis = throwData.off_axis_spin_deg,
				wristAlign = throwData.wrist_align_to_throw_deg,
				moveAlign = throwData.throw_align_to_movement_deg
			};

			List<EventData> jousts = Program.LastJousts.ToList();
			if (SparkSettings.instance.dashboardJoustTimeOrder == 1)
			{
				jousts.Sort((first, second) => second.joustTimeMillis.CompareTo(first.joustTimeMillis));
			}
			jousts.Reverse();

			AccumulatedFrame[] rounds = Program.rounds.ToArray();
			GoalData[] goals = Program.LastGoals.ToArray();

			return new
			{
				mode = isCombat ? "combat" : "arena",
				combat,
				disc = frame.disc?.velocity?.ToVector3().Length() ?? 0f,
				possession = frame.possession != null && frame.possession.Count > 0 ? frame.possession[0] : -1,
				@throw = throwPayload,
				players,
				avgPing = pingCount > 0 ? pingTotal / pingCount : (int?)null,
				worstPing = worst,
				loss = players.Count > 0 ? lossTotal / players.Count * 100f : 0f,
				serverScore = Program.CurrentRound.smoothedServerScore,
				serverStatus = ServerStatusText(),
				serverWarn = Program.CurrentRound.serverScore < -1.5f,
				jousts = jousts.Select(joust => new
				{
					name = joust.player.name,
					team = joust.player.team_color == Team.TeamColor.blue ? "blue" : "orange",
					seconds = joust.joustTimeMillis / 1000f
				}),
				roundsTotal = rounds.Length,
				rounds = rounds.Reverse().Take(6).Select(round => new
				{
					time = round.finishReason == AccumulatedFrame.FinishReason.not_finished
						? "live"
						: round.matchTime.ToLocalTime().ToString("t"),
					orange = round.frame.orange_points,
					blue = round.frame.blue_points,
					round = round.frame.total_round_count > 0
						? "R" + (round.frame.blue_round_score + round.frame.orange_round_score + 1) / round.frame.total_round_count
						: ""
				}),
				goals = goals.Reverse().Take(8).Select(goal => new
				{
					time = goal.GameClock.ToString("N0", CultureInfo.InvariantCulture) + "s",
					points = goal.LastScore.point_amount,
					scorer = goal.LastScore.person_scored,
					team = goal.LastScore.team == "blue" ? "blue" : "orange",
					speed = goal.LastScore.disc_speed,
					distance = goal.LastScore.distance_thrown
				})
			};
		}

		/// <summary>
		/// Builds the combat-mode payload: loadouts/rosters from CombatDataParser and the raw
		/// per-player weapon/ordnance/tacmod fields off Program.lastJSON (the WPF-native combat
		/// dashboard reads the same two sources — see the now-superseded UpdateCombatDashboard).
		/// </summary>
		private static object BuildCombatPayload(Frame frame)
		{
			if (string.IsNullOrEmpty(Program.lastJSON)) return null;

			JObject jsonObj;
			try { jsonObj = JObject.Parse(Program.lastJSON); }
			catch { return null; }

			string blueScore = jsonObj["blue_round_score"]?.ToString() ?? jsonObj["blue_points"]?.ToString() ?? "0";
			string orangeScore = jsonObj["orange_round_score"]?.ToString() ?? jsonObj["orange_points"]?.ToString() ?? "0";
			string clock = frame.game_clock_display?.Length > 3 ? frame.game_clock_display[..^3] : frame.game_clock_display;

			int roundsPlayed = (int)(jsonObj["blue_round_score"]?.ToObject<float>() ?? 0) + (int)(jsonObj["orange_round_score"]?.ToObject<float>() ?? 0);
			int totalRounds = jsonObj["total_round_count"]?.ToObject<int>() ?? frame.total_round_count;
			string roundText = totalRounds > 0 ? $"Round {Math.Min(roundsPlayed + 1, totalRounds)} of {totalRounds}" : "No round";

			string mapName = jsonObj["map_name"]?.ToString();
			string mapLabel = CombatMapDisplayName(mapName);

			JArray teamsArray = jsonObj["teams"] as JArray;
			const string BLUE = "var(--blue)", ORANGE = "var(--orange)";
			Dictionary<string, string> nameColors = new Dictionary<string, string>();
			List<(string name, int ping, string weapon, string mods, int kills, int assists, int deaths, int damage, bool isClient)>[] rawTeams =
			{
				new List<(string, int, string, string, int, int, int, int, bool)>(),
				new List<(string, int, string, string, int, int, int, int, bool)>()
			};

			for (int t = 0; t < 2 && t < frame.teams.Count; t++)
			{
				Team apiTeam = frame.teams[t];
				JToken jsonTeam = teamsArray != null && teamsArray.Count > t ? teamsArray[t] : null;
				JArray jsonPlayers = jsonTeam?["players"] as JArray;
				string teamColor = t == 0 ? BLUE : ORANGE;

				for (int p = 0; p < apiTeam.players.Count; p++)
				{
					Player apiPlayer = apiTeam.players[p];
					JToken jsonPlayer = jsonPlayers != null && jsonPlayers.Count > p ? jsonPlayers[p] : null;

					string weapon = jsonPlayer?["Weapon"]?.ToString() ?? jsonPlayer?["weapon"]?.ToString() ?? "N/A";
					string ordnance = jsonPlayer?["Ordnance"]?.ToString() ?? jsonPlayer?["ordnance"]?.ToString() ?? "N/A";
					string tacmod = jsonPlayer?["TacMod"]?.ToString() ?? jsonPlayer?["tacmod"]?.ToString() ?? "N/A";
					string mods = string.IsNullOrEmpty(ordnance) || ordnance == "N/A"
						? (string.IsNullOrEmpty(tacmod) || tacmod == "N/A" ? "" : tacmod)
						: (string.IsNullOrEmpty(tacmod) || tacmod == "N/A" ? ordnance : $"{ordnance} · {tacmod}");

					CombatStats stats = CombatDataParser.GetCombatStats(apiPlayer.userid);
					rawTeams[t].Add((apiPlayer.name, apiPlayer.ping, weapon, mods, stats.kills, stats.assists, stats.deaths, (int)stats.damage, apiPlayer.name == frame.client_name));
					nameColors[apiPlayer.name] = teamColor;
				}
			}

			int peakDamage = Math.Max(1, rawTeams[0].Concat(rawTeams[1]).Select(p => p.damage).DefaultIfEmpty(0).Max());

			List<object> Dress(int t, string color) => rawTeams[t].Select(p => (object)new
			{
				name = p.name,
				color,
				bg = p.isClient ? "var(--raised)" : "transparent",
				kills = p.kills,
				assists = p.assists,
				deaths = p.deaths,
				damage = p.damage.ToString("N0"),
				weapon = p.weapon,
				mods = p.mods,
				dmgPct = Math.Round(Math.Clamp((double)p.damage / peakDamage, 0.0, 1.0) * 100) + "%"
			}).ToList();

			int blueKills = rawTeams[0].Sum(p => p.kills), orangeKills = rawTeams[1].Sum(p => p.kills);
			int blueDamage = rawTeams[0].Sum(p => p.damage), orangeDamage = rawTeams[1].Sum(p => p.damage);

			object[] rosters =
			{
				new { name = "Blue team", color = BLUE, players = Dress(0, BLUE), summary = $"{blueKills} K · {blueDamage:N0} dmg" },
				new { name = "Orange team", color = ORANGE, players = Dress(1, ORANGE), summary = $"{orangeKills} K · {orangeDamage:N0} dmg" }
			};

			object[] totals =
			{
				new { name = "Blue", color = BLUE, tint = "var(--blue-tint)", kills = blueKills, damage = blueDamage.ToString("N0"), objective = FormatObjectiveTime(SumObjectiveTime(frame.teams.Count > 0 ? frame.teams[0] : null)) },
				new { name = "Orange", color = ORANGE, tint = "var(--orange-tint)", kills = orangeKills, damage = orangeDamage.ToString("N0"), objective = FormatObjectiveTime(SumObjectiveTime(frame.teams.Count > 1 ? frame.teams[1] : null)) }
			};

			// ── Objective (capture point vs payload) ──────────────────────────
			bool isPayloadMap = mapName == "mpl_combat_fission" || mapName == "mpl_combat_gauss";
			object objective;
			if (isPayloadMap)
			{
				JToken payload = jsonObj["payload"];
				float distance = payload?["distance"]?.ToObject<float>() ?? 0;
				float speed = payload?["speed"]?.ToObject<float>() ?? 0;
				float pct = payload?["progress"]?.ToObject<float>() ?? payload?["percentage"]?.ToObject<float>() ?? Math.Clamp(distance / 200f, 0f, 1f);
				bool isMoving = speed > 0.05f;

				objective = new
				{
					isPayload = true,
					stateText = isMoving ? "MOVING" : "STOPPED",
					stateColor = isMoving ? "var(--good)" : "var(--text-faint)",
					payloadPct = Math.Round(pct * 100) + "%",
					distance = $"{distance:N1} m",
					speed = $"{speed:N2} m/s"
				};
			}
			else
			{
				bool isContested = jsonObj["contested"]?.ToObject<bool>() ?? false;
				float blueProgress = jsonObj["blue_points"]?.ToObject<float>() ?? 0;
				float orangeProgress = jsonObj["orange_points"]?.ToObject<float>() ?? 0;

				string stateText; string stateColor;
				if (isContested) { stateText = "CONTESTED"; stateColor = "var(--bad)"; }
				else if (blueProgress > orangeProgress) { stateText = "BLUE HOLDS"; stateColor = BLUE; }
				else if (orangeProgress > blueProgress) { stateText = "ORANGE HOLDS"; stateColor = ORANGE; }
				else { stateText = "NEUTRAL"; stateColor = "var(--text-faint)"; }

				objective = new
				{
					isPayload = false,
					stateText,
					stateColor,
					bluePct = $"{blueProgress:N0}%",
					orangePct = $"{orangeProgress:N0}%"
				};
			}

			// ── Kill feed ───────────────────────────────────────────────────────
			List<object> kills;
			lock (CombatDataParser.ParseLock)
			{
				kills = CombatDataParser.KillFeed.Select((k, i) => (object)new
				{
					killer = string.IsNullOrEmpty(k.killer) ? "Self" : k.killer,
					victim = string.IsNullOrEmpty(k.killed) ? "Unknown" : k.killed,
					weapon = k.killed_with,
					killerColor = nameColors.TryGetValue(k.killer ?? "", out string kc) ? kc : "var(--text-dim)",
					victimColor = nameColors.TryGetValue(k.killed ?? "", out string vc) ? vc : "var(--text-dim)",
					bg = i == 0 ? "var(--raised)" : "transparent"
				}).ToList();
			}

			// ── Network (blue/orange only — no spectators, matching the original native combat dashboard) ──
			int pingTotal = 0, pingCount = 0, worstPing = 0;
			float lossTotal = 0f;
			List<object> pings = new List<object>();
			for (int t = 0; t < 2; t++)
			{
				string color = t == 0 ? BLUE : ORANGE;
				foreach (var p in rawTeams[t])
				{
					if (p.ping > 0)
					{
						pingTotal += p.ping;
						pingCount++;
						worstPing = Math.Max(worstPing, p.ping);
					}

					string pingColor = p.ping <= 0 ? "var(--text-faint)" : p.ping < 70 ? "var(--good)" : p.ping < 110 ? "var(--warn)" : "var(--bad)";
					pings.Add(new { name = p.name, color, ping = p.ping > 0 ? p.ping.ToString() : "--", pct = Math.Min(100, p.ping / 2) + "%", pingColor });
				}
			}
			int pingPlayerCount = rawTeams[0].Count + rawTeams[1].Count;
			// packetlossratio isn't carried in the raw tuple above, so it's pulled straight off the frame.
			for (int t = 0; t < 2 && t < frame.teams.Count; t++)
			{
				foreach (Player player in frame.teams[t].players) lossTotal += player.packetlossratio;
			}
			float lossPercent = pingPlayerCount > 0 ? lossTotal / pingPlayerCount * 100f : 0f;

			object net = new
			{
				status = ServerStatusText(),
				warn = Program.CurrentRound.serverScore < -1.5f,
				avg = pingCount > 0 ? (int?)(pingTotal / pingCount) : null,
				worst = worstPing,
				loss = pingPlayerCount > 0 ? lossPercent.ToString("N1") : "--",
				score = Program.CurrentRound.serverScore > 0 ? Program.CurrentRound.smoothedServerScore.ToString("N1") : "--",
				scorePct = Math.Max(0, Math.Min(100, Program.CurrentRound.smoothedServerScore / 150.0 * 100.0))
			};

			return new
			{
				map = mapLabel,
				blueScore,
				orangeScore,
				clock,
				roundText,
				objective,
				totals,
				rosters,
				kills,
				feedCount = kills.Count + " total",
				pings,
				net
			};
		}

		private static float SumObjectiveTime(Team team)
		{
			if (team?.players == null) return 0f;
			return team.players.Sum(p => CombatDataParser.GetCombatStats(p.userid).objective_time);
		}

		private static string FormatObjectiveTime(float seconds)
		{
			TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
			return $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
		}

		private static string CombatMapDisplayName(string mapName)
		{
			return mapName switch
			{
				"mpl_combat_dyson" => "DYSON",
				"mpl_combat_combustion" => "COMBUSTION",
				"mpl_combat_fission" => "FISSION",
				"mpl_combat_gauss" => "SURGE",
				_ => "--"
			};
		}

		private static string ServerStatusText()
		{
			if (Program.CurrentRound.serverScore > 0)
			{
				return $"{Properties.Resources.Score_} {Program.CurrentRound.smoothedServerScore:N1}";
			}

			if (Math.Abs(Program.CurrentRound.serverScore - -1) < .1f) return ">150";
			if (Program.CurrentRound.serverScore < -1.5f) return "Wrong player count";
			return $"{Properties.Resources.Score_} --";
		}

		private void Execute(string script)
		{
			try
			{
				view.CoreWebView2?.ExecuteScriptAsync(script);
			}
			catch (Exception)
			{
				// A torn-down WebView during shutdown isn't worth reporting.
			}
		}

		private static string Quote(string value)
		{
			return JsonConvert.SerializeObject(value ?? string.Empty);
		}

		private static void LogRowIfPossible(string message)
		{
			try { Logger.LogRow(Logger.LogType.Error, message); } catch { }
		}
	}
}
