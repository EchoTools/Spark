using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Speech.Synthesis;
using Newtonsoft.Json;
using Spark.Properties;

namespace Spark
{
	public class TTSController
	{
		private readonly string[,,] voiceTypes =
		{
			{ { "en-US-Wavenet-D", "en-US-Wavenet-C" }, { "ja-JP-Wavenet-D", "ja-JP-Wavenet-B" } },
			{ { "en-US-Standard-D", "en-US-Standard-C" }, { "ja-JP-Standard-D", "ja-JP-Standard-B" } }
		};


		private readonly Thread ttsThread;
		private readonly Queue<DateTime> rateLimiterQueue = new Queue<DateTime>();
		private const float rateLimitPerSecond = 15;
		private bool ttsDisabled = false;
		private string[] blacklistedNames = Array.Empty<string>();
		
		// Use BlockingCollection for efficient threading (no polling/sleep loops)
		private readonly BlockingCollection<string> ttsQueue = new BlockingCollection<string>();
		private readonly SpeechSynthesizer synth;

		public static string CacheFolder {
			get {
				string customPath = SparkSettings.instance?.ttsCacheFolder;
				if (!string.IsNullOrWhiteSpace(customPath))
				{
					return customPath;
				}
				
				// Default fallback to Spark application directory
				return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SparkTTSCache");
			}
		}
		
		private readonly Stopwatch lastRulesChangedTimer = Stopwatch.StartNew();
		
		private float currentRate = 1.0f;
		private string currentRateString = "1.0";
		private static readonly Random _rng = new Random();

		public TTSController()
		{
			synth = new SpeechSynthesizer();
			SetOutputToDefaultAudioDevice();
			LoadTtsSpeed();
			
			ttsThread = new Thread(TTSThread);
			ttsThread.IsBackground = true;
			ttsThread.SetApartmentState(ApartmentState.STA); // Ensure STA for MediaPlayer
			ttsThread.Start();

			Task.Run(async () =>
			{
				string blacklistFilename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgniteVR", "Spark", "tts_blacklist.txt");
				if (File.Exists(blacklistFilename))
				{
					blacklistedNames = await File.ReadAllLinesAsync(blacklistFilename);
				}
			});

			RegisterEvents();
		}

		private void RegisterEvents()
		{
			Program.PlayerJoined += (frame, team, player) =>
			{
				if (!SparkSettings.instance.playerJoinTTS) return;
				if (blacklistedNames.Contains(player.name)) return;
				SpeakAsync($"{player.name} {Resources.tts_join_1} {team.color} {Resources.tts_join_2}");
			};
			Program.PlayerLeft += (frame, team, player) =>
			{
				if (!SparkSettings.instance.playerLeaveTTS) return;
				if (blacklistedNames.Contains(player.name)) return;
				SpeakAsync($"{player.name} {Resources.tts_leave_1} {team.color} {Resources.tts_leave_2}");
			};
			Program.PlayerSwitchedTeams += (frame, fromTeam, toTeam, player) =>
			{
				if (!SparkSettings.instance.playerSwitchTeamTTS) return;
				if (blacklistedNames.Contains(player.name)) return;

				if (fromTeam != null)
				{
					SpeakAsync($"{player.name} {Resources.tts_switch_1} {fromTeam.color} {Resources.tts_switch_2} {toTeam.color} {Resources.tts_switch_3}");
				}
				else
				{
					SpeakAsync($"{player.name} {Resources.tts_switch_alt_1} {toTeam.color} {Resources.tts_switch_alt_2}");
				}
			};
			Program.PauseRequest += (frame, player, distance) =>
			{
				if (SparkSettings.instance.pausedTTS)
				{
					SpeakAsync($"{frame.pause.paused_requested_team} {Resources.tts_pause_req}");
				}
			};
			Program.GamePaused += (frame, player, distance) =>
			{
				if (SparkSettings.instance.pausedTTS)
				{
					SpeakAsync($"{frame.pause.paused_requested_team} {Resources.tts_paused}");
				}
			};
			Program.GameUnpaused += (frame, player, distance) =>
			{
				if (SparkSettings.instance.pausedTTS)
				{
					SpeakAsync($"{frame.pause.unpaused_team} {Resources.tts_unpause}");
				}
			};
			Program.LocalThrow += (frame) =>
			{
				if (SparkSettings.instance.throwSpeedTTS && frame.last_throw.total_speed > 10)
				{
					SpeakAsync(SparkSettings.instance.ttsSpecific ? $"{frame.last_throw.total_speed:N2}" : $"{frame.last_throw.total_speed:N1}");
				}
			};
			Program.BigBoost += (frame, team, player, speed, howLongAgo) =>
			{
				if (SparkSettings.instance.maxBoostSpeedTTS && player.name == frame.client_name)
				{
					SpeakAsync(SparkSettings.instance.ttsSpecific ? $"{speed:N1} {Resources.tts_meters_per_second}" : $"{speed:N0} {Resources.tts_meters_per_second}");
				}
			};
			Program.PlayspaceAbuse += (frame, team, player, playspacePos) =>
			{
				if (SparkSettings.instance.playspaceTTS)
				{
					SpeakAsync($"{player.name} {Resources.tts_abused}");
				}
			};
			Program.Joust += (frame, team, player, isNeutral, joustTime, maxSpeed, maxTubeExitSpeed) =>
			{
				string joustTimeStr = SparkSettings.instance.ttsSpecific ? $"{joustTime:N2}" : $"{joustTime:N1}";
				string maxSpeedStr = SparkSettings.instance.ttsSpecific ? $"{maxSpeed:N1}" : $"{maxSpeed:N0}";

				if (SparkSettings.instance.joustTimeTTS && !SparkSettings.instance.joustSpeedTTS)
				{
					SpeakAsync($"{team.color} {joustTimeStr}");
				}
				else if (!SparkSettings.instance.joustTimeTTS && SparkSettings.instance.joustSpeedTTS)
				{
					SpeakAsync($"{team.color} {maxSpeedStr} {Resources.tts_meters_per_second}");
				}
				else if (SparkSettings.instance.joustTimeTTS && SparkSettings.instance.joustSpeedTTS)
				{
					SpeakAsync($"{team.color} {joustTimeStr} {maxSpeedStr} {Resources.tts_meters_per_second}");
				}
			};
			Program.Goal += (frame, goalEvent) =>
			{
				string distanceStr = SparkSettings.instance.ttsSpecific ? $"{frame.last_score.distance_thrown:N2}" : $"{frame.last_score.distance_thrown:N1}";
				string speedStr = SparkSettings.instance.ttsSpecific ? $"{frame.last_score.disc_speed:N2}" : $"{frame.last_score.disc_speed:N1}";

				if (SparkSettings.instance.goalDistanceTTS && SparkSettings.instance.goalSpeedTTS)
				{
					SpeakAsync($"{distanceStr} {Resources.tts_meters}. {speedStr} {Resources.tts_meters_per_second}");
				}
				else if (SparkSettings.instance.goalDistanceTTS)
				{
					SpeakAsync($"{distanceStr} {Resources.tts_meters}");
				}
				else if (SparkSettings.instance.goalSpeedTTS)
				{
					SpeakAsync($"{speedStr} {Resources.tts_meters_per_second}");
				}
			};
			Program.RulesChanged += frame =>
			{
				if (SparkSettings.instance.rulesChangedTTS && lastRulesChangedTimer.Elapsed.TotalSeconds > 2)
				{
					SpeakAsync($"{frame.rules_changed_by} changed the rules");
				}
				lastRulesChangedTimer.Restart();
			};
			Program.LargePing += (frame, team, player) =>
			{
				if (SparkSettings.instance.pingSpikeTTS &&
				    (!SparkSettings.instance.pingSpikeTTSPrivateOnly || frame.private_match))
				{
					SpeakAsync($"{player.name}'s ping spiked to {player.ping}");
				}
			};
		}

		~TTSController()
		{
			try
			{
				ttsQueue?.CompleteAdding();
				synth?.Dispose();
			}
			catch { }
		}

		/// <summary>
		/// The speeds on offer, as multiples of the voice's normal pace: tenths from half speed to
		/// double. That is the range the neural voices behind the Spark API document for their speaking
		/// rate, so every step sounds different online. The offline Windows voice moves one step of its
		/// own -10..10 rate per tenth, as the four speeds this replaced already did.
		/// </summary>
		public static readonly IReadOnlyList<SpeedOption> SpeedOptions = Enumerable.Range(5, 16)
			.Select(tenths => new SpeedOption(tenths / 10.0))
			.ToList();

		public sealed class SpeedOption
		{
			public SpeedOption(double rate)
			{
				Rate = rate;
			}

			public double Rate { get; }

			/// <summary>
			/// "1.4x", plus the name the old four-speed list used where this is one of those speeds, so
			/// whatever someone had picked is still easy to find.
			/// </summary>
			public string Label
			{
				get
				{
					string name = Rate switch
					{
						0.6 => Resources.Slow,
						1.0 => Resources.Normal,
						1.4 => Resources.Fast,
						1.8 => Resources.Very_Fast,
						_ => null,
					};
					string speed = $"{Rate:0.0}x";
					return name == null ? speed : $"{speed} ({name})";
				}
			}

			public override string ToString() => Label;
		}

		/// <summary>The offered speed closest to <paramref name="speed"/>, or normal speed if it isn't usable.</summary>
		public static double NearestSpeed(double speed)
		{
			if (double.IsNaN(speed) || speed <= 0) return 1.0;
			return SpeedOptions.OrderBy(option => Math.Abs(option.Rate - speed)).First().Rate;
		}

		public void LoadTtsSpeed()
		{
			try
			{
				ApplyRate(NearestSpeed(SparkSettings.instance.ttsSpeedMultiplier));
			}
			catch
			{
				ApplyRate(1.0);
			}
		}

		private void TTSThread()
		{
			MediaPlayer mediaPlayer = new MediaPlayer();
			mediaPlayer.MediaEnded += (sender, e) =>
			{
			};
			
			// Use GetConsumingEnumerable to block until item exists (CPU efficient)
			foreach (string result in ttsQueue.GetConsumingEnumerable())
			{
				if (!Program.running) break;

				try
				{
					if (result.StartsWith("OFFLINE|"))
					{
						string offlineText = result.Substring(8);
						mediaPlayer.Stop(); // Ensure any playing audio stops
						synth.SpeakAsyncCancelAll(); // Stop any currently playing offline speech
						
						try
						{
							if (SparkSettings.instance.ttsVoice == 0)
							{
								synth.SelectVoiceByHints(VoiceGender.Male);
							}
							else
							{
								synth.SelectVoiceByHints(VoiceGender.Female);
							}
						}
						catch { }
						
						synth.SpeakAsync(offlineText);
						Thread.Sleep(50);
					}
					else
					{
						synth.SpeakAsyncCancelAll(); // Stop offline speech if API audio plays
						// Ensure previous playback stops
						mediaPlayer.Stop();
						mediaPlayer.Open(new Uri(result));

						mediaPlayer.Play();
						
						// Only trim cache probabilistically to save IO
						if (_rng.Next(0, 10) == 0)
						{
							Task.Run(TrimCacheFolder);
						}
						
						// Small buffer to prevent stutter if rapid fire
						Thread.Sleep(50);
					}
				}
				catch
				{
					// Ignore playback errors
				}
			}
		}

		public float Rate => currentRate;

		/// <param name="speed">A multiple of normal speed; snapped to the nearest of <see cref="SpeedOptions"/>.</param>
		public void SetRate(double speed)
		{
			speed = NearestSpeed(speed);
			SparkSettings.instance.ttsSpeedMultiplier = speed;

			Task.Run(() =>
			{
				try
				{
					SparkSettings.instance.Save();
				}
				catch { }
			});

			ApplyRate(speed);
		}

		private void ApplyRate(double speed)
		{
			currentRate = (float)speed;
			// A step of the offline voice's -10..10 rate per tenth: the old speeds used exactly this
			// (0.6x was -4, 1.4x was 4, 1.8x was 8).
			synth.Rate = Math.Clamp((int)Math.Round((speed - 1.0) * 10), -10, 10);
			// Goes into every cached clip's filename. Invariant, and still "1.4" rather than "1.40", so
			// clips cached at the old speeds keep being found.
			currentRateString = speed.ToString("0.0#", CultureInfo.InvariantCulture);
		}

		public void SetOutputToDefaultAudioDevice()
		{
			try
			{
				synth.SetOutputToDefaultAudioDevice();
			}
			catch { }
		}

		public void SpeakAsync(string text)
		{
			// Offload all logic to thread pool immediately
			Task.Run(() => Speak(text));
		}

		private void Speak(string text)
		{
			lock(rateLimiterQueue)
			{
				rateLimiterQueue.Enqueue(DateTime.UtcNow);
				
				// Clean up old entries
				while (rateLimiterQueue.Count > 0 && 
					   (DateTime.UtcNow - rateLimiterQueue.Peek()).TotalSeconds > 1)
				{
					rateLimiterQueue.Dequeue();
				}

				if (rateLimiterQueue.Count > rateLimitPerSecond)
				{
					ttsDisabled = true;
					return;
				}
			}

			if (ttsDisabled) return;

			if (!Directory.Exists(CacheFolder))
			{
				Directory.CreateDirectory(CacheFolder);
			}

			// Clean filename more efficiently
			StringBuilder cleanTextBuilder = new StringBuilder(text.Length);
			foreach (char c in text)
			{
				if (!Path.GetInvalidFileNameChars().Contains(c))
				{
					cleanTextBuilder.Append(c);
				}
			}
			string cleanText = cleanTextBuilder.ToString().Replace(" ", "_");
			
			if (cleanText.Length > 50) // Reduced length to avoid path length issues
			{
				cleanText = cleanText.Substring(0, 50);
			}
			
			string filePath = Path.Combine(CacheFolder, $"v2_{currentRateString}_{SparkSettings.instance.languageIndex}_{SparkSettings.instance.ttsVoice}_{cleanText}.mp3");

			if (File.Exists(filePath))
			{
				ttsQueue.Add(filePath);
				return;
			}
			
			// Run network request
			try
			{
				string voiceName = SparkSettings.instance.ttsVoice == 1 ? "en-US-Wavenet-C" : "en-US-Wavenet-D";

				string json = JsonConvert.SerializeObject(new Dictionary<string, object>
				{
					{"text", text},
					{"language_code", voiceName},
					{"voice_name", voiceName},
					{"speaking_rate", currentRate},
				});
				
				HttpRequestMessage request = new HttpRequestMessage
				{
					Method = HttpMethod.Post,
					RequestUri = new Uri("https://sparkapi-production-e6df.up.railway.app/tts"),
					Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
				};
				
				// Synchronous wait here is fine because we are already in Task.Run from SpeakAsync
				// and we want to ensure the file is written before queueing
				HttpResponseMessage response = FetchUtils.client.SendAsync(request).Result;
				response.EnsureSuccessStatusCode();
				byte[] bytes = response.Content.ReadAsByteArrayAsync().Result;
			
				if (bytes.Length > 0)
				{
					File.WriteAllBytes(filePath, bytes);
					ttsQueue.Add(filePath);
				}
				else
				{
					ttsQueue.Add("OFFLINE|" + text);
				}
			}
			catch
			{
				// Ignore TTS generation errors and fallback to offline
				ttsQueue.Add("OFFLINE|" + text);
			}
		}

		public static void ClearCacheFolder()
		{
			// Disabled: No longer deletes TTS cache
		}

		public static void TrimCacheFolder()
		{
			// Disabled: No longer trims TTS cache
		}
	}
}