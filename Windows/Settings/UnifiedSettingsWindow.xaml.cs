using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows.Navigation;
using System.Windows.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using EchoVRAPI;



namespace Spark
{
	/// <summary>
	/// Interaction logic for UnifiedSettingsWindow.xaml
	/// </summary>
	public partial class UnifiedSettingsWindow
	{
		// set to false initially so that loading the settings from disk doesn't activate the events
		private bool initialized;

		/// <summary>
		/// Set to true once the opt in status fetched.
		/// </summary>
		private bool optInFound;

		/// <summary>
		/// Throttles theme updates during slider dragging to prevent UI lag.
		/// </summary>
		private readonly System.Windows.Threading.DispatcherTimer _themeDebounceTimer;


		public UnifiedSettingsWindow()
		{
			InitializeComponent();

			_themeDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
			_themeDebounceTimer.Tick += (s, ev) =>
			{
				_themeDebounceTimer.Stop();
				ThemesController.ApplyCustomTheme(_themeDark, _themeMid, _themeLight);
			};
		}

		private void WindowLoad(object sender, RoutedEventArgs e)
		{
			//Initialize();

			optInCheckbox.IsEnabled = false;
			optInStatusLabel.Content = "Fetching opt-in status...";
			_ = GetOptInStatus();


#if WINDOWS_STORE_RELEASE
			enableBetasCheckbox.Visibility = Visibility.Collapsed;
#endif


			ThisPCLocalIP.Text = $"This PC's Local IP: {QuestIPFetching.GetLocalIP()} (for PC-PC Spectate Me)";

			CameraModeDropdownChanged(SparkSettings.instance.spectatorCamera);

			if (SparkSettings.instance.mutePlayerComms)
			{
				MutePlayerCommsDropdown.SelectedIndex = 2;
			}
			else if (SparkSettings.instance.muteEnemyTeam)
			{
				MutePlayerCommsDropdown.SelectedIndex = 1;
			}
			else
			{
				MutePlayerCommsDropdown.SelectedIndex = 0;
			}

			initialized = true;
		}


		#region General

		public static bool StartWithWindows
		{
			get => SparkSettings.instance.startOnBoot;
			set
			{
				SparkSettings.instance.startOnBoot = value;

				RegistryKey rk =
					Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

				if (value)
				{
					string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Spark.exe");
					rk?.SetValue(Properties.Resources.AppName, path);
				}
				else
					rk?.DeleteValue(Properties.Resources.AppName, false);
			}
		}

		private void CloseButtonEvent(object sender, RoutedEventArgs e)
		{
			SparkSettings.instance.Save();
			Close();
		}

		private void EchoVRIPChanged(object sender, TextChangedEventArgs e)
		{
			if (!initialized) return;
			Program.echoVRIP = ((TextBox)sender).Text;
			SparkSettings.instance.echoVRIP = Program.echoVRIP;
			SaveQuestIPButton.Visibility = Visibility.Visible;
		}

		private void EchoVRPortChanged(object sender, TextChangedEventArgs e)
		{
			if (!initialized) return;
			if (Program.overrideEchoVRPort)
			{
				EchoVRPortTextBox.Text = SparkSettings.instance.echoVRPort.ToString();
			}
			else
			{
				if (int.TryParse(((TextBox)sender).Text, out Program.echoVRPort))
				{
					SparkSettings.instance.echoVRPort = Program.echoVRPort;
				}
			}
		}

		private void ResetIP_Click(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			Program.echoVRIP = "127.0.0.1";
			if (!Program.overrideEchoVRPort) Program.echoVRPort = 6721;
			EchoVRIPTextBox.Text = Program.echoVRIP;
			EchoVRPortTextBox.Text = Program.echoVRPort.ToString();
			SparkSettings.instance.echoVRIP = Program.echoVRIP;
			if (!Program.overrideEchoVRPort) SparkSettings.instance.echoVRPort = Program.echoVRPort;
			SaveQuestIPButton.Visibility = Visibility.Collapsed;
		}

		private void ExecutableLocationChanged(object sender, TextChangedEventArgs e)
		{
			if (!initialized) return;
			string path = ((TextBox)sender).Text;
			if (File.Exists(path))
			{
				ExeLocationLabel.Content = "EchoVR Executable Location:";
				SparkSettings.instance.echoVRPath = path;
			}
			else
			{
				ExeLocationLabel.Content = "EchoVR Executable Location:   (not valid)";
			}
		}

		private void OpenQuestSpectatorPopup(object sender, RoutedEventArgs e)
		{
			if (!string.IsNullOrEmpty(SparkSettings.instance.followPlayerName))
			{
				TargetNameInput.Text = SparkSettings.instance.followPlayerName;
			}
			SpectatorNameInput.Text = SparkSettings.instance.client_name ?? "";
			
			AutoJoinCheckbox.IsChecked = SparkSettings.instance.questSpectatorAutoJoin;
			
			QuestSpectatorPopup.Visibility = Visibility.Visible;
		}

		private void CloseSpectatorPopup(object sender, RoutedEventArgs e)
		{
			QuestSpectatorPopup.Visibility = Visibility.Collapsed;
		}

private void LaunchQuestSpectator(object sender, RoutedEventArgs e)
		{
			string spectatorName = SpectatorNameInput.Text.Trim();
			string targetName = TargetNameInput.Text.Trim();

			if (string.IsNullOrEmpty(targetName))
			{
				new Spark.MessageBox("Please enter the name of the player you want to spectate.", Properties.Resources.Error).Show();
				return;
			}

			// 1. Save Settings
			SparkSettings.instance.followPlayerName = targetName;
			SparkSettings.instance.client_name = spectatorName;
			SparkSettings.instance.questSpectatorAutoJoin = AutoJoinCheckbox.IsChecked == true;
			SparkSettings.instance.Save();

			// 2. Set Camera Mode to 'Follow specific player' (Index 3)
			SparkSettings.instance.spectatorCamera = 3;
			CameraModeDropdownChanged(3);

			// 3. Hide Popup
			QuestSpectatorPopup.Visibility = Visibility.Collapsed;

			// 4. Async Launch & Monitor
			Task.Run(async () =>
			{
				// --- PHASE 1: INITIAL LAUNCH ---
				// Try to find them immediately to generate Launch Arguments
				string initialLobbyId = "";
				Logger.LogRow(Logger.LogType.Info, $"Quest Spectator: Initial check for '{targetName}'...");
				
				try 
				{
					using (WebClient client = new WebClient()) 
					{
						string json = await client.DownloadStringTaskAsync("https://g.echovrce.com/status/matches");
						JObject data = JObject.Parse(json);
						JArray labels = (JArray)data["labels"];
						if (labels != null) 
						{
							foreach (var server in labels) 
							{
								var players = server["players"] as JArray;
								if (players != null && players.Any(p => string.Equals((string)p["display_name"], targetName, StringComparison.OrdinalIgnoreCase))) 
								{
									initialLobbyId = (string)server["id"];
									break;
								}
							}
						}
					}
				} 
				catch (Exception) { /* Ignore initial error */ }

				string args = "-spectatorstream";
				if (SparkSettings.instance.spectatorStreamNoOVR) args += " -noovr";
				if (!string.IsNullOrEmpty(initialLobbyId)) args += $" -lobbyid {initialLobbyId}";

				if (File.Exists(SparkSettings.instance.echoVRPath)) 
				{
					try 
					{ 
						Process.Start(SparkSettings.instance.echoVRPath, args); 
					}
					catch (Exception ex) 
					{ 
						Logger.LogRow(Logger.LogType.Error, $"Error launching Echo VR: {ex.Message}"); 
						return; 
					}
				} 
				else 
				{
					Logger.LogRow(Logger.LogType.Error, "Echo VR executable not found.");
					return;
				}

				// --- PHASE 2: MONITOR LOOP ---
				if (SparkSettings.instance.questSpectatorAutoJoin)
				{
					string lastAttemptedSessionId = initialLobbyId;

					using (WebClient client = new WebClient())
					{
						while (Program.running)
						{
							try
							{
								// STEP 1: Check Local State (Are we already with the player?)
								bool targetInLocalMatch = false;
								if (Program.lastFrame != null && !string.IsNullOrEmpty(Program.lastFrame.sessionid))
								{
									// Check if the target is in our current player list
									var allPlayers = Program.lastFrame.GetAllPlayers();
									if (allPlayers.Any(p => string.Equals(p.name, targetName, StringComparison.OrdinalIgnoreCase)))
									{
										targetInLocalMatch = true;
									}
								}

								// IF FOUND LOCALLY: Do nothing, just wait. We are where we want to be.
								if (targetInLocalMatch)
								{
									// Clear the last attempted ID so if they leave and rejoin the same server, we can follow.
									lastAttemptedSessionId = ""; 
									await Task.Delay(3000);
									continue; 
								}

								// STEP 2: Target NOT in local match. Search API.
								// We only query the API if the player is missing locally.
								
								string json = await client.DownloadStringTaskAsync("https://g.echovrce.com/status/matches");
								JObject data = JObject.Parse(json);
								JArray labels = (JArray)data["labels"];
								
								if (labels != null)
								{
									foreach (var server in labels)
									{
										var players = server["players"] as JArray;
										if (players != null)
										{
											bool targetHere = false;
											bool spectatorHere = false;

											foreach (var p in players)
											{
												string pName = (string)p["display_name"];
												if (string.Equals(pName, targetName, StringComparison.OrdinalIgnoreCase)) targetHere = true;
												if (string.Equals(pName, spectatorName, StringComparison.OrdinalIgnoreCase)) spectatorHere = true;
											}

											if (targetHere)
											{
												string serverId = (string)server["id"];

												// JOIN CRITERIA:
												// 1. We aren't already trying to join this specific server (Loop protection)
												// 2. The API doesn't see us inside that server already (Safeguard)
												// 3. Our local game isn't already in that session (Double check)
												
												if (serverId != lastAttemptedSessionId && !spectatorHere)
												{
													if (Program.lastFrame == null || Program.lastFrame.sessionid != serverId)
													{
														Logger.LogRow(Logger.LogType.Info, $"Quest Spectator: Found {targetName} in {serverId}. Joining...");
														
														lastAttemptedSessionId = serverId;
														await Program.APIJoin(serverId);

														// Wait for join to process
														await Task.Delay(5000);
														
														// Force Camera Lock
														Application.Current.Dispatcher.Invoke(() => {
															CameraWriteController.UseCameraControlKeys();
														});
													}
												}
												break; // Found them, stop checking other servers
											}
										}
									}
								}
								// If target not found in API, we simply loop again. We stay in the current match (if any).
							}
							catch (Exception ex)
							{
								Console.WriteLine($"Quest Spectator Loop Error: {ex.Message}");
							}

							await Task.Delay(3000);
						}
					}
				}
			});
		}
		
		private async void FindQuestClick(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			FindQuestStatusLabel.Content = Properties.Resources.Searching_for_Quest_on_network;
			FindQuestStatusLabel.Visibility = Visibility.Visible;
			EchoVRIPTextBox.IsEnabled = false;
			EchoVRPortTextBox.IsEnabled = false;
			FindQuest.IsEnabled = false;
			ResetIP.IsEnabled = false;
			SaveQuestIPButton.Visibility = Visibility.Collapsed;

			Progress<string> progress = new Progress<string>(s => FindQuestStatusLabel.Content = s);
			await Task.Factory.StartNew(() => Program.echoVRIP = QuestIPFetching.FindQuestIP(progress), TaskCreationOptions.None);

			// if we failed with this method, scan the network with API requests instead
			if ((string)FindQuestStatusLabel.Content == Properties.Resources.Failed_to_find_Quest_on_network_)
			{
				FindQuestStatusLabel.Content = Properties.Resources.Failed__Scanning_for_Echo_VR_API_instead__This_may_take_a_while_;
				await Task.Run(async () =>
				{
					Progress<float> searchProgress = new Progress<float>();
					searchProgress.ProgressChanged += (o, val) => { Dispatcher.Invoke(() => { FindQuestStatusLabel.Content = $"{Properties.Resources.Failed__Scanning_for_Echo_VR_API_instead__This_may_take_a_while_}\t{val:P0}"; }); };
					List<IPAddress> ips = QuestIPFetching.GetPossibleLocalIPs();
					List<(IPAddress, string)> responses = await QuestIPFetching.PingEchoVRAPIAsync(ips, 20, searchProgress);
					IPAddress found = responses.FirstOrDefault(r => r.Item2 != null).Item1;
					Dispatcher.Invoke(() =>
					{
						if (found != null)
						{
							Program.echoVRIP = found.ToString();

							FindQuestStatusLabel.Content = Properties.Resources.Found_Quest_on_network_;
						}
						else
						{
							FindQuestStatusLabel.Content = Properties.Resources.Failed_to_find_Quest_on_network__Make_sure_you_are_in_a_private_public_match_and_API_Access_is_enabled_;
						}
					});
				});
			}

			EchoVRIPTextBox.IsEnabled = true;
			EchoVRPortTextBox.IsEnabled = true;
			FindQuest.IsEnabled = true;
			ResetIP.IsEnabled = true;
			if (!Program.overrideEchoVRPort) Program.echoVRPort = 6721;
			EchoVRIPTextBox.Text = Program.echoVRIP;
			EchoVRPortTextBox.Text = Program.echoVRPort.ToString();
			SparkSettings.instance.echoVRIP = Program.echoVRIP;
			if (!Program.overrideEchoVRPort) SparkSettings.instance.echoVRPort = Program.echoVRPort;
			SaveQuestIPButton.Visibility = Visibility.Collapsed;
		}

		private void ShowFirstTimeSetupWindowClicked(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;

			Program.ToggleWindow(typeof(FirstTimeSetupWindow));
		}

		public static Visibility FirestoreVisible => !DiscordOAuth.Personal ? Visibility.Visible : Visibility.Collapsed;

		public static string ReplayFilename => string.IsNullOrEmpty(Program.replayFilesManager.fileName) ? "---" : Program.replayFilesManager.fileName;

		private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
		{
			try
			{
				Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
				e.Handled = true;
			}
			catch (Exception ex)
			{
				Logger.LogRow(Logger.LogType.Error, ex.ToString());
			}
		}

		public int SetTheme
		{
			get => SparkSettings.instance.theme;
			set
			{
				SparkSettings.instance.theme = value;

				ThemesController.SetTheme((ThemesController.ThemeTypes)value);
			}
		}

		private async Task GetOptInStatus()
		{
			if (string.IsNullOrEmpty(SparkSettings.instance.client_name))
			{
				optInFound = true;
				optInCheckbox.IsEnabled = false;
				optInStatusLabel.Content = "Run the game once to find your Oculus name.";
				return;
			}

			if (DiscordOAuth.oauthToken == string.Empty)
			{
				optInFound = true;
				optInCheckbox.IsEnabled = false;
				optInStatusLabel.Content = "Log into Discord to be able to opt in.";
				return;
			}

			try
			{
				string resp = await FetchUtils.GetRequestAsync(
					$"{Program.APIURL}/optin/get/{SparkSettings.instance.client_name}",
					new Dictionary<string, string> { { "x-api-key", DiscordOAuth.igniteUploadKey } });

				JToken objResp = JsonConvert.DeserializeObject<JToken>(resp);
				if (objResp?["opted_in"] != null)
				{
					optInCheckbox.IsChecked = (bool)objResp["opted_in"];
				}
				else
				{
					Logger.LogRow(Logger.LogType.Error, $"Couldn't get opt-in status.");
					optInStatusLabel.Content = "Failed to get opt-in status. Response invalid.";
				}
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, $"Couldn't get opt-in status.\n{e}");
				optInStatusLabel.Content = "Failed to get opt-in status.";
			}

			optInFound = true;
			optInCheckbox.IsEnabled = true;
			optInStatusLabel.Content = $"Oculus Username: {SparkSettings.instance.client_name}";
		}

		private void OptIn(object sender, RoutedEventArgs e)
		{
			if (!optInFound) return;

			FetchUtils.PostRequestCallback(
				$"{Program.APIURL}/optin/set/{SparkSettings.instance.client_name}/{((CheckBox)sender).IsChecked}",
				new Dictionary<string, string>
				{
					{ "x-api-key", DiscordOAuth.igniteUploadKey }, { "token", DiscordOAuth.oauthToken }
				},
				string.Empty,
				(resp) =>
				{
					if (resp.Contains("opted in"))
					{
						optInCheckbox.IsChecked = true;
					}
					else if (resp.Contains("opted normal"))
					{
						optInCheckbox.IsChecked = false;
					}
				});
		}

		#endregion

		#region Replays

		private void OpenReplayFolder(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			Process.Start(new ProcessStartInfo
			{
				FileName = SparkSettings.instance.saveFolder,
				UseShellExecute = true
			});
		}

		private void ResetReplayFolder(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			SparkSettings.instance.saveFolder =
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Spark", "replays");
			Directory.CreateDirectory(SparkSettings.instance.saveFolder);
			storageLocationTextBox.Text = SparkSettings.instance.saveFolder;
		}

		private void SetStorageLocation(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			string selectedPath = "";
			CommonOpenFileDialog folderBrowserDialog = new CommonOpenFileDialog
			{
				InitialDirectory = SparkSettings.instance.saveFolder,
				IsFolderPicker = true
			};
			if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
			{
				selectedPath = folderBrowserDialog.FileName;
			}

			if (selectedPath != "")
			{
				SetStorageLocation(selectedPath);
				Console.WriteLine(selectedPath);
			}
		}

		private void SetStorageLocation(string path)
		{
			SparkSettings.instance.saveFolder = path;
			storageLocationTextBox.Text = SparkSettings.instance.saveFolder;
		}

		private void SplitFileButtonClicked(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			Program.replayFilesManager.Split();
		}

		#endregion

		#region TTS

		public static bool DiscordLoggedIn => DiscordOAuth.IsLoggedIn;

		public static Visibility DiscordNotLoggedInVisible =>
			DiscordOAuth.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;

		public static bool JoustTime
		{
			get => SparkSettings.instance.joustTimeTTS;
			set
			{
				SparkSettings.instance.joustTimeTTS = value;

				if (value) Program.synth.SpeakAsync($"{Team.TeamColor.orange.ToLocalizedString()} 1.8");
				Console.WriteLine($"{Team.TeamColor.orange.ToLocalizedString()} 1.8");
			}
		}

		public static bool JoustSpeed
		{
			get => SparkSettings.instance.joustSpeedTTS;
			set
			{
				SparkSettings.instance.joustSpeedTTS = value;

				if (value)
					Program.synth.SpeakAsync(
						$"{Team.TeamColor.orange.ToLocalizedString()} 32 {Properties.Resources.tts_meters_per_second}");
			}
		}

		public static bool ServerLocation
		{
			get => SparkSettings.instance.serverLocationTTS;
			set
			{
				SparkSettings.instance.serverLocationTTS = value;

				if (value) Program.synth.SpeakAsync("Chicago, Illinois");
			}
		}

		public static bool MaxBoostSpeed
		{
			get => SparkSettings.instance.maxBoostSpeedTTS;
			set
			{
				SparkSettings.instance.maxBoostSpeedTTS = value;

				if (value) Program.synth.SpeakAsync($"32 {Properties.Resources.tts_meters_per_second}");
			}
		}

		public static bool TubeExitSpeed
		{
			get => SparkSettings.instance.tubeExitSpeedTTS;
			set
			{
				SparkSettings.instance.tubeExitSpeedTTS = value;

				if (value) Program.synth.SpeakAsync($"32 {Properties.Resources.tts_meters_per_second}");
			}
		}

		public static int SpeechSpeed
		{
			get => SparkSettings.instance.TTSSpeed;
			set
			{
				Program.synth.SetRate(value);

				if (value != SparkSettings.instance.TTSSpeed)
					Program.synth.SpeakAsync(Properties.Resources.This_is_the_new_speed);

				SparkSettings.instance.TTSSpeed = value;
			}
		}

		public static bool GamePaused
		{
			get => SparkSettings.instance.pausedTTS;
			set
			{
				SparkSettings.instance.pausedTTS = value;

				if (value)
					Program.synth.SpeakAsync(
						$"{Team.TeamColor.orange.ToLocalizedString()} {Properties.Resources.tts_paused}");
			}
		}

		public static bool PlayerJoin
		{
			get => SparkSettings.instance.playerJoinTTS;
			set
			{
				SparkSettings.instance.playerJoinTTS = value;

				if (value)
					Program.synth.SpeakAsync(
						$"NtsFranz {Properties.Resources.tts_join_1} {Team.TeamColor.orange.ToLocalizedString()} {Properties.Resources.tts_join_2}");
			}
		}

		public static bool PlayerLeave
		{
			get => SparkSettings.instance.playerLeaveTTS;
			set
			{
				SparkSettings.instance.playerLeaveTTS = value;

				if (value)
					Program.synth.SpeakAsync(
						$"NtsFranz {Properties.Resources.tts_leave_1} {Team.TeamColor.orange.ToLocalizedString()} {Properties.Resources.tts_leave_2}");
			}
		}

		public static bool PlayerSwitch
		{
			get => SparkSettings.instance.playerSwitchTeamTTS;
			set
			{
				SparkSettings.instance.playerSwitchTeamTTS = value;

				if (value)
					Program.synth.SpeakAsync(
						$"NtsFranz {Properties.Resources.tts_switch_1} {Team.TeamColor.blue.ToLocalizedString()} {Properties.Resources.tts_switch_2} {Team.TeamColor.orange.ToLocalizedString()} {Properties.Resources.tts_switch_3}");
			}
		}

		public static bool ThrowSpeed
		{
			get => SparkSettings.instance.throwSpeedTTS;
			set
			{
				SparkSettings.instance.throwSpeedTTS = value;

				if (value) Program.synth.SpeakAsync("19");
			}
		}

		public static bool GoalSpeed
		{
			get => SparkSettings.instance.goalSpeedTTS;
			set
			{
				SparkSettings.instance.goalSpeedTTS = value;

				if (value) Program.synth.SpeakAsync($"19 {Properties.Resources.tts_meters_per_second}");
			}
		}

		public static bool GoalDistance
		{
			get => SparkSettings.instance.goalDistanceTTS;
			set
			{
				SparkSettings.instance.goalDistanceTTS = value;

				if (value) Program.synth.SpeakAsync($"23 {Properties.Resources.tts_meters}");
			}
		}


		public static bool RulesChanged
		{
			get => SparkSettings.instance.rulesChangedTTS;
			set
			{
				SparkSettings.instance.rulesChangedTTS = value;

				if (value)
				{
					Program.synth.SpeakAsync($"NtsFranz changed the rules");
				}
			}
		}

		#endregion


		#region EchoVR Settings

		public Visibility EchoVRSettingsProgramOpenWarning =>
			Program.GetEchoVRProcess()?.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		public Visibility EchoVRInstalled =>
			File.Exists(SparkSettings.instance.echoVRPath) ? Visibility.Visible : Visibility.Collapsed;

		public Visibility EchoVRNotInstalled => !File.Exists(SparkSettings.instance.echoVRPath)
			? Visibility.Visible
			: Visibility.Collapsed;

		public bool Fullscreen
		{
			get => EchoVRSettingsManager.Fullscreen;
			set => EchoVRSettingsManager.Fullscreen = value;
		}

		public bool MultiResShading
		{
			get => EchoVRSettingsManager.MultiResShading;
			set => EchoVRSettingsManager.MultiResShading = value;
		}

		public bool AutoRes
		{
			get => EchoVRSettingsManager.AutoRes;
			set => EchoVRSettingsManager.AutoRes = value;
		}

		public bool TemporalAA
		{
			get => EchoVRSettingsManager.TemporalAA;
			set => EchoVRSettingsManager.TemporalAA = value;
		}

		public bool Volumetrics
		{
			get => EchoVRSettingsManager.Volumetrics;
			set => EchoVRSettingsManager.Volumetrics = value;
		}

		public bool Bloom
		{
			get => EchoVRSettingsManager.Bloom;
			set => EchoVRSettingsManager.Bloom = value;
		}

		public string Monitor
		{
			get => EchoVRSettingsManager.Monitor.ToString();
			set => EchoVRSettingsManager.Monitor = int.Parse(value);
		}

		public string Resolution
		{
			get => EchoVRSettingsManager.Resolution.ToString();
			set => EchoVRSettingsManager.Resolution = float.Parse(value);
		}

		public string FoV
		{
			get => EchoVRSettingsManager.FoV.ToString();
			set => EchoVRSettingsManager.FoV = float.Parse(value);
		}

		public string Sharpening
		{
			get => EchoVRSettingsManager.Sharpening.ToString();
			set => EchoVRSettingsManager.Sharpening = float.Parse(value);
		}

		public int AA
		{
			get => EchoVRSettingsManager.AA;
			set => EchoVRSettingsManager.AA = value;
		}

		public int ShadowQuality
		{
			get => EchoVRSettingsManager.ShadowQuality;
			set => EchoVRSettingsManager.ShadowQuality = value;
		}

		public int MeshQuality
		{
			get => EchoVRSettingsManager.MeshQuality;
			set => EchoVRSettingsManager.MeshQuality = value;
		}

		public int FXQuality
		{
			get => EchoVRSettingsManager.FXQuality;
			set => EchoVRSettingsManager.FXQuality = value;
		}

		public int TextureQuality
		{
			get => EchoVRSettingsManager.TextureQuality;
			set => EchoVRSettingsManager.TextureQuality = value;
		}

		public int LightingQuality
		{
			get => EchoVRSettingsManager.LightingQuality;
			set => EchoVRSettingsManager.LightingQuality = value;
		}


		private void RefreshAllSettings(object sender, SelectionChangedEventArgs e)
		{
			if (initialized)
			{
				DataContext = null;
				DataContext = this;
			}
		}

		#endregion


		public static string AppVersionLabelText => $"v{Program.AppVersionString()}";


		private void CameraModeDropdownChanged(object sender, SelectionChangedEventArgs e)
		{
			if (!initialized) return;

			// setting already handled in binding
			ComboBox box = (ComboBox)sender;
			CameraModeDropdownChanged(box.SelectedIndex);
		}

		private void CameraModeDropdownChanged(int index)
		{
			switch (index)
			{
				case 0:
				case 1:
					followSpecificPlayerPanel.Visibility = Visibility.Collapsed;
					followCameraModeLabel.Visibility = Visibility.Collapsed;
					followCameraModeDropdown.Visibility = Visibility.Collapsed;
					discholderFollowTeamCheckbox.Visibility = Visibility.Collapsed;
					discholderFollowCameraModeLabel.Visibility = Visibility.Collapsed;
					discholderFollowCameraModeDropdown.Visibility = Visibility.Collapsed;
					break;
				case 2:
					followSpecificPlayerPanel.Visibility = Visibility.Collapsed;
					followCameraModeLabel.Visibility = Visibility.Visible;
					followCameraModeDropdown.Visibility = Visibility.Visible;
					discholderFollowTeamCheckbox.Visibility = Visibility.Collapsed;
					discholderFollowCameraModeLabel.Visibility = Visibility.Collapsed;
					discholderFollowCameraModeDropdown.Visibility = Visibility.Collapsed;
					break;
				case 3:
					followSpecificPlayerPanel.Visibility = Visibility.Visible;
					followCameraModeLabel.Visibility = Visibility.Visible;
					followCameraModeDropdown.Visibility = Visibility.Visible;
					discholderFollowTeamCheckbox.Visibility = Visibility.Collapsed;
					discholderFollowCameraModeLabel.Visibility = Visibility.Collapsed;
					discholderFollowCameraModeDropdown.Visibility = Visibility.Collapsed;
					break;
				case 4:
					followSpecificPlayerPanel.Visibility = Visibility.Collapsed;
					followCameraModeLabel.Visibility = Visibility.Collapsed;
					followCameraModeDropdown.Visibility = Visibility.Collapsed;
					discholderFollowTeamCheckbox.Visibility = Visibility.Visible;
					discholderFollowCameraModeLabel.Visibility = Visibility.Visible;
					discholderFollowCameraModeDropdown.Visibility = Visibility.Visible;
					break;
			}
		}

		private void SpectatorCameraFindNow(object sender, RoutedEventArgs e)
		{
			CameraWriteController.UseCameraControlKeys();
		}

		private void HideEchoVRUINow(object sender, RoutedEventArgs e)
		{
			if (!Program.InGame) return;
			CameraWriteController.SetUIVisibility(HideUICheckbox.IsChecked != true);
		}

		private void HideMinimapNow(object sender, RoutedEventArgs e)
		{
			if (!Program.InGame) return;
			CameraWriteController.SetMinimapVisibility(HideMinimapCheckbox.IsChecked != true);
		}

		private void ToggleNameplatesNow(object sender, RoutedEventArgs e)
		{
			if (!Program.InGame) return;
			CameraWriteController.SetNameplatesVisibility(HideNameplatesCheckbox.IsChecked != true);
		}

		private void UploadTabletStats(object sender, RoutedEventArgs e)
		{
			List<TabletStats> stats = Program.FindTabletStats();

			if (stats != null)
			{
				new UploadTabletStatsMenu(stats) { Owner = this }.Show();
			}
		}

		private void ResetAllSettings(object sender, RoutedEventArgs e)
		{
			EchoVRSettingsManager.ResetAllSettingsToDefault();

			RefreshAllSettings(sender, null);
		}

		private void MutePlayerCommsDropdownChanged(object sender, SelectionChangedEventArgs e)
		{
			if (!initialized) return;

			int index = ((ComboBox)sender).SelectedIndex;
			switch (index)
			{
				case 0: // leave default
					SparkSettings.instance.mutePlayerComms = false;
					SparkSettings.instance.muteEnemyTeam = false;
					CameraWriteController.SetTeamsMuted(false, false);
					break;
				case 1: // mute enemy team
					SparkSettings.instance.mutePlayerComms = false;
					SparkSettings.instance.muteEnemyTeam = true;
					break;
				case 2: // mute both teams
					SparkSettings.instance.mutePlayerComms = true;
					SparkSettings.instance.muteEnemyTeam = false;
					break;
			}

			CameraWriteController.SetPlayersMuted();
		}


		private void ClearTTSCacheButton(object sender, RoutedEventArgs e)
		{
			TTSController.ClearCacheFolder();
		}

		private void ChangeTTSCacheFolderButton_Click(object sender, RoutedEventArgs e)
		{
			if (!initialized) return;
			string selectedPath = "";
			CommonOpenFileDialog folderBrowserDialog = new CommonOpenFileDialog
			{
				InitialDirectory = TTSController.CacheFolder,
				IsFolderPicker = true
			};
			if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
			{
				selectedPath = folderBrowserDialog.FileName;
			}

			if (selectedPath != "")
			{
				SparkSettings.instance.ttsCacheFolder = selectedPath;
				RefreshAllSettings(sender, null);
			}
		}

		private void OpenTTSCacheButton(object sender, RoutedEventArgs e)
		{
			string folder = TTSController.CacheFolder;
			if (!Directory.Exists(Path.GetDirectoryName(folder)))
			{
				Directory.CreateDirectory(folder);
			}

			if (Directory.Exists(folder))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = folder,
					UseShellExecute = true
				});
			}
		}

		private void OpenSettingsFileFolder(object sender, RoutedEventArgs e)
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgniteVR", "Spark");
			if (!Directory.Exists(Path.GetDirectoryName(folder)))
			{
				Directory.CreateDirectory(folder);
			}

			if (Directory.Exists(folder))
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = folder,
					UseShellExecute = true
				});
			}
		}

		private void InstallReshade(object sender, RoutedEventArgs e)
		{
			if (string.IsNullOrEmpty(SparkSettings.instance.echoVRPath)) return;
			if (!File.Exists(SparkSettings.instance.echoVRPath)) return;

			ReshadeProgress.Visibility = Visibility.Visible;
			ReshadeProgress.Value = 0;

			// delete the old temp file
			if (File.Exists(Path.Combine(Path.GetTempPath(), "reshade.zip")))
			{
				File.Delete(Path.Combine(Path.GetTempPath(), "reshade.zip"));
			}

			// download reshade
			try
			{
				WebClient webClient = new WebClient();
				webClient.DownloadFileCompleted += ReshadeDownloadCompleted;
				webClient.DownloadProgressChanged += ReshadeDownloadProgressChanged;
				webClient.DownloadFileAsync(new Uri("https://github.com/NtsFranz/Spark/raw/main/resources/reshade.zip"), Path.Combine(Path.GetTempPath(), "reshade.zip"));
			}
			catch (Exception)
			{
				new MessageBox("Something broke while trying to download update", Properties.Resources.Error).Show();
			}
		}

		private void ReshadeDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
		{
			ReshadeProgress.Visibility = Visibility.Visible;
			ReshadeProgress.Value = e.ProgressPercentage;
		}

		private void ReshadeDownloadCompleted(object sender, AsyncCompletedEventArgs e)
		{
			try
			{
				// install reshade from the zip
				string dir = Path.GetDirectoryName(SparkSettings.instance.echoVRPath);
				if (dir != null)
				{
					ZipFile.ExtractToDirectory(Path.Combine(Path.GetTempPath(), "reshade.zip"), dir, true);
				}
			}
			catch (Exception)
			{
				new MessageBox("Something broke while trying to install Reshade. Report this to NtsFranz", Properties.Resources.Error).Show();
			}

			ReshadeProgress.Visibility = Visibility.Collapsed;
		}

		private void RemoveReshade(object sender, RoutedEventArgs e)
		{
			if (string.IsNullOrEmpty(SparkSettings.instance.echoVRPath)) return;
			if (!File.Exists(SparkSettings.instance.echoVRPath)) return;
			string dir = Path.GetDirectoryName(SparkSettings.instance.echoVRPath);
			if (dir == null) return;

			try
			{
				File.Delete(Path.Combine(dir, "DefaultPreset.ini"));
				File.Delete(Path.Combine(dir, "dxgi.dll"));
				File.Delete(Path.Combine(dir, "dxgi.log"));
				File.Delete(Path.Combine(dir, "ReShade.ini"));
				File.Delete(Path.Combine(dir, "Reshade.log"));
				File.Delete(Path.Combine(dir, "ReshadePreset.ini"));
				if (Directory.Exists(Path.Combine(dir, "reshade-shaders")))
				{
					Directory.Delete(Path.Combine(dir, "reshade-shaders"), true);
				}
			}
			catch (UnauthorizedAccessException)
			{
				new MessageBox("Can't uninstall Reshade. Try closing EchoVR and trying again.", Properties.Resources.Error).Show();
			}
		}		
		

		private void SaveQuestIPClicked(object sender, RoutedEventArgs e)
		{
			SaveQuestIPButton.Visibility = Visibility.Collapsed;
		}

		private void PlayTestSound(object sender, RoutedEventArgs e)
		{
			Program.synth.SpeakAsync("THIS IS A TEST SOUND!");
		}

		#region Change Theme

		// Tracks whether we're currently programmatically updating sliders/hex boxes
		// (to avoid feedback loops between TextChanged and Slider.ValueChanged)
		private bool _themeUpdating;

		// The 3 working colours
		private Color _themeDark;
		private Color _themeMid;
		private Color _themeLight;

		private void ChangeThemeTab_Loaded(object sender, RoutedEventArgs e)
		{
			LoadThemeFromSettings();
		}

		private void LoadThemeFromSettings()
		{
			_themeDark  = ThemesController.ParseHex(SparkSettings.instance?.customThemeDark  ?? "#c32b61");
			_themeMid   = ThemesController.ParseHex(SparkSettings.instance?.customThemeMid   ?? "#ea6192");
			_themeLight = ThemesController.ParseHex(SparkSettings.instance?.customThemeLight ?? "#ffaac9");
			SyncUIFromColors();
		}

		private void SyncUIFromColors()
		{
			// Crash Fix: Check if UI controls are ready before syncing.
			// This happens if the settings window is loaded but the theme tab hasn't been visited yet.
			if (DarkPreviewRect == null || DarkHexBox == null || DarkHueSlider == null) return;

			_themeUpdating = true;
			SyncColorToUI(DarkPreviewRect,  DarkHexBox,  DarkHueSlider,  DarkSatSlider,  DarkValSlider,  _themeDark);
			SyncColorToUI(MidPreviewRect,   MidHexBox,   MidHueSlider,   MidSatSlider,   MidValSlider,   _themeMid);
			SyncColorToUI(LightPreviewRect, LightHexBox, LightHueSlider, LightSatSlider, LightValSlider, _themeLight);
			_themeUpdating = false;
		}

		private static void SyncColorToUI(System.Windows.Shapes.Rectangle preview,
			                               TextBox hexBox,
			                               Slider hue, Slider sat, Slider val,
			                               Color color)
		{
			if (preview == null || hexBox == null || hue == null || sat == null || val == null) return;
			preview.Fill = new SolidColorBrush(color);
			hexBox.Text = ThemesController.ColorToHex(color);
			ColorToHsv(color, out double h, out double s, out double v);
			hue.Value = h;
			sat.Value = s;
			val.Value = v;
		}

		// ─── Dark sliders ───────────────────────────────────────────────────────
		private void DarkSlider_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
		{
			if (_themeUpdating || DarkHueSlider == null) return;
			_themeDark = HsvToColor(DarkHueSlider.Value, DarkSatSlider.Value, DarkValSlider.Value);
			_themeUpdating = true;
			DarkPreviewRect.Fill = new SolidColorBrush(_themeDark);
			DarkHexBox.Text = ThemesController.ColorToHex(_themeDark);
			_themeUpdating = false;
			_themeDebounceTimer.Stop();
			_themeDebounceTimer.Start();
		}

		private void DarkHexBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			if (_themeUpdating || DarkHexBox == null) return;
			if (!TryParseHexBox(DarkHexBox.Text, out Color c)) return;
			_themeDark = c;
			_themeUpdating = true;
			DarkPreviewRect.Fill = new SolidColorBrush(c);
			ColorToHsv(c, out double h, out double s, out double v);
			DarkHueSlider.Value = h; DarkSatSlider.Value = s; DarkValSlider.Value = v;
			_themeUpdating = false;
			ThemesController.ApplyCustomTheme(_themeDark, _themeMid, _themeLight);
		}

		// ─── Mid sliders ────────────────────────────────────────────────────────
		private void MidSlider_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
		{
			if (_themeUpdating || MidHueSlider == null) return;
			_themeMid = HsvToColor(MidHueSlider.Value, MidSatSlider.Value, MidValSlider.Value);
			_themeUpdating = true;
			MidPreviewRect.Fill = new SolidColorBrush(_themeMid);
			MidHexBox.Text = ThemesController.ColorToHex(_themeMid);
			_themeUpdating = false;
			_themeDebounceTimer.Stop();
			_themeDebounceTimer.Start();
		}

		private void MidHexBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			if (_themeUpdating || MidHexBox == null) return;
			if (!TryParseHexBox(MidHexBox.Text, out Color c)) return;
			_themeMid = c;
			_themeUpdating = true;
			MidPreviewRect.Fill = new SolidColorBrush(c);
			ColorToHsv(c, out double h, out double s, out double v);
			MidHueSlider.Value = h; MidSatSlider.Value = s; MidValSlider.Value = v;
			_themeUpdating = false;
			ThemesController.ApplyCustomTheme(_themeDark, _themeMid, _themeLight);
		}

		// ─── Light sliders ──────────────────────────────────────────────────────
		private void LightSlider_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
		{
			if (_themeUpdating || LightHueSlider == null) return;
			_themeLight = HsvToColor(LightHueSlider.Value, LightSatSlider.Value, LightValSlider.Value);
			_themeUpdating = true;
			LightPreviewRect.Fill = new SolidColorBrush(_themeLight);
			LightHexBox.Text = ThemesController.ColorToHex(_themeLight);
			_themeUpdating = false;
			_themeDebounceTimer.Stop();
			_themeDebounceTimer.Start();
		}

		private void LightHexBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			if (_themeUpdating || LightHexBox == null) return;
			if (!TryParseHexBox(LightHexBox.Text, out Color c)) return;
			_themeLight = c;
			_themeUpdating = true;
			LightPreviewRect.Fill = new SolidColorBrush(c);
			ColorToHsv(c, out double h, out double s, out double v);
			LightHueSlider.Value = h; LightSatSlider.Value = s; LightValSlider.Value = v;
			_themeUpdating = false;
			ThemesController.ApplyCustomTheme(_themeDark, _themeMid, _themeLight);
		}

		// ─── Presets ────────────────────────────────────────────────────────────
		private void ApplyPreset(string dark, string mid, string light)
		{
			_themeDark  = ThemesController.ParseHex(dark);
			_themeMid   = ThemesController.ParseHex(mid);
			_themeLight = ThemesController.ParseHex(light);
			SyncUIFromColors();
			ThemesController.ApplyCustomTheme(_themeDark, _themeMid, _themeLight);
		}

		private void PresetBlack_Click     (object s, RoutedEventArgs e) => ApplyPreset("#000000", "#141414", "#363636");
		private void PresetBlue_Click      (object s, RoutedEventArgs e) => ApplyPreset("#4040ff", "#5b5bff", "#9d9dff");
		private void PresetCoolPurple_Click(object s, RoutedEventArgs e) => ApplyPreset("#c76fdd", "#ce82e1", "#e3b9ee");
		private void PresetDarkRed_Click   (object s, RoutedEventArgs e) => ApplyPreset("#4a0002", "#820003", "#ff0d13");
		private void PresetGreen_Click     (object s, RoutedEventArgs e) => ApplyPreset("#006400", "#008000", "#00e800");
		private void PresetHotPink_Click   (object s, RoutedEventArgs e) => ApplyPreset("#fe63d8", "#fe81df", "#ffbfef");
		private void PresetPurple_Click    (object s, RoutedEventArgs e) => ApplyPreset("#690f96", "#8a14c2", "#d213bb");
		private void PresetRed_Click       (object s, RoutedEventArgs e) => ApplyPreset("#b70004", "#df0005", "#ff7174");
		private void PresetYellow_Click    (object s, RoutedEventArgs e) => ApplyPreset("#fab011", "#fcc520", "#fcde64");

		// ─── Apply / Reset ──────────────────────────────────────────────────────
		private void ApplyTheme_Click(object sender, RoutedEventArgs e)
		{
			ThemesController.SaveAndApply(_themeDark, _themeMid, _themeLight);
		}

		private void ResetTheme_Click(object sender, RoutedEventArgs e)
		{
			// Reset to Spark Original Pink
			ApplyPreset("#c32b61", "#ea6192", "#ffaac9");
			ThemesController.SaveAndApply(_themeDark, _themeMid, _themeLight);
		}

		// ─── Helpers ────────────────────────────────────────────────────────────
		private static bool TryParseHexBox(string text, out Color color)
		{
			color = default;
			string t = text?.TrimStart('#') ?? "";
			if (t.Length != 6) return false;
			try { color = ThemesController.ParseHex("#" + t); return true; }
			catch { return false; }
		}

		private static Color HsvToColor(double hue, double sat, double val)
		{
			hue = hue % 360.0;
			if (hue < 0) hue += 360.0;
			if (sat < 0) sat = 0; if (sat > 1) sat = 1;
			if (val < 0) val = 0; if (val > 1) val = 1;

			if (sat == 0)
			{
				byte c = (byte)(val * 255);
				return Color.FromRgb(c, c, c);
			}

			double h = hue / 60.0;
			int i = (int)Math.Floor(h);
			double f = h - i;
			double p = val * (1 - sat);
			double q = val * (1 - sat * f);
			double t2 = val * (1 - sat * (1 - f));

			double r, g, b;
			switch (i % 6)
			{
				case 0: r = val; g = t2;  b = p;   break;
				case 1: r = q;   g = val; b = p;   break;
				case 2: r = p;   g = val; b = t2;  break;
				case 3: r = p;   g = q;   b = val; break;
				case 4: r = t2;  g = p;   b = val; break;
				default: r = val; g = p;  b = q;   break;
			}

			return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
		}

		private static void ColorToHsv(Color color, out double hue, out double sat, out double val)
		{
			double r = color.R / 255.0;
			double g = color.G / 255.0;
			double b = color.B / 255.0;
			double max = Math.Max(r, Math.Max(g, b));
			double min = Math.Min(r, Math.Min(g, b));
			double delta = max - min;

			val = max;
			sat = max == 0 ? 0 : delta / max;

			if (delta == 0) { hue = 0; return; }

			if (max == r)      hue = 60 * (((g - b) / delta) % 6);
			else if (max == g) hue = 60 * (((b - r) / delta) + 2);
			else               hue = 60 * (((r - g) / delta) + 4);

			if (hue < 0) hue += 360;
		}

		#endregion
	}

	
	public class SettingBindingExtension : Binding
	{
		public SettingBindingExtension()
		{
			Initialize();
		}

		public SettingBindingExtension(string path) : base(path)
		{
			Initialize();
		}

		private void Initialize()
		{
			Source = SparkSettings.instance;
			Mode = BindingMode.TwoWay;
		}
	}

	public class SettingLoadExtension : Binding
	{
		public SettingLoadExtension()
		{
			Initialize();
		}

		public SettingLoadExtension(string path) : base(path)
		{
			Initialize();
		}

		private void Initialize()
		{
			Source = SparkSettings.instance;
			Mode = BindingMode.OneWay;
		}
	}
}
