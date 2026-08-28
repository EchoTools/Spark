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
