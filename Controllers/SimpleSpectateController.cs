using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace Spark
{
	/// <summary>
	/// Keeps a local spectator client sitting in whatever session the person you're following is in.
	///
	/// The old version only knew how to scan the public match API for a display name, which meant it
	/// could never follow anyone into a private match or a social lobby — those simply aren't listed.
	/// Presence from the friends bot doesn't have that limit: the player's own Spark publishes the
	/// session id it's actually in, whatever kind of session that is. So a friend (or your own second
	/// PC, signed into the same Discord account) can be followed anywhere. The public API stays on as
	/// a fallback for targets who aren't friends and aren't running Spark.
	/// </summary>
	public class SimpleSpectateController
	{
		/// <summary>
		/// The spectator client has to be on 6721: that's the port
		/// <see cref="CameraWriteController"/> writes camera commands to.
		/// </summary>
		public const int SPECTATOR_PORT = 6721;

		private const string PublicMatchesUrl = "https://g.echovrce.com/status/matches";
		private const int PollMs = 3000;

		/// <summary>How long before a join to the same session is worth trying again.</summary>
		private const int JoinRetrySeconds = 15;

		/// <summary>
		/// Spark\Log\spectate.tsv. Shared with <see cref="FriendsPresence"/> so both halves of a
		/// two-PC setup — what gets published, and what gets followed — land in one file.
		/// </summary>
		internal const string LogFile = "spectate";

		public enum FollowMode
		{
			/// <summary>Follow a named friend, falling back to the public match API.</summary>
			Named,

			/// <summary>Follow this Discord account's own other PC.</summary>
			Self,
		}

		private CancellationTokenSource cancel;

		public bool Running => cancel != null && !cancel.IsCancellationRequested;

		/// <summary>Who we're following, for status display.</summary>
		public string TargetName { get; private set; } = "";

		public FollowMode Mode { get; private set; } = FollowMode.Named;

		/// <summary>Raised with a short human-readable status whenever the loop changes what it's doing.</summary>
		public event Action<string> StatusChanged;

		/// <summary>
		/// Launches the spectator client and starts following. <paramref name="targetName"/> is
		/// ignored in <see cref="FollowMode.Self"/>.
		/// </summary>
		public void Start(FollowMode mode, string targetName, bool anonymous, bool autoJoin)
		{
			Stop();

			Mode = mode;
			TargetName = mode == FollowMode.Self
				? (string.IsNullOrWhiteSpace(SparkSettings.instance.client_name) ? "your other PC" : SparkSettings.instance.client_name)
				: targetName;

			if (!File.Exists(SparkSettings.instance.echoVRPath))
			{
				new MessageBox(Properties.Resources.echovr_path_not_set, Properties.Resources.Error).Show();
				return;
			}

			// This client is a spectator, not a player. If it kept publishing presence it would
			// overwrite the row it's reading from — fatal in Self mode, wrong in every mode.
			FriendsPresence.SuppressPresencePush = true;

			cancel = new CancellationTokenSource();
			CancellationToken token = cancel.Token;

			Task.Run(async () =>
			{
				try
				{
					await RunAsync(anonymous, autoJoin, token);
				}
				catch (OperationCanceledException)
				{
					// Stop() was called.
				}
				catch (Exception e)
				{
					Logger.LogRow(Logger.LogType.Error, $"Simple Spectate: follow loop died.\n{e}");
					SetStatus("Stopped after an error");
				}
			}, token);
		}

		public void Stop()
		{
			cancel?.Cancel();
			cancel = null;
			FriendsPresence.SuppressPresencePush = false;
		}

		private async Task RunAsync(bool anonymous, bool autoJoin, CancellationToken token)
		{
			// Launching straight into the target's session saves a join round-trip when they're
			// already somewhere; otherwise the client comes up in the menu and the loop moves it.
			SetStatus($"Looking for {TargetName}...");
			string initialSessionId = await ResolveTargetSessionAsync(token);

			LaunchSpectator(initialSessionId, anonymous);

			SetStatus(initialSessionId != null
				? $"Launching into {TargetName}'s session..."
				: $"Waiting for {TargetName} to join a session...");

			if (!autoJoin)
			{
				// Nothing left to follow, but this Spark still owns a spectator client, so it stays
				// "running" (and stays quiet about its own presence) until the user stops it.
				SetStatus("Spectator launched. Auto-follow is off.");
				return;
			}

			string lastAttemptSession = initialSessionId;
			DateTime lastAttemptTime = DateTime.UtcNow;

			while (!token.IsCancellationRequested && Program.running)
			{
				await Task.Delay(PollMs, token);

				try
				{
					string targetSessionId = await ResolveTargetSessionAsync(token);
					(bool clientAlive, string ourSessionId) = await GetSpectatorStateAsync();

					if (targetSessionId == null)
					{
						// Target went quiet. Stay put rather than dropping out of a session we're
						// happily watching — they're probably mid-transition.
						SetStatus(ourSessionId != null
							? $"Watching — waiting for {TargetName}"
							: $"Waiting for {TargetName} to join a session...");
						continue;
					}

					if (SameSession(ourSessionId, targetSessionId))
					{
						SetStatus($"Spectating {TargetName}");
						continue;
					}

					if (!clientAlive)
					{
						// The spectator client is gone — closed, crashed, or never came up. A join
						// request has nothing to talk to, so bring it back straight into the session.
						Logger.LogRow(Logger.LogType.File, LogFile, "Simple Spectate: spectator client not responding, relaunching.");
						SetStatus("Restarting the spectator client...");
						LaunchSpectator(targetSessionId, anonymous);
						lastAttemptSession = targetSessionId;
						lastAttemptTime = DateTime.UtcNow;
						continue;
					}

					// Don't re-issue a join we've only just made — but do let it be retried, since a
					// join can quietly fail to take and we'd otherwise never try that session again.
					if (SameSession(lastAttemptSession, targetSessionId) &&
					    (DateTime.UtcNow - lastAttemptTime).TotalSeconds < JoinRetrySeconds)
					{
						continue;
					}

					SetStatus($"Joining {TargetName}'s session...");
					Logger.LogRow(Logger.LogType.File, LogFile, $"Simple Spectate: following {TargetName} into {targetSessionId}.");

					lastAttemptSession = targetSessionId;
					lastAttemptTime = DateTime.UtcNow;
					await Program.APIJoin(targetSessionId, overrideIP: "127.0.0.1", overridePort: SPECTATOR_PORT);

					await Task.Delay(5000, token);

					Application.Current?.Dispatcher.Invoke(CameraWriteController.UseCameraControlKeys);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception e)
				{
					Console.WriteLine($"Simple Spectate loop error: {e.Message}");
				}
			}
		}

		// ─── Finding the target ────────────────────────────────────────────────

		/// <summary>
		/// The session the target is in, or null if we can't place them. Bot presence first — it's
		/// the only source that covers private matches and lobbies — then the public match API.
		/// </summary>
		private async Task<string> ResolveTargetSessionAsync(CancellationToken token)
		{
			string fromBot = await ResolveFromBotAsync();
			if (fromBot != null) return fromBot;

			// Self-follow has no public-API equivalent: it's your own account's presence or nothing.
			if (Mode == FollowMode.Self) return null;

			return await ResolveFromPublicApiAsync(token);
		}

		private async Task<string> ResolveFromBotAsync()
		{
			if (!FriendsPresence.Configured) return null;

			try
			{
				// A Spark that never opened the Friends tab has no row on the bot yet, and /me would
				// 404 forever without this.
				await FriendsPresence.EnsureRegisteredAsync();

				if (Mode == FollowMode.Self)
				{
					JObject me = await FriendsPresence.GetMeAsync();
					return JoinableSessionId(me);
				}

				JArray friends = await FriendsPresence.GetFriendsAsync();
				if (friends == null) return null;

				foreach (JToken friend in friends)
				{
					if (!NameMatches(friend, TargetName)) continue;
					return JoinableSessionId(friend);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("Simple Spectate: friends lookup failed: " + e.Message);
			}

			return null;
		}

		private async Task<string> ResolveFromPublicApiAsync(CancellationToken token)
		{
			try
			{
				string json = await FetchUtils.GetRequestAsync(PublicMatchesUrl, null);
				if (string.IsNullOrEmpty(json)) return null;

				JArray labels = JObject.Parse(json)["labels"] as JArray;
				if (labels == null) return null;

				foreach (JToken server in labels)
				{
					if (server["players"] is not JArray players) continue;

					bool targetHere = players.Any(p =>
						string.Equals((string)p["display_name"], TargetName, StringComparison.OrdinalIgnoreCase));

					if (targetHere) return (string)server["id"];
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception e)
			{
				Console.WriteLine("Simple Spectate: public match search failed: " + e.Message);
			}

			return null;
		}

		private static bool NameMatches(JToken friend, string targetName)
		{
			if (string.IsNullOrWhiteSpace(targetName)) return false;

			// The bot returns both names and either may be the one the user typed.
			return string.Equals(friend["echo_username"]?.ToString(), targetName, StringComparison.OrdinalIgnoreCase) ||
			       string.Equals(friend["discord_username"]?.ToString(), targetName, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>The session id from a bot presence row, if it's online and actually in one.</summary>
		private static string JoinableSessionId(JToken presence)
		{
			if (presence == null) return null;
			if (presence["online"]?.Value<bool>() != true) return null;

			string sessionId = presence["lobby_id"]?.ToString();
			return string.IsNullOrEmpty(sessionId) ? null : sessionId.ToUpperInvariant();
		}

		// ─── The local spectator client ────────────────────────────────────────

		private void LaunchSpectator(string sessionId, bool anonymous)
		{
			try { Program.KillEchoVR($"-httpport {SPECTATOR_PORT}"); }
			catch (Exception e)
			{
				// Nothing to kill, or a process's command line couldn't be read. Either way the
				// launch below is still the thing we actually want to happen.
				Logger.LogRow(Logger.LogType.Error, $"Simple Spectate: couldn't close the old spectator client.\n{e}");
			}

			try
			{
				Program.StartEchoVR(
					Program.JoinType.Spectator,
					port: SPECTATOR_PORT,
					noovr: anonymous,
					session_id: sessionId);
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, $"Simple Spectate: failed to launch the spectator client.\n{e}");
			}
		}

		/// <summary>
		/// Whether the local spectator client is up, and what it's watching. Asked directly rather
		/// than read off <see cref="Program.lastFrame"/>, which on a spectator PC may be pointed at
		/// a Quest. The two answers are separate because a client sitting in the menu or a lobby
		/// replies with no session id — that's alive with nowhere to be, not a dead client.
		/// </summary>
		private static async Task<(bool alive, string sessionId)> GetSpectatorStateAsync()
		{
			try
			{
				string json = await FetchUtils.GetRequestAsync($"http://127.0.0.1:{SPECTATOR_PORT}/session", null);
				if (string.IsNullOrEmpty(json)) return (false, null);
				if (json[0] != '{') return (true, null);

				string sessionId = JObject.Parse(json)["sessionid"]?.ToString();
				return (true, string.IsNullOrEmpty(sessionId) ? null : sessionId.ToUpperInvariant());
			}
			catch (Exception)
			{
				return (false, null);
			}
		}

		private static bool SameSession(string a, string b)
		{
			return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}

		private void SetStatus(string status)
		{
			try { StatusChanged?.Invoke(status); }
			catch (Exception) { /* status display is best-effort */ }
		}
	}
}
