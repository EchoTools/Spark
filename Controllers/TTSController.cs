using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
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

		private bool playing = true;
		private readonly Thread ttsThread;
		private readonly Queue<DateTime> rateLimiterQueue = new Queue<DateTime>();
		private const float rateLimitPerSecond = 15;
		private bool ttsDisabled = false;
		private string[] blacklistedNames = Array.Empty<string>();
		private readonly ConcurrentQueue<string> ttsQueue = new ConcurrentQueue<string>();

		public static string CacheFolder => Path.Combine(Path.GetTempPath(), "SparkTTSCache");
		
		private readonly Stopwatch lastRulesChangedTimer = Stopwatch.StartNew();
		
		private float currentRate = 1.0f;
		private string currentRateString = "1.0";

		public TTSController()
		{
			LoadTtsSpeed();
			
			ttsThread = new Thread(TTSThread);
			ttsThread.IsBackground = true;
			ttsThread.Start();

			Task.Run(async () =>
			{
				string blacklistFilename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgniteVR", "Spark", "tts_blacklist.txt");
				if (File.Exists(blacklistFilename))
				{
					blacklistedNames = await File.ReadAllLinesAsync(blacklistFilename);
				}
			});

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
					SpeakAsync($"{frame.last_throw.total_speed:N1}");
				}
			};
			Program.BigBoost += (frame, team, player, speed, howLongAgo) =>
			{
				if (SparkSettings.instance.maxBoostSpeedTTS && player.name == frame.client_name)
				{
					SpeakAsync($"{speed:N0} {Resources.tts_meters_per_second}");
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
				if (SparkSettings.instance.joustTimeTTS && !SparkSettings.instance.joustSpeedTTS)
				{
					SpeakAsync($"{team.color} {joustTime:N1}");
				}
				else if (!SparkSettings.instance.joustTimeTTS && SparkSettings.instance.joustSpeedTTS)
				{
					SpeakAsync($"{team.color} {maxSpeed:N0} {Resources.tts_meters_per_second}");
				}
				else if (SparkSettings.instance.joustTimeTTS && SparkSettings.instance.joustSpeedTTS)
				{
					SpeakAsync($"{team.color} {joustTime:N1} {maxSpeed:N0} {Resources.tts_meters_per_second}");
				}
			};
			Program.Goal += (frame, goalEvent) =>
			{
				if (SparkSettings.instance.goalDistanceTTS && SparkSettings.instance.goalSpeedTTS)
				{
					SpeakAsync($"{frame.last_score.distance_thrown:N1} {Resources.tts_meters}. {frame.last_score.disc_speed:N1} {Resources.tts_meters_per_second}");
				}
				else if (SparkSettings.instance.goalDistanceTTS)
				{
					SpeakAsync($"{frame.last_score.distance_thrown:N1} {Resources.tts_meters}");
				}
				else if (SparkSettings.instance.goalSpeedTTS)
				{
					SpeakAsync($"{frame.last_score.disc_speed:N1} {Resources.tts_meters_per_second}");
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
		}

		~TTSController()
		{
			ttsThread?.Abort();
		}

		private void LoadTtsSpeed()
		{
			try
			{
				int savedValue = 2;
				
				if (SparkSettings.instance.TTSSpeed >= 1 && SparkSettings.instance.TTSSpeed <= 4)
				{
					savedValue = SparkSettings.instance.TTSSpeed;
				}
				else if (SparkSettings.instance.ttsSpeedIndex >= 1 && SparkSettings.instance.ttsSpeedIndex <= 4)
				{
					savedValue = SparkSettings.instance.ttsSpeedIndex;
				}
				
				int index = savedValue - 1;
				
				if (index < 0) index = 1;
				if (index > 3) index = 1;
				
				SetRateInternal(index);
			}
			catch
			{
				SetRateInternal(1);
			}
		}

		private void TTSThread()
		{
			MediaPlayer mediaPlayer = new MediaPlayer();
			mediaPlayer.MediaEnded += (sender, e) =>
			{
				playing = false;
			};
			
			while (Program.running)
			{
				if (ttsQueue.TryDequeue(out string result))
				{
					try
					{
						mediaPlayer.Stop();
						mediaPlayer.Open(new Uri(result));
						playing = true;
						mediaPlayer.Play();
						Thread.Sleep(100);
						Task.Run(TrimCacheFolder);
					}
					catch
					{
						// Ignore playback errors
					}
				}
				else
				{
					Thread.Sleep(50);
				}
			}
		}

		public float Rate => currentRate;

		public void SetRate(int speedIndex)
		{
			if (speedIndex < 0) speedIndex = 1;
			if (speedIndex > 3) speedIndex = 1;
			
			int value = speedIndex + 1;
			
			SparkSettings.instance.TTSSpeed = value;
			SparkSettings.instance.ttsSpeedIndex = value;
			
			try
			{
				SparkSettings.instance.Save();
			}
			catch
			{
				// Ignore save errors
			}
			
			SetRateInternal(speedIndex);
		}
		
		private void SetRateInternal(int speedIndex)
		{
			switch (speedIndex)
			{
				case 0:
					currentRate = 0.6f;
					break;
				case 1:
					currentRate = 1.0f;
					break;
				case 2:
					currentRate = 1.4f;
					break;
				case 3:
					currentRate = 1.8f;
					break;
				default:
					currentRate = 1.0f;
					break;
			}
			
			currentRateString = currentRate.ToString("F1");
		}

		public void SetOutputToDefaultAudioDevice()
		{
		}

		public void SpeakAsync(string text)
		{
			Task.Run(() => Speak(text));
		}

		private void Speak(string text)
		{
			rateLimiterQueue.Enqueue(DateTime.UtcNow);
			
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

			if (ttsDisabled) return;

			if (!Directory.Exists(CacheFolder))
			{
				Directory.CreateDirectory(CacheFolder);
			}

			string cleanText = text;
			foreach (char c in Path.GetInvalidFileNameChars())
			{
				cleanText = cleanText.Replace(c.ToString(), "");
			}
			cleanText = cleanText.Replace(" ", "_");
			
			if (cleanText.Length > 100)
			{
				cleanText = cleanText.Substring(0, 100);
			}
			
			string filePath = Path.Combine(CacheFolder, $"{currentRateString}_{SparkSettings.instance.languageIndex}_{SparkSettings.instance.useWavenetVoices}_{SparkSettings.instance.ttsVoice}_{cleanText}.mp3");

			if (File.Exists(filePath))
			{
				ttsQueue.Enqueue(filePath);
				return;
			}
			
			Task.Run(async () =>
			{
				try
				{
					string json = JsonConvert.SerializeObject(new Dictionary<string, object>
					{
						{"text", text},
						{"language_code", voiceTypes[SparkSettings.instance.useWavenetVoices ? 0 : 1, SparkSettings.instance.languageIndex, SparkSettings.instance.ttsVoice]},
						{"voice_name", voiceTypes[SparkSettings.instance.useWavenetVoices ? 0 : 1, SparkSettings.instance.languageIndex, SparkSettings.instance.ttsVoice]},
						{"speaking_rate", currentRate},
					});
					
					HttpRequestMessage request = new HttpRequestMessage
					{
						Method = HttpMethod.Post,
						RequestUri = new Uri($"{Program.APIURL}/tts"),
						Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
					};
					
					HttpResponseMessage response = await FetchUtils.client.SendAsync(request);
					byte[] bytes = await response.Content.ReadAsByteArrayAsync();
				
					if (bytes.Length > 0)
					{
						await File.WriteAllBytesAsync(filePath, bytes);
					}

					ttsQueue.Enqueue(filePath);
				}
				catch
				{
					// Ignore TTS generation errors
				}
			});
		}

		public static void ClearCacheFolder()
		{
			try
			{
				if (Directory.Exists(CacheFolder))
				{
					DirectoryInfo di = new DirectoryInfo(CacheFolder);
					foreach (FileInfo file in di.GetFiles())
					{
						try 
						{ 
							file.Delete();
						} 
						catch { }
					}
				}
			}
			catch
			{
				// Ignore cache clearing errors
			}
		}

		public static void TrimCacheFolder()
		{
			try
			{
				if (Directory.Exists(CacheFolder))
				{
					DirectoryInfo di = new DirectoryInfo(CacheFolder);
					List<FileInfo> files = di.GetFiles().ToList();
					
					files.Sort((f1, f2) => f1.LastAccessTime.CompareTo(f2.LastAccessTime));
					
					long totalSize = files.Sum(file => file.Length);
					long maxSize = SparkSettings.instance.ttsCacheSizeBytes;
					
					while (totalSize > maxSize && files.Count > 0)
					{
						FileInfo oldestFile = files[0];
						try
						{
							totalSize -= oldestFile.Length;
							oldestFile.Delete();
							files.RemoveAt(0);
						}
						catch
						{
							files.RemoveAt(0);
						}
					}
				}
			}
			catch
			{
				// Ignore cache trimming errors
			}
		}
	}
}
