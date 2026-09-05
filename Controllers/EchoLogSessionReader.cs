using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Spark
{
	/// <summary>
	/// Pulls the current session id straight out of EchoVR's own r14 log.
	///
	/// The /session API only answers once you're in a *match*: a social lobby comes back as error
	/// code -6 with no session id at all, so a friend sitting in a lobby has nothing joinable to
	/// advertise and nobody can drop in on them. The client does log the id of every session it
	/// enters, lobbies included:
	///
	///     [NSLOBBY] requesting 1 player session in game session {B6B4E0BC-3185-4684-85BF-C6920268E19B}
	///
	/// so tailing the log fills the gap the API leaves. Matches still take their id from the API —
	/// that's authoritative and already correct — and this only covers the lobby case.
	/// </summary>
	public static class EchoLogSessionReader
	{
		private static readonly Regex sessionRegex = new Regex(
			@"in game session\s*\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex httpPortRegex = new Regex(
			@"Bound HTTP listener to\s+[0-9.]+:(\d+)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>Anything that means the client has dropped out of the session it was in.</summary>
		private static readonly string[] endMarkers =
		{
			"ending session",
			"leaving session",
			"canceling pending session",
		};

		private const string NullSessionId = "00000000-0000-0000-0000-000000000000";

		/// <summary>Beyond this the first read is capped to the tail, to keep startup off a huge log.</summary>
		private const long MaxFirstReadBytes = 8 * 1024 * 1024;

		/// <summary>Most a single poll will pull in, so one slow catch-up can't stall the caller.</summary>
		private const long MaxChunkBytes = 4 * 1024 * 1024;

		private static readonly object stateLock = new object();
		private static string currentLogPath;
		private static long readPosition;
		private static string sessionId;
		private static DateTime lastPoll = DateTime.MinValue;
		private static DateTime lastDirScan = DateTime.MinValue;

		/// <summary>
		/// The session the local client is in according to its log, or null when it has left. Polls
		/// the log at most once a second, so this is cheap to call from a loop.
		/// </summary>
		public static string CurrentSessionId
		{
			get
			{
				Poll();
				lock (stateLock)
				{
					return sessionId;
				}
			}
		}

		/// <summary>The log folder that goes with the configured echovr.exe, or null if unset/missing.</summary>
		public static string LogDirectory
		{
			get
			{
				try
				{
					string exePath = SparkSettings.instance?.echoVRPath;
					if (string.IsNullOrEmpty(exePath)) return null;

					string exeDir = Path.GetDirectoryName(exePath);
					if (string.IsNullOrEmpty(exeDir)) return null;

					// ...\ready-at-dawn-echo-arena\bin\win10\echovr.exe -> ...\ready-at-dawn-echo-arena\_local\r14logs
					string dir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "_local", "r14logs"));
					return Directory.Exists(dir) ? dir : null;
				}
				catch (Exception)
				{
					return null;
				}
			}
		}

		/// <summary>
		/// Log reading only describes the game running on *this* PC, so it's meaningless when Spark
		/// is pointed at a Quest across the network.
		/// </summary>
		public static bool WatchingLocalGame
		{
			get
			{
				string ip = SparkSettings.instance?.echoVRIP;
				return string.IsNullOrEmpty(ip) || ip == "127.0.0.1" || ip == "localhost" || ip == "::1";
			}
		}

		public static void Reset()
		{
			lock (stateLock)
			{
				currentLogPath = null;
				readPosition = 0;
				sessionId = null;
				lastPoll = DateTime.MinValue;
				lastDirScan = DateTime.MinValue;
			}
		}

		private static void Poll()
		{
			lock (stateLock)
			{
				if ((DateTime.UtcNow - lastPoll).TotalMilliseconds < 1000) return;
				lastPoll = DateTime.UtcNow;
			}

			if (!WatchingLocalGame)
			{
				lock (stateLock) { sessionId = null; }
				return;
			}

			try
			{
				string path = FindActiveLogFile();
				if (path == null)
				{
					lock (stateLock) { sessionId = null; }
					return;
				}

				bool fresh;
				long startAt;
				lock (stateLock)
				{
					fresh = !string.Equals(path, currentLogPath, StringComparison.OrdinalIgnoreCase);
					if (fresh)
					{
						currentLogPath = path;
						readPosition = 0;
						sessionId = null;
					}
					startAt = readPosition;
				}

				using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

				// A log that was rotated or truncated under us restarts from the top.
				if (startAt > stream.Length) startAt = 0;

				// Don't chew through a several-hundred-megabyte log on the first read; the tail still
				// carries the most recent join, and any session change after this is picked up live.
				if (fresh && stream.Length > MaxFirstReadBytes) startAt = stream.Length - MaxFirstReadBytes;

				long available = Math.Min(stream.Length - startAt, MaxChunkBytes);
				if (available <= 0) return;

				stream.Seek(startAt, SeekOrigin.Begin);
				byte[] buffer = new byte[available];
				int read = stream.Read(buffer, 0, (int)available);
				if (read <= 0) return;

				string text = Encoding.UTF8.GetString(buffer, 0, read);

				// The game is writing this file as we read it, so the tail is very often a half-
				// written line. Leave it for the next poll — consuming it would split a session id
				// across two reads and neither half would match.
				int lastNewline = text.LastIndexOf('\n');
				long consumed;
				if (lastNewline >= 0)
				{
					text = text.Substring(0, lastNewline + 1);
					consumed = Encoding.UTF8.GetByteCount(text);
				}
				else if (read >= MaxChunkBytes)
				{
					// A single line longer than the whole chunk. It can't be one we care about
					// (those are short), so skip past it rather than re-reading it forever.
					text = string.Empty;
					consumed = read;
				}
				else
				{
					// Nothing complete yet — wait for the rest of the line.
					return;
				}

				string found = null;
				bool sawEnd = false;
				foreach (string line in text.Split('\n'))
				{
					// Only the short status lines are interesting; the profile JSON the client dumps
					// runs to hundreds of KB on one line and can't contain either pattern.
					if (line.Length > 512) continue;

					Match m = sessionRegex.Match(line);
					if (m.Success)
					{
						string id = m.Groups[1].Value.ToUpperInvariant();
						if (id != NullSessionId)
						{
							found = id;
							sawEnd = false;
						}
						continue;
					}

					foreach (string marker in endMarkers)
					{
						if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
						{
							sawEnd = true;
							break;
						}
					}
				}

				lock (stateLock)
				{
					readPosition = startAt + consumed;
					// An end marker with nothing after it means we're out of the session; a join later
					// in the same batch wins, which is why `sawEnd` clears above.
					if (found != null) sessionId = found;
					else if (sawEnd) sessionId = null;
				}
			}
			catch (IOException)
			{
				// Log locked mid-write — the next poll picks up where this one left off.
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, $"Error reading EchoVR log for session id.\n{e}");
			}
		}

		/// <summary>
		/// Picks the log belonging to the client Spark is actually watching. Spectator clients write
		/// into the same folder, so "newest file" alone would follow the wrong one whenever two
		/// clients are up; the HTTP port each one binds tells them apart.
		/// </summary>
		private static string FindActiveLogFile()
		{
			string dir = LogDirectory;
			if (dir == null) return null;

			// Listing the directory is the expensive part, so only redo it every few seconds. In
			// between, keep tailing the file already open unless it has gone away.
			lock (stateLock)
			{
				if (currentLogPath != null &&
				    (DateTime.UtcNow - lastDirScan).TotalSeconds < 5 &&
				    File.Exists(currentLogPath))
				{
					return currentLogPath;
				}
				lastDirScan = DateTime.UtcNow;
			}

			List<FileInfo> candidates = new DirectoryInfo(dir)
				.GetFiles("*.log")
				.Where(f => (DateTime.Now - f.LastWriteTime).TotalHours < 24)
				.OrderByDescending(f => f.LastWriteTime)
				.Take(6)
				.ToList();

			if (candidates.Count == 0) return null;
			if (candidates.Count == 1) return candidates[0].FullName;

			int wantedPort = SparkSettings.instance?.echoVRPort ?? 6721;
			foreach (FileInfo file in candidates)
			{
				if (BoundHttpPort(file.FullName) == wantedPort) return file.FullName;
			}

			return candidates[0].FullName;
		}

		/// <summary>The HTTP API port this log's client bound, or -1 if it never got that far.</summary>
		private static int BoundHttpPort(string path)
		{
			try
			{
				using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using StreamReader reader = new StreamReader(stream);

				// The bind happens during startup, well inside the first few hundred lines.
				for (int i = 0; i < 1500; i++)
				{
					string line = reader.ReadLine();
					if (line == null) break;
					if (line.Length > 512) continue;

					Match m = httpPortRegex.Match(line);
					if (m.Success && int.TryParse(m.Groups[1].Value, out int port)) return port;
				}
			}
			catch (Exception)
			{
				// An unreadable log just doesn't win the port match.
			}

			return -1;
		}
	}
}
