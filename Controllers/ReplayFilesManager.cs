using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ButterReplays;
using EchoVRAPI;
using Newtonsoft.Json;
using static Logger;

namespace Spark
{
	public class ReplayFilesManager : IDisposable
	{
		private ButterFile butter;
		public string fileName;

		private readonly object butterWritingLock = new object();
		private readonly object fileWritingLock = new object();
		
		public bool zipping;
		public bool splitting;

		// Replaced concurrent queues with BlockingCollection for CPU-efficient waiting
		private readonly BlockingCollection<Action> writeQueue = new BlockingCollection<Action>();
		private readonly Task writeThread;
		private readonly CancellationTokenSource cts = new CancellationTokenSource();

        // Fix: Added property back to satisfy Program.cs check. 
        // Returns true if there are still items pending in the write queue.
		public bool replayThreadActive => writeQueue.Count > 0;

		public ConcurrentQueue<DateTime> replayBufferTimestamps = new ConcurrentQueue<DateTime>();
		public ConcurrentQueue<string> replayBufferJSON = new ConcurrentQueue<string>();
		public ConcurrentQueue<string> replayBufferJSONBones = new ConcurrentQueue<string>();

		private const string echoreplayDateFormat = "yyyy/MM/dd HH:mm:ss.fff";
		private const string fileNameFormat = "rec_yyyy-MM-dd_HH-mm-ss";

		private int lastButterNumChunks;
		private static readonly List<float> fullDeltaTimes = new List<float> { 33.3333333f, 66.666666f, 100 };
		private static int FrameInterval => Math.Clamp((int)(fullDeltaTimes[SparkSettings.instance.targetDeltaTimeIndexFull] / Program.StatsIntervalMs), 1, 10000);
		private int frameIndex;

		public ReplayFilesManager()
		{
			butter = new ButterFile(compressionFormat: SparkSettings.instance.butterCompressionFormat);

			// Start the dedicated file writing thread
			writeThread = Task.Factory.StartNew(FileWritingLoop, TaskCreationOptions.LongRunning);

			Split();

			Program.NewFrame += AddButterFrame;
			Program.FrameFetched += AddEchoreplayFrame;

			Program.JoinedGame += _ =>
			{
				lock (fileWritingLock)
				{
					fileName = DateTime.Now.ToString(fileNameFormat);
				}
			};
			Program.LeftGame += _ =>
			{
				Task.Run(async () =>
				{
					await Task.Delay(100);
					Split();
				});
			};
			Program.NewRound += _ =>
			{
				Split();
			};
			Program.SparkClosing += () =>
			{
                // Stop accepting new writes, but allow buffer to drain
				writeQueue.CompleteAdding();
				Split();
			};
		}

		/// <summary>
		/// Consumes items from the queue and executes them. 
		/// Blocks when empty (0% CPU usage) instead of sleeping/polling.
		/// </summary>
		private void FileWritingLoop()
		{
			try
			{
				foreach (var action in writeQueue.GetConsumingEnumerable(cts.Token))
				{
					try
					{
						action();
					}
					catch (Exception ex)
					{
						Logger.LogRow(Logger.LogType.Error, "Error in file writing thread: " + ex);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Graceful shutdown
			}
		}

		private void AddEchoreplayFrame(DateTime timestamp, string session, string bones)
		{
			frameIndex++;

			if (!SparkSettings.instance.enableReplayBuffer)
			{
				if (!SparkSettings.instance.enableFullLogging) return;
				if (!SparkSettings.instance.saveEchoreplayFiles) return;
			}

			if (frameIndex % FrameInterval != 0) return;

			try
			{
				if (session.Length <= 800) return;

				if (SparkSettings.instance.enableFullLogging)
				{
					bool log = true;
					if (SparkSettings.instance.onlyRecordPrivateMatches)
					{
						SimpleFrame obj = JsonConvert.DeserializeObject<SimpleFrame>(session);
						if (obj?.private_match != true)
						{
							log = false;
						}
					}

					if (log)
					{
						string lineToWrite = bones != null 
							? $"{timestamp.ToString(echoreplayDateFormat)}\t{session}\t{bones}"
							: $"{timestamp.ToString(echoreplayDateFormat)}\t{session}";

						// Offload IO to background thread without blocking main thread
                        if (!writeQueue.IsAddingCompleted)
                        {
						    writeQueue.Add(() => WriteEchoreplayLineDirect(lineToWrite));
                        }
					}
				}

				if (SparkSettings.instance.enableReplayBuffer)
				{
					replayBufferTimestamps.Enqueue(timestamp);
					replayBufferJSON.Enqueue(session);
					replayBufferJSONBones.Enqueue(bones);

					while (replayBufferTimestamps.Count > 0 && 
					       timestamp - replayBufferTimestamps.First() > TimeSpan.FromSeconds(SparkSettings.instance.replayBufferLength))
					{
						replayBufferTimestamps.TryDequeue(out _);
						replayBufferJSON.TryDequeue(out _);
						replayBufferJSONBones.TryDequeue(out _);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Error in adding .echoreplay frame " + ex);
			}
		}

		private void AddButterFrame(Frame f)
		{
			if (!SparkSettings.instance.enableFullLogging) return;
			if (!SparkSettings.instance.saveButterFiles) return;

			if (frameIndex % FrameInterval != 0) return;
			
            if (!writeQueue.IsAddingCompleted)
            {
			    writeQueue.Add(() => ProcessButterFrame(f));
            }
		}

		private void ProcessButterFrame(Frame f)
		{
			butter.AddFrame(f);

			if (butter.NumChunks() != lastButterNumChunks)
			{
				WriteOutButterFile();
				lastButterNumChunks = butter.NumChunks();
			}
		}

		private void WriteOutButterFile()
		{
			lock (butterWritingLock)
			{
				byte[] butterBytes = butter?.GetBytes();
				if (butterBytes != null && butterBytes.Length > 0)
				{
					string path = Path.Combine(SparkSettings.instance.saveFolder, fileName + ".butter");
					File.WriteAllBytes(path, butterBytes);
				}
			}
		}

		private void WriteEchoreplayLineDirect(string line)
		{
			lock (fileWritingLock)
			{
				if (!Directory.Exists(SparkSettings.instance.saveFolder)) return;

				string filePath = Path.Combine(SparkSettings.instance.saveFolder, fileName + ".echoreplay");
				
				using (StreamWriter streamWriter = new StreamWriter(filePath, true))
				{
					streamWriter.WriteLine(line);
				}
			}
		}

		public void SaveReplayClip(string filename)
		{
			string[] frames = replayBufferJSON.ToArray();
			DateTime[] timestamps = replayBufferTimestamps.ToArray();

			if (frames.Length != timestamps.Length)
			{
				LogRow(LogType.Error, "Something went wrong in the replay buffer saving.");
				return;
			}

            if (writeQueue.IsAddingCompleted) return;

			writeQueue.Add(() => 
			{
				try
				{
					string fullFileName = $"{DateTime.Now:clip_yyyy-MM-dd_HH-mm-ss}_{filename}";
					string filePath = Path.Combine(SparkSettings.instance.saveFolder, $"{fullFileName}.echoreplay");

					lock (fileWritingLock)
					{
						using (StreamWriter streamWriter = new StreamWriter(filePath, false))
						{
							for (int i = 0; i < frames.Length; i++)
							{
								streamWriter.WriteLine(timestamps[i].ToString(echoreplayDateFormat) + "\t" + frames[i]);
							}
						}

						if (SparkSettings.instance.useCompression)
						{
							zipping = true;
							string tempDir = Path.Combine(SparkSettings.instance.saveFolder, "temp_zip_" + Guid.NewGuid());
							Directory.CreateDirectory(tempDir);
							File.Move(filePath, Path.Combine(tempDir, $"{fullFileName}.echoreplay"));
							ZipFile.CreateFromDirectory(tempDir, filePath);
							Directory.Delete(tempDir, true);
							zipping = false;
						}
					}
				}
				catch (Exception e)
				{
					Logger.LogRow(Logger.LogType.Error, "Error saving clip: " + e.Message);
				}
			});
		}

		public void Split()
		{
            if (writeQueue.IsAddingCompleted) return;

			writeQueue.Add(() => 
			{
				splitting = true;
				
				lock (butterWritingLock)
				{
					WriteOutButterFile();
					butter = new ButterFile(compressionFormat: SparkSettings.instance.butterCompressionFormat);
					lastButterNumChunks = 0;
				}

				lock (fileWritingLock)
				{
					string lastFilename = fileName;
					fileName = DateTime.Now.ToString(fileNameFormat);

					if (SparkSettings.instance.useCompression && !string.IsNullOrEmpty(lastFilename))
					{
						string oldFile = Path.Combine(SparkSettings.instance.saveFolder, lastFilename + ".echoreplay");
						if (File.Exists(oldFile))
						{
							zipping = true;
							try
							{
								string tempDir = Path.Combine(SparkSettings.instance.saveFolder, "temp_zip_" + Guid.NewGuid());
								Directory.CreateDirectory(tempDir);
								
								string destFile = Path.Combine(tempDir, lastFilename + ".echoreplay");
								File.Move(oldFile, destFile);
								
								ZipFile.CreateFromDirectory(tempDir, oldFile);
								Directory.Delete(tempDir, true);
							}
							catch (Exception ex)
							{
								Logger.LogRow(Logger.LogType.Error, "Error zipping split file: " + ex.Message);
							}
							zipping = false;
						}
					}
				}
				splitting = false;
			});

			replayBufferTimestamps.Clear();
			replayBufferJSON.Clear();
			replayBufferJSONBones.Clear();
		}

		public void Dispose()
		{
            // Signal queue to finish what it has
			if (!writeQueue.IsAddingCompleted) writeQueue.CompleteAdding();
			
            // Give the thread a moment to finish pending writes (e.g. 2 seconds)
			try { writeThread.Wait(2000); } catch { }
			
            // Force kill if still stuck
            cts.Cancel();
			writeQueue.Dispose();
			cts.Dispose();
		}
	}
}