using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks; // Added for Task
using System.Collections.Generic;
using DiscordRPC;
using DiscordRPC.Logging;
using DiscordRPC.Message;
using EchoVRAPI;
using Spark.Properties;
using static Logger;

namespace Spark
{
	internal static class DiscordRichPresence
	{
		private static DiscordRpcClient discordClient;
		private static DateTime initialStateTime;

		private enum GlobalGameState { Transitioning, InGame, InLobby, Generic, Disconnected }

		private static bool statusChanged;
		private static string lastStatus = "Unknown";
		private static string lastPausedState = "Unknown";
		private static GlobalGameState globalGameState = GlobalGameState.Disconnected;

		private static readonly Dictionary<string, string> prettyGameStatus = new Dictionary<string, string>()
		{
			{ "pre_match", "Pre-match" },
			{ "playing", "In Progress" },
			{ "score", "Score" },
			{ "round_start", "Round Start" },
			{ "pre_sudden_death", "Pre-Overtime" },
			{ "sudden_death", "Overtime" },
			{ "post_sudden_death", "Post-Match" },
			{ "round_over", "Post-Round" },
			{ "post_match", "Post-Match" },
			{ "Unknown", "Unknown" }
		};

		private static readonly Dictionary<string, string> prettyCombatMapName = new Dictionary<string, string>()
		{
			{ "mpl_combat_dyson", "Dyson" },
			{ "mpl_combat_combustion", "Combustion" },
			{ "mpl_combat_fission", "Fission" },
			{ "mpl_combat_gauss", "Surge" }
		};

		private static CancellationTokenSource _cts;

		public static void Start()
		{
			if (_cts != null) return;
			_cts = new CancellationTokenSource();
			Task.Run(() => DiscordLoop(_cts.Token));
		}

		public static void Stop()
		{
			_cts?.Cancel();
			_cts = null;
			DisposeDiscord();
		}

		private static async Task DiscordLoop(CancellationToken token)
		{
			InitializeDiscord();

			try
			{
				while (Program.running && !token.IsCancellationRequested)
				{
					// Use the existing LastFrame directly instead of passing it to avoid copy overhead
					ProcessDiscordPresence(Program.InGame ? Program.lastFrame : null);

					// Async delay releases the thread back to the pool
					await Task.Delay(1000, token);
				}
			}
			catch (TaskCanceledException)
			{
				// Ignore
			}
			catch (Exception e)
			{
				Logger.LogRow(LogType.Error, $"Discord RP Error: {e.Message}");
			}
		}

		private static void InitializeDiscord()
		{
			if (discordClient != null && !discordClient.IsDisposed) return;

			discordClient = new DiscordRpcClient(SecretKeys.discordRPCClientID);
			discordClient.RegisterUriScheme();
			discordClient.Logger = new ConsoleLogger { Level = LogLevel.Warning };
			discordClient.OnJoin += OnJoin;
			discordClient.OnSpectate += OnSpectate;
			discordClient.OnJoinRequested += OnJoinRequested;
			discordClient.SetSubscription(EventType.Join | EventType.Spectate | EventType.JoinRequest);
			discordClient.Initialize();
		}

		private static void OnJoin(object sender, JoinMessage args)
		{
			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = args.Secret,
					UseShellExecute = true
				});
			}
			catch { }
		}

		private static void OnJoinRequested(object sender, JoinRequestMessage args)
		{
			Program.synth.SpeakAsync(args.User.Username + " requested to join using Discord.");
		}

		private static void OnSpectate(object sender, SpectateMessage args)
		{
			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = args.Secret,
					UseShellExecute = true
				});
			}
			catch { }
		}

		private static string GetPrivateDetailsString(Frame frame, RichPresence rp)
		{
			if (frame.private_match)
			{
				rp.WithSecrets(new Secrets
				{
					JoinSecret = "spark://c/" + frame.sessionid,
					SpectateSecret = "spark://s/" + frame.sessionid,
				});
				return "Private ";
			}

			rp.WithSecrets(new Secrets
			{
				SpectateSecret = "spark://s/" + frame.sessionid,
			});
			return "Public ";
		}

		private static string GetSpectatingDetailsString(Frame frame)
		{
			// Cache local player lookup
			var player = frame.GetPlayer(frame.client_name);
			if (player != null && player.team_color == Team.TeamColor.spectator)
			{
				return "Spectating ";
			}

			return "Playing (" + frame.ClientTeamColor + ") ";
		}

		private static void RemoveRichPresence()
		{
			try
			{
				if (discordClient != null && !discordClient.IsDisposed && discordClient.CurrentPresence != null)
					discordClient.SetPresence(null);
			}
			catch { }
		}

		private static void DisposeDiscord()
		{
			if (discordClient != null && !discordClient.IsDisposed)
			{
				discordClient.Dispose();
			}
		}

		private static void ProcessDiscordPresence(Frame frame)
		{
			if (!SparkSettings.instance.discordRichPresence)
			{
				DisposeDiscord();
				return;
			}

			if (discordClient == null || discordClient.IsDisposed)
			{
				// Re-init handled by loop logic potentially, or just log error
				return;
			}

			if (frame == null && Program.connectionState == Program.ConnectionState.NotConnected)
			{
				lastStatus = lastPausedState = "Unknown";
				globalGameState = GlobalGameState.Disconnected;
				RemoveRichPresence();
				return;
			}

			RichPresence rp = new RichPresence();
			StringBuilder details = new StringBuilder(64);
			StringBuilder state = new StringBuilder(64);

			if (frame != null)
			{
				if (frame.GetPlayer(frame.client_name) == null)
				{
					RemoveRichPresence();
					return;
				}
				
				if (!string.IsNullOrEmpty(frame.teams[2].ToString()) && 
					frame.teams[2].players.Find(p => p.name == frame.client_name) != null && 
					SparkSettings.instance.discordRichPresenceSpectator)
				{
					RemoveRichPresence();
					return;
				}

				switch (frame.map_name)
				{
					case "mpl_arena_a":
					{
						globalGameState = GlobalGameState.InGame;
						details.Append("Arena ");
						details.Append(GetPrivateDetailsString(frame, rp));
						details.Append("(" + frame.teams[0].players.Count + " v " + frame.teams[1].players.Count + ")");
						details.Append(": " + frame.blue_points + " - " + frame.orange_points);
						state.Append(GetSpectatingDetailsString(frame));

						if (frame.private_match && frame.pause.paused_state != "unpaused" && frame.pause.paused_state != "paused_requested")
						{
							if (lastPausedState != frame.pause.paused_state)
							{
								lastPausedState = frame.pause.paused_state;
								initialStateTime = DateTime.UtcNow.AddSeconds(-frame.pause.paused_timer);
							}
							state.Append(" - " + char.ToUpper(frame.pause.paused_state[0]) + frame.pause.paused_state[1..]);
							rp.Timestamps = new Timestamps { Start = initialStateTime };
						}
						else
						{
							if (string.IsNullOrEmpty(frame.game_status)) frame.game_status = lastStatus;
							statusChanged = lastStatus != frame.game_status;
							lastStatus = frame.game_status;

							if (frame.game_status == "pre_match" || frame.game_status == "pre_sudden_death")
							{
								if (statusChanged)
								{
									statusChanged = false;
									initialStateTime = DateTime.UtcNow;
								}
								rp.Timestamps = new Timestamps { Start = initialStateTime };
							}
							else
							{
								rp.Timestamps = new Timestamps
								{
									End = frame.game_status == "post_match" ? DateTime.UtcNow : DateTime.UtcNow.AddSeconds(frame.game_clock)
								};
							}
							state.Append(" - " + (prettyGameStatus.ContainsKey(frame.game_status) ? prettyGameStatus[frame.game_status] : frame.game_status));
						}
						break;
					}
					case "mpl_combat_dyson":
					case "mpl_combat_combustion":
					case "mpl_combat_fission":
					case "mpl_combat_gauss":
					{
						if (globalGameState != GlobalGameState.InGame)
						{
							globalGameState = GlobalGameState.InGame;
							initialStateTime = DateTime.UtcNow;
						}
						rp.Timestamps = new Timestamps { Start = initialStateTime };
						if (prettyCombatMapName.ContainsKey(frame.map_name))
							details.Append(prettyCombatMapName[frame.map_name] + " ");
						details.Append(GetPrivateDetailsString(frame, rp));
						details.Append("(" + frame.teams[0].players.Count + " v " + frame.teams[1].players.Count + ")");
						state.Append(GetSpectatingDetailsString(frame));
						break;
					}
					default:
						lastStatus = lastPausedState = "Unknown";
						if (globalGameState != GlobalGameState.Generic)
						{
							globalGameState = GlobalGameState.Generic;
							initialStateTime = DateTime.UtcNow;
						}
						rp.Timestamps = new Timestamps { Start = initialStateTime };
						break;
				}

				if (state.Length > 0) rp.State = state.ToString();

				if (globalGameState != GlobalGameState.Generic)
				{
					rp.WithParty(new Party
					{
						ID = frame.sessionid,
						Size = frame.GetAllPlayers().Count,
						Max = frame.private_match ? 15 : 8
					});
				}
			}
			else
			{
				switch (Program.connectionState)
				{
					case Program.ConnectionState.InLobby:
					{
						lastStatus = lastPausedState = "Unknown";
						details.Append("in EchoVR Lobby");
						if (globalGameState != GlobalGameState.InLobby)
						{
							globalGameState = GlobalGameState.InLobby;
							initialStateTime = DateTime.UtcNow;
						}
						rp.Timestamps = new Timestamps { Start = initialStateTime };
						break;
					}
					case Program.ConnectionState.Menu:
					{
						if (globalGameState == GlobalGameState.Generic) return;
						lastStatus = lastPausedState = "Unknown";
						details.Append("In Transition");
						if (globalGameState != GlobalGameState.Transitioning)
						{
							globalGameState = GlobalGameState.Transitioning;
							initialStateTime = DateTime.UtcNow;
						}
						rp.Timestamps = new Timestamps { Start = initialStateTime };
						break;
					}
					case Program.ConnectionState.NoAPI:
					{
						lastStatus = lastPausedState = "Unknown";
						if (globalGameState != GlobalGameState.Generic)
						{
							globalGameState = GlobalGameState.Generic;
							initialStateTime = DateTime.UtcNow;
						}
						rp.Timestamps = new Timestamps { Start = initialStateTime };
						break;
					}
				}
			}

			if (details.Length > 0) rp.Details = details.ToString();

			rp.Assets = new Assets
			{
				LargeImageKey = "echo_arena_store_icon",
				LargeImageText = SparkSettings.instance.discordRichPresenceServerLocation &&
				                 !string.IsNullOrEmpty(Program.CurrentRound?.serverLocation)
					? Program.CurrentRound.serverLocation
					: Resources.Rich_presence_from_Spark
			};

			discordClient.SetPresence(rp);
		}
	}
}