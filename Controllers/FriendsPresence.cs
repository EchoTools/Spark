using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Spark
{
	/// <summary>
	/// Talks to the Spark friends bot. Presence goes up from whichever Spark is watching the game,
	/// and comes back down to anything that wants to know where a friend is — the Friends tab and
	/// Simple Spectate mode both read from here.
	///
	/// The bot is the only route to a *private* match or a lobby: those never show up in the public
	/// match API, but the player's own Spark knows the session id and publishes it.
	/// </summary>
	public static class FriendsPresence
	{
		public const string SessionTypeMatch = "match";
		public const string SessionTypeLobby = "lobby";
		public const string SessionTypeMenu = "menu";

		/// <summary>How long a presence row stays fresh on the bot. Heartbeat has to beat this.</summary>
		private const int HeartbeatSeconds = 30;

		/// <summary>
		/// How often the heartbeat wakes up. Shorter than <see cref="HeartbeatSeconds"/> because the
		/// lobby id can land in the log a moment after the game reports being in a lobby, and a
		/// friend waiting to join shouldn't sit on a stale id for half a minute.
		/// </summary>
		private const int HeartbeatTickSeconds = 5;

		private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		private static readonly object stateLock = new object();

		private static string BotUrl => SecretKeys.FRIENDS_BOT_URL;

		public static bool Configured => DiscordOAuth.IsLoggedIn && !string.IsNullOrEmpty(DiscordOAuth.oauthToken);

		/// <summary>
		/// Set while this Spark is running as a spectator. Two PCs signed into the same Discord
		/// account share one presence row, so the spectating copy has to keep quiet or it overwrites
		/// the playing copy's session id with its own — and then follows itself in circles.
		/// </summary>
		public static bool SuppressPresencePush { get; set; }

		// Last thing we published, replayed by the heartbeat so the row never goes stale mid-match.
		private static string lastLobbyId;
		private static string lastTeam;
		private static string lastMode;
		private static string lastSessionType = SessionTypeMenu;
		private static bool offline = true;
		private static bool heartbeatRunning;

		// Registration is per Discord token, so a logout/login re-registers rather than reusing a
		// row the new account doesn't own.
		private static Task<string> registerTask;
		private static string registeredForToken;

		/// <summary>Last thing we logged publishing, so the heartbeat doesn't repeat itself.</summary>
		private static string lastLoggedSignature;

		/// <summary>
		/// Bumped by every push. The lobby push reads the log off-thread, so this lets it drop its
		/// result if the game already moved on to something else while it was reading.
		/// </summary>
		private static int pushGeneration;

		// ─── Requests ──────────────────────────────────────────────────────────

		private static HttpRequestMessage MakeRequest(HttpMethod method, string path, object body = null)
		{
			HttpRequestMessage req = new HttpRequestMessage(method, BotUrl + path);
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DiscordOAuth.oauthToken);
			if (body != null)
			{
				req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
			}
			return req;
		}

		private static async Task<JObject> SendAsync(HttpRequestMessage req)
		{
			HttpResponseMessage resp = await http.SendAsync(req);
			string content = await resp.Content.ReadAsStringAsync();
			if (!resp.IsSuccessStatusCode) throw new Exception(content);
			return JObject.Parse(content);
		}

		// ─── Reading ───────────────────────────────────────────────────────────

		/// <summary>Registers this Discord account with the bot and returns the friend code.</summary>
		public static async Task<string> RegisterAsync(string echoUsername)
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Post, "/friends/register", new { echo_username = echoUsername });
			JObject result = await SendAsync(req);
			return result["friend_code"]?.ToString();
		}

		/// <summary>
		/// Makes sure this account has a row on the bot, registering it once per login if not.
		///
		/// Presence updates are an UPDATE against an existing row, so without this anyone who never
		/// opened the Friends tab would push into nowhere and simply never show up for their friends
		/// — and the tab is off by default, so that's most people. Cheap and idempotent after the
		/// first call, which is why every push goes through it.
		/// </summary>
		public static Task<string> EnsureRegisteredAsync()
		{
			lock (stateLock)
			{
				if (!Configured) return Task.FromResult<string>(null);

				// Single-flight: several pushes can land at once on startup, and they should share
				// one registration rather than racing to create the same row.
				if (registerTask == null || registeredForToken != DiscordOAuth.oauthToken)
				{
					registeredForToken = DiscordOAuth.oauthToken;
					registerTask = RegisterOnceAsync();
				}

				return registerTask;
			}
		}

		private static async Task<string> RegisterOnceAsync()
		{
			try
			{
				string echoUsername = null;
				try { echoUsername = Program.lastFrame?.client_name; } catch (Exception) { }
				if (string.IsNullOrEmpty(echoUsername)) echoUsername = SparkSettings.instance?.client_name;

				string code = await RegisterAsync(echoUsername);
				if (!string.IsNullOrEmpty(code) && SparkSettings.instance != null &&
				    SparkSettings.instance.myFriendCode != code)
				{
					SparkSettings.instance.myFriendCode = code;
					SparkSettings.instance.Save();
				}
				return code;
			}
			catch (Exception e)
			{
				Console.WriteLine("FriendsPresence: register failed: " + e.Message);

				// Don't cache a failure — the next push should be free to try again.
				lock (stateLock) { registerTask = null; }
				return null;
			}
		}

		public static async Task<JArray> GetFriendsAsync()
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Get, "/friends/list");
			JObject result = await SendAsync(req);
			return result["friends"] as JArray;
		}

		/// <summary>
		/// This account's own presence row. On the spectating PC that's the *playing* PC's session,
		/// which is exactly what makes "spectate myself across two PCs" work.
		/// </summary>
		public static async Task<JObject> GetMeAsync()
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Get, "/friends/me");
			return await SendAsync(req);
		}

		public static async Task<JObject> LookupAsync(string friendCode)
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Get, $"/friends/lookup/{friendCode}");
			return await SendAsync(req);
		}

		public static async Task AddFriendAsync(string friendCode)
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Post, $"/friends/add/{friendCode}");
			await SendAsync(req);
		}

		public static async Task RemoveFriendAsync(string friendCode)
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Delete, $"/friends/remove/{friendCode}");
			await SendAsync(req);
		}

		public static async Task SetVisibilityAsync(bool isPublic)
		{
			using HttpRequestMessage req = MakeRequest(HttpMethod.Post, "/friends/visibility", new { @public = isPublic });
			await SendAsync(req);
		}

		// ─── Publishing ────────────────────────────────────────────────────────

		/// <summary>In a match — the session id from the game API is authoritative here.</summary>
		public static void PushMatch(string sessionId, string team, string mode)
		{
			Publish(NextGeneration(), sessionId, team, mode, SessionTypeMatch);
		}

		/// <summary>
		/// In a social lobby. The API has no session id to give in this state, so it comes from the
		/// client's log instead — without it friends can see you're in a lobby but can't join you.
		/// </summary>
		public static void PushLobby()
		{
			// Reading the log touches the disk and this is called from the game-polling loop, so
			// keep it off that thread. If the id hasn't been written yet the heartbeat picks it up.
			int generation = NextGeneration();
			Task.Run(() => Publish(generation, EchoLogSessionReader.CurrentSessionId, null, "In Lobby", SessionTypeLobby));
		}

		public static void PushMenu()
		{
			Publish(NextGeneration(), null, null, "In Menu", SessionTypeMenu);
		}

		public static void PushOffline()
		{
			lock (stateLock)
			{
				pushGeneration++;
				offline = true;
				lastLoggedSignature = null;
				lastLobbyId = null;
				lastTeam = null;
				lastMode = null;
				lastSessionType = SessionTypeMenu;
			}

			if (!Configured || SuppressPresencePush) return;

			Task.Run(async () =>
			{
				try
				{
					await EnsureRegisteredAsync();
					using HttpRequestMessage req = MakeRequest(HttpMethod.Post, "/friends/offline");
					using HttpResponseMessage resp = await http.SendAsync(req);
					Report("offline", null, resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}");
				}
				catch (Exception e)
				{
					Report("offline", null, e.Message);
				}
			});
		}

		private static int NextGeneration()
		{
			lock (stateLock)
			{
				return ++pushGeneration;
			}
		}

		private static void Publish(int generation, string sessionId, string team, string mode, string sessionType)
		{
			lock (stateLock)
			{
				// A newer push already landed while we were working out what to say — drop this one.
				if (generation != pushGeneration) return;

				offline = false;
				lastLobbyId = sessionId;
				lastTeam = team;
				lastMode = mode;
				lastSessionType = sessionType;
			}

			Send(sessionId, team, mode, sessionType);
			StartHeartbeat();
		}

		private static void Send(string sessionId, string team, string mode, string sessionType)
		{
			if (!Configured || SuppressPresencePush) return;

			Task.Run(async () =>
			{
				try
				{
					await EnsureRegisteredAsync();
					using HttpRequestMessage req = MakeRequest(HttpMethod.Post, "/friends/lobby", new
					{
						lobby_id = sessionId,
						team,
						mode,
						session_type = sessionType,
					});
					using HttpResponseMessage resp = await http.SendAsync(req);
					Report(sessionType, sessionId, resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}");
				}
				catch (Exception e)
				{
					Report(sessionType, sessionId, e.Message);
				}
			});
		}

		/// <summary>
		/// Logs the *outcome* of a push, not the attempt.
		///
		/// This used to log before the request was even sent, so a rejected push — an expired Discord
		/// token, the bot down — looked exactly like a successful one, which is worse than silence
		/// when you're trying to work out why a friend can't see you. Successes are logged only when
		/// what we advertise changes, since the heartbeat repeats itself every 30s; failures are
		/// always logged.
		/// </summary>
		private static void Report(string sessionType, string sessionId, string error)
		{
			string what = $"{sessionType} session {sessionId ?? "(none)"}";

			if (error != null)
			{
				lock (stateLock) { lastLoggedSignature = null; }
				Logger.LogRow(Logger.LogType.File, SimpleSpectateController.LogFile,
					$"Friends presence: FAILED to publish {what} — {error}");
				return;
			}

			string signature = $"{sessionType}|{sessionId}";
			lock (stateLock)
			{
				if (signature == lastLoggedSignature) return;
				lastLoggedSignature = signature;
			}

			Logger.LogRow(Logger.LogType.File, SimpleSpectateController.LogFile,
				$"Friends presence: published {what}.");
		}

		/// <summary>
		/// Presence used to be pushed only when the connection state changed, so anyone who stayed in
		/// one match longer than the bot's staleness window silently dropped to "offline" for their
		/// friends. Re-sending on a timer keeps the row alive, and re-reads the lobby id from the log
		/// so a lobby that changes underneath us is picked up too.
		/// </summary>
		private static void StartHeartbeat()
		{
			lock (stateLock)
			{
				if (heartbeatRunning) return;
				heartbeatRunning = true;
			}

			Task.Run(async () =>
			{
				DateTime lastSend = DateTime.UtcNow;

				while (Program.running)
				{
					await Task.Delay(HeartbeatTickSeconds * 1000);

					try
					{
						string sessionId, team, mode, sessionType;
						lock (stateLock)
						{
							if (offline) continue;
							sessionId = lastLobbyId;
							team = lastTeam;
							mode = lastMode;
							sessionType = lastSessionType;
						}

						bool changed = false;
						if (sessionType == SessionTypeLobby)
						{
							string fromLog = EchoLogSessionReader.CurrentSessionId;
							if (fromLog != sessionId)
							{
								sessionId = fromLog;
								changed = true;
								lock (stateLock) { lastLobbyId = sessionId; }
							}
						}

						if (!changed && (DateTime.UtcNow - lastSend).TotalSeconds < HeartbeatSeconds) continue;

						lastSend = DateTime.UtcNow;
						Send(sessionId, team, mode, sessionType);
					}
					catch (Exception e)
					{
						Console.WriteLine("FriendsPresence: heartbeat error: " + e.Message);
					}
				}

				lock (stateLock) { heartbeatRunning = false; }
			});
		}
	}
}
