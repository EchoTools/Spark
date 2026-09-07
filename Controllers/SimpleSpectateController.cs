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

		/// <summary>
		/// How long to let a freshly launched EchoVR come up before treating it as failed. A cold
		/// start runs to tens of seconds, far past <see cref="PollMs"/>, so the follow loop has to
		/// wait this out rather than deciding a client that hasn't answered yet is dead.
		/// </summary>
		private const int ClientBootSeconds = 120;

		/// <summary>How long to let a join settle before reading back which slot it landed in.</summary>
		private const int SlotCheckDelayMs = 6000;

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
			// Launched bare, with no session id. A spectator client isn't allowed to start with
			// -lobbyid, so it comes up on -spectatorstream — which drops it into an unrelated
			// public match — and is then moved into the target's session over the local HTTP API.
			// That's the same join the follow loop makes for every later hop, so there's one code
			// path for getting into a session rather than two.
			SetStatus("Starting the spectator client...");
			LaunchSpectator(anonymous);
			DateTime lastLaunch = DateTime.UtcNow;

			// Nothing can be asked of the client until it answers on its HTTP port.
			if (!await WaitForSpectatorAsync(lastLaunch, token))
			{
				SetStatus("The spectator client didn't start.");
				return;
			}

			SetStatus($"Looking for {TargetName}...");

			string lastAttemptSession = null;
			DateTime lastAttemptTime = DateTime.MinValue;

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
						// A cold EchoVR start takes far longer than one poll, so a client that isn't
						// answering yet is usually still booting rather than gone. Relaunching on
						// the first silent poll is what made EchoVR open and close over and over:
						// each pass killed a client that had never been given time to finish
						// starting, so it never got far enough to answer.
						if ((DateTime.UtcNow - lastLaunch).TotalSeconds < ClientBootSeconds)
						{
							SetStatus("Waiting for the spectator client to start...");
							continue;
						}

						Logger.LogRow(Logger.LogType.File, LogFile, "Simple Spectate: spectator client not responding, relaunching.");
						SetStatus("Restarting the spectator client...");
						LaunchSpectator(anonymous);
						lastLaunch = DateTime.UtcNow;
						lastAttemptSession = null;
						lastAttemptTime = DateTime.MinValue;
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
					bool joined = await JoinAsSpectatorAsync(targetSessionId, anonymous, token, SetStatus);
					lastLaunch = DateTime.UtcNow;   // the join may have relaunched the client

					await Task.Delay(5000, token);

					Application.Current?.Dispatcher.Invoke(CameraWriteController.UseCameraControlKeys);

					// "Keep following them between sessions" governs the later hops only. The first
					// one always has to happen: -spectatorstream on its own leaves the client
					// watching a public match that has nothing to do with the target.
					if (joined && !autoJoin)
					{
						SetStatus($"Spectating {TargetName}. Auto-follow is off.");
						return;
					}
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

			return NormalizeSessionId(presence["lobby_id"]?.ToString());
		}

		/// <summary>
		/// Session ids turn up either as a bare guid or as a guid with a trailing ".something".
		/// The game wants the bare guid in upper case, which is the form the server browser sends.
		/// </summary>
		private static string NormalizeSessionId(string sessionId)
		{
			return string.IsNullOrEmpty(sessionId) ? null : sessionId.Split('.')[0].ToUpperInvariant();
		}

		// ─── The local spectator client ────────────────────────────────────────

		/// <summary>
		/// Brings up a spectator client already sitting in <paramref name="sessionId"/>, for the
		/// one-shot "spectate this" buttons that have no follow loop of their own.
		///
		/// The two steps can't be collapsed into a single launch: a spectator client isn't allowed
		/// to start with -lobbyid, so it has to come up on -spectatorstream and be moved into the
		/// session afterwards over its local HTTP API.
		/// </summary>
		/// <param name="anonymous">Passes -noovr, which also lets this run beside a playing client.</param>
		public static async Task<bool> SpectateSessionAsync(string sessionId, bool anonymous, CancellationToken token = default)
		{
			if (string.IsNullOrEmpty(sessionId)) return false;

			LaunchSpectatorClient(anonymous);

			if (!await WaitForSpectatorClientAsync(DateTime.UtcNow, token, null))
			{
				Logger.LogRow(Logger.LogType.File, LogFile, "Simple Spectate: spectator client never came up, nothing to join into.");
				return false;
			}

			bool joined = await JoinAsSpectatorAsync(NormalizeSessionId(sessionId), anonymous, token, null);
			Logger.LogRow(Logger.LogType.File, LogFile, $"Simple Spectate: one-shot join into {sessionId} {(joined ? "succeeded" : "failed")}.");
			return joined;
		}

		private void LaunchSpectator(bool anonymous) => LaunchSpectatorClient(anonymous);

		/// <summary>
		/// Whether the spectator client is actually in a spectator slot, or was seated as a player.
		/// Null when it can't be told.
		///
		/// This has to be read back rather than assumed, because nothing in the join request steers
		/// it. The server only resolves an unassigned entrant role for social and *public*
		/// arena/combat lobbies (evr_match.go, the TeamUnassigned switch); a private arena falls
		/// through, the role stays unassigned, and the game seats the client on a team. Asking for
		/// a role in the join body does not help — team_idx -1, 2 and omitting it entirely all come
		/// back as a player.
		/// </summary>
		private static async Task<bool?> IsInSpectatorSlotAsync()
		{
			try
			{
				string json = await FetchUtils.GetRequestAsync($"http://127.0.0.1:{SPECTATOR_PORT}/session", null);
				if (string.IsNullOrEmpty(json) || json[0] != '{') return null;

				JObject frame = JObject.Parse(json);
				string me = frame["client_name"]?.ToString();
				if (string.IsNullOrEmpty(me)) return null;
				if (frame["teams"] is not JArray teams) return null;

				for (int i = 0; i < teams.Count; i++)
				{
					if (teams[i]["players"] is not JArray players) continue;

					foreach (JToken player in players)
					{
						if (!string.Equals(player["name"]?.ToString(), me, StringComparison.OrdinalIgnoreCase)) continue;

						// teams[0] and teams[1] are the playing teams; anything after them is
						// spectators, so landing outside the first two is the good case.
						return i >= 2;
					}
				}

				// In the session but on nobody's roster, which is what a spectator looks like.
				return true;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>
		/// Whether the session is a public match. The match API lists exactly the public ones, so a
		/// session it doesn't know about is private. Null when the lookup can't be completed.
		/// </summary>
		private static async Task<bool?> IsPublicSessionAsync(string sessionId)
		{
			try
			{
				string json = await FetchUtils.GetRequestAsync(PublicMatchesUrl, null);
				if (string.IsNullOrEmpty(json)) return null;
				if (JObject.Parse(json)["labels"] is not JArray labels) return null;

				foreach (JToken server in labels)
				{
					if (SameSession(NormalizeSessionId((string)server["id"]), sessionId)) return true;
				}

				return false;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>
		/// Puts the spectator client into <paramref name="sessionId"/> as a spectator.
		///
		/// A private match has to be entered through the launch arguments. The join API carries no
		/// role, and the server only resolves an unassigned role for social and *public*
		/// arena/combat lobbies, so joining a private match that way gets the client seated on a
		/// team instead — measured with team_idx -1, team_idx 2, and with the field omitted, all
		/// three of which came back as a player. Pairing the session id with -spectatorstream at
		/// launch is the only way into a private match as a spectator.
		///
		/// Public matches keep the join API, which is much cheaper: no client restart.
		/// </summary>
		private static async Task<bool> JoinAsSpectatorAsync(string sessionId, bool anonymous, CancellationToken token, Action<string> status)
		{
			if (await IsPublicSessionAsync(sessionId) == false)
			{
				Logger.LogRow(Logger.LogType.File, LogFile,
					$"Simple Spectate: {sessionId} is a private match, launching into it rather than joining over the API.");
				return await RelaunchIntoSessionAsync(sessionId, anonymous, token, status);
			}

			if (await Program.APISpectate(sessionId, overrideIP: "127.0.0.1", overridePort: SPECTATOR_PORT))
			{
				await Task.Delay(SlotCheckDelayMs, token);

				bool? spectating = await IsInSpectatorSlotAsync();
				if (spectating != false)
				{
					// Spectating, or the placement couldn't be read — either way don't churn the
					// client on a guess.
					return true;
				}

				Logger.LogRow(Logger.LogType.File, LogFile,
					$"Simple Spectate: join to {sessionId} seated us as a player, relaunching into the session instead.");
			}

			// The match looked public, or we couldn't tell, and the join still didn't leave us
			// spectating. Fall back to the launch that always works.
			return await RelaunchIntoSessionAsync(sessionId, anonymous, token, status);
		}

		/// <summary>
		/// Restarts the spectator client straight into <paramref name="sessionId"/>, pairing the
		/// session id with -spectatorstream so the client is a spectator from the moment it loads.
		/// Costs a client restart, which is why it isn't used for matches the API can handle.
		/// </summary>
		private static async Task<bool> RelaunchIntoSessionAsync(string sessionId, bool anonymous, CancellationToken token, Action<string> status)
		{
			status?.Invoke("Restarting the spectator client into the session...");
			LaunchSpectatorClient(anonymous, sessionId);

			if (!await WaitForSpectatorClientAsync(DateTime.UtcNow, token, status)) return false;

			await Task.Delay(SlotCheckDelayMs, token);
			return await IsInSpectatorSlotAsync() != false;
		}

		private static void LaunchSpectatorClient(bool anonymous, string sessionId = null)
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
				// sessionId is normally null, so this comes up on -spectatorstream alone and is
				// moved into the target's session by the join. It is only passed — becoming
				// -lobbyid — when that join seated us as a player and there is nothing else left
				// to try. See JoinAsSpectatorAsync.
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
		/// Waits for a freshly launched spectator client to start answering on its HTTP port.
		/// False means it never did within <see cref="ClientBootSeconds"/>.
		/// </summary>
		private Task<bool> WaitForSpectatorAsync(DateTime launchedAt, CancellationToken token)
			=> WaitForSpectatorClientAsync(launchedAt, token, SetStatus);

		private static async Task<bool> WaitForSpectatorClientAsync(DateTime launchedAt, CancellationToken token, Action<string> status)
		{
			while (!token.IsCancellationRequested && Program.running)
			{
				(bool alive, string _) = await GetSpectatorStateAsync();
				if (alive) return true;

				if ((DateTime.UtcNow - launchedAt).TotalSeconds > ClientBootSeconds) return false;

				status?.Invoke("Waiting for the spectator client to start...");
				await Task.Delay(PollMs, token);
			}

			return false;
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
