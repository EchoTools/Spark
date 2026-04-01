using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Vosk;
using NAudio.Wave;
using Newtonsoft.Json;
using System.Net;
using System.IO.Compression;

namespace Spark
{
	public class SpeechRecognition : IDisposable
	{
		public float micLevel = 0;
		public float speakerLevel = 0;

		private bool capturing;
		private WaveInEvent micCapture;
		private WasapiLoopbackCapture speakerCapture;
		private VoskRecognizer voskRecMic;
		private VoskRecognizer voskRecSpeaker;

		public bool Enabled
		{
			get => capturing;
			set
			{
				if (value != capturing)
				{
					capturing = value;
					try
					{
						if (capturing)
						{
							if (micCapture != null)
							{
								try { micCapture.StartRecording(); } catch (Exception ex) { Logger.LogRow(Logger.LogType.Error, "Error starting mic: " + ex.Message); }
							}
							if (speakerCapture != null)
							{
								try { speakerCapture.StartRecording(); } catch { }
							}
						}
						else
						{
							if (micCapture != null)
							{
								try { micCapture.StopRecording(); } catch { }
							}
							if (speakerCapture != null)
							{
								try { speakerCapture.StopRecording(); } catch { }
							}
						}
					}
					catch (Exception e)
					{
						Logger.LogRow(Logger.LogType.Error, "Error toggling voice rec state.\n" + e);
					}
				}
			}
		}

		public SpeechRecognition()
		{
			try
			{
				Vosk.Vosk.SetLogLevel(0);
				_ = Task.Run(DownloadVoskModel);
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, "Error starting voice rec.\n" + e);
			}
		}

		private async Task DownloadVoskModel()
		{
			string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgniteVR", "Spark");
			string path = Path.Combine(appDataPath, "vosk-model-small-en-us-0.15");
			
			if (Directory.Exists(path))
			{
				AfterDownload(path);
			}
			else
			{
				Logger.LogRow(Logger.LogType.Error, "Vosk model not found. Downloading.");
				try 
				{
					if (!Directory.Exists(appDataPath)) Directory.CreateDirectory(appDataPath);

					using (WebClient webClient = new WebClient())
					{
						webClient.Headers.Add("User-Agent: Spark");
						string zipFile = Path.Combine(Path.GetTempPath(), "vosk_model.zip");
						await webClient.DownloadFileTaskAsync(new Uri("https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"), zipFile);
						ZipFile.ExtractToDirectory(zipFile, appDataPath);
						
						if (File.Exists(zipFile)) File.Delete(zipFile);
					}

					if (Directory.Exists(path))
					{
						AfterDownload(path);
					}
					else
					{
						Logger.LogRow(Logger.LogType.Error, "Vosk model failed to download or extract properly.");
					}
				}
				catch (Exception ex)
				{
					Logger.LogRow(Logger.LogType.Error, $"Download failed: {ex.Message}");
				}
			}
		}

		private void AfterDownload(string path)
		{
			try 
			{
				Model model = new Model(path);

				voskRecMic = new VoskRecognizer(model, 16000f);
				voskRecMic.SetMaxAlternatives(10);
				voskRecMic.SetWords(true);

				micCapture = new WaveInEvent();
				micCapture.WaveFormat = new WaveFormat(16000, 1);
				micCapture.DeviceNumber = GetMicByName(SparkSettings.instance.microphone);
				micCapture.DataAvailable += MicDataAvailable;

				// Note: Speaker capture was commented out in original file, but we can init it safely if needed
				// To enable speaker capture, uncomment the lines below:
				// voskRecSpeaker = new VoskRecognizer(model, 16000f);
				// voskRecSpeaker.SetMaxAlternatives(10);
				// voskRecSpeaker.SetWords(true);
				// speakerCapture = new WasapiLoopbackCapture(GetSpeakerByName(SparkSettings.instance.speaker));
				// speakerCapture.DataAvailable += SpeakerDataAvailable;

				// Only start recording if the setting is actually enabled
				if (SparkSettings.instance.enableVoiceRecognition)
				{
					Enabled = true; // This triggers the StartRecording logic in the setter
				}
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, "Error initializing microphone: " + e.Message);
			}
		}

		private void SpeakerDataAvailable(object sender, WaveInEventArgs e)
		{
			if (!SparkSettings.instance.enableVoiceRecognition) return;

			speakerLevel = 0;
			float maxSample = 0;
			for (int index = 0; index < e.BytesRecorded; index += 4)
			{
				float sample = BitConverter.ToSingle(e.Buffer, index);
				if (sample < 0) sample = -sample;
				if (sample > maxSample) maxSample = sample;
			}
			speakerLevel = maxSample;

			// Optimization: Avoid allocation if speaker rec isn't active
			if (voskRecSpeaker != null)
			{
				float[] floats = new float[e.BytesRecorded / 4];
				Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);

				if (voskRecSpeaker.AcceptWaveform(floats, floats.Length))
				{
					HandleResult(voskRecSpeaker.Result());
				}
			}
		}

		private void MicDataAvailable(object sender, WaveInEventArgs e)
		{
			if (!SparkSettings.instance.enableVoiceRecognition) return;

			float maxSample = 0;
			for (int index = 0; index < e.BytesRecorded; index += 2)
			{
				short sample = (short)((e.Buffer[index + 1] << 8) | e.Buffer[index + 0]);
				float sample32 = Math.Abs(sample / 32768f);
				if (sample32 > maxSample) maxSample = sample32;
			}
			micLevel = maxSample;

			if (voskRecMic != null && voskRecMic.AcceptWaveform(e.Buffer, e.BytesRecorded))
			{
				HandleResult(voskRecMic.Result());
			}
		}

		private static void HandleResult(string result)
		{
			if (string.IsNullOrEmpty(result)) return;

			Task.Run(() => 
			{
				try
				{
					if (!result.Contains("text")) return;

					List<string> clipTerms = new List<string>();

					bool checkHighlights = SparkSettings.instance.clipThatDetectionNVHighlights;
					bool checkMedal = SparkSettings.instance.clipThatDetectionMedal;

					if (checkHighlights || checkMedal)
					{
						clipTerms.Add("clip that");
						clipTerms.Add("quebec");
						clipTerms.Add("hope that");
						clipTerms.Add("could that");
						clipTerms.Add("cop that");
						clipTerms.Add("say cheese");
					}
					
					if (clipTerms.Count == 0) return;

					Dictionary<string, List<Dictionary<string, object>>> r = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(result);
					if (r == null || !r.ContainsKey("alternatives")) return;

					foreach (Dictionary<string, object> alt in r["alternatives"])
					{
						if (!alt.ContainsKey("text")) continue;
						string text = alt["text"].ToString();
						
						if (string.IsNullOrWhiteSpace(text)) continue;

						Debug.WriteLine(text);

						foreach (string clipTerm in clipTerms)
						{
							if (text.Contains(clipTerm))
							{
								Program.ManualClip?.Invoke();

								if (checkMedal)
								{
									Medal.ClipNow();
								}
								if (checkHighlights)
								{
									HighlightsHelper.SaveHighlight("PERSONAL_HIGHLIGHT_GROUP", "MANUAL", true);
								}

								Program.synth.SpeakAsync("Clip Saved!");
								return;
							}
						}
					}
				}
				catch (Exception e)
				{
					Logger.LogRow(Logger.LogType.Error, "Error handling voice result: " + e);
				}
			});
		}

		private static int GetMicByName(string name)
		{
			int count = WaveIn.DeviceCount;
			for (int deviceId = 0; deviceId < count; deviceId++)
			{
				WaveInCapabilities deviceInfo = WaveIn.GetCapabilities(deviceId);
				if (deviceInfo.ProductName.StartsWith(name))
				{
					return deviceId;
				}
			}
			return 0;
		}

		private static MMDevice GetSpeakerByName(string name)
		{
			MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
			var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
			foreach(var device in devices)
			{
				if (device.FriendlyName == name) return device;
			}
			return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		}

		public float GetMicLevel() => micLevel;
		public float GetSpeakerLevel() => speakerLevel;

		public async Task ReloadMic()
		{
			if (micCapture != null)
			{
				micCapture.StopRecording();
				micCapture.Dispose();
				micCapture = null;
			}
			
			await Task.Delay(200);

			try {
				micCapture = new WaveInEvent();
				micCapture.WaveFormat = new WaveFormat(16000, 1);
				micCapture.DeviceNumber = GetMicByName(SparkSettings.instance.microphone);
				micCapture.DataAvailable += MicDataAvailable;
				
				if (Enabled) micCapture.StartRecording();
			} catch (Exception e) {
				Logger.LogRow(Logger.LogType.Error, "Error reloading mic: " + e.Message);
			}
		}

		public async Task ReloadSpeaker()
		{
			if (speakerCapture != null)
			{
				try {
					speakerCapture.StopRecording();
					speakerCapture.Dispose();
				} catch {}
				speakerCapture = null;
			}

			await Task.Delay(200);

			try {
				// We only initialize if the setting calls for it, or we can just initialize it but not start it
				// For now, mirroring ReloadMic logic but respecting the fact that speaker capture might be disabled in code
				speakerCapture = new WasapiLoopbackCapture(GetSpeakerByName(SparkSettings.instance.speaker));
				speakerCapture.DataAvailable += SpeakerDataAvailable;
				if (Enabled) speakerCapture.StartRecording();
			} catch (Exception e) {
				// Logging as warning since speaker capture is often disabled/optional
				Logger.LogRow(Logger.LogType.Error, "Error reloading speaker (this is normal if speaker capture is disabled): " + e.Message);
			}
		}

		public void Dispose()
		{
			micCapture?.StopRecording();
			micCapture?.Dispose();
			speakerCapture?.StopRecording();
			speakerCapture?.Dispose();
			voskRecMic?.Dispose();
			voskRecSpeaker?.Dispose();
		}
	}
}