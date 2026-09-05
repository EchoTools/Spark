using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Spark
{
	/// <summary>
	/// Pulls the current session id straight out of EchoVR's own r14 log.
	///
	/// The /session API only answers once you're in a *match*: a social lobby comes back as error
	/// code -6 with no session id at all, so a friend sitting in a lobby has nothing joinable to
	/// advertise and nobody can drop in on them. The client does record every room it's in, as an
	/// Oculus room data-store update:
	///
	///     {"category":"social","provider":"ovr","message":"ovr_Room_UpdateDataStore",
	///      "data":{"seqid":8,"lobbyid":"B6B4E0BC-3185-4684-85BF-C6920268E19B", ...}}
	///
	/// The most recent "lobbyid" is the room you're in now, and the client writes the all-zero guid
	/// when you leave. Matches still take their id from the API — that's authoritative and already
	/// correct — and this only covers the lobby case.
	///
	/// Checked against 62 real logs. Two other markers look usable and are not:
	///
	///  - "[NSLOBBY] requesting N player session in game session {guid}" is only written when the
	///    client actively matchmakes a session, not when it's placed in one or rejoins. It was
	///    absent entirely in 34 of the 62 logs where lobbyid had the right answer, and stale in
	///    one more. There was no log where it knew something lobbyid didn't.
	///  - "lobby_id" (underscore) inside ovr_RichPresence_Set lines is frequently the all-zero
	///    guid *while you're in a room*, so matching it loosely would wipe the real value. The
	///    pattern below is deliberately strict about the key name.
	/// </summary>
	public static class EchoLogSessionReader
	{
		private static readonly Regex lobbyIdRegex = new Regex(
			@"""lobbyid""\s*:\s*""([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})""",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex httpPortRegex = new Regex(
			@"Bound HTTP listener to\s+[0-9.]+:(\d+)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>What the client writes for lobbyid once you've left the room.</summary>
		private const string NullSessionId = "00000000-0000-0000-0000-000000000000";

		/// <summary>Beyond this the first read is capped to the tail, to keep startup off a huge log.</summary>
		private const long MaxFirstReadBytes = 8 * 1024 * 1024;

		/// <summary>Most a single poll will pull in, so one slow catch-up can't stall the caller.</summary>
		private const long MaxChunkBytes = 4 * 1024 * 1024;

		/// <summary>
		/// Skip anything longer than this. Room updates measured 325-370 characters across the real
		/// logs, so this leaves generous headroom while still skipping the profile dumps, which are
		/// the only genuinely large lines.
		/// </summary>
		private const int MaxScannedLineLength = 2048;

		/// <summary>How often the background watcher re-reads the log.</summary>
		private const int PollIntervalMs = 1000;

		private static readonly object stateLock = new object();
		private static bool watching;
		private static string currentLogPath;
		private static long readPosition;
		private static string sessionId;
		private static DateTime lastDirScan = DateTime.MinValue;

		/// <summary>
		/// The room the local client is in according to its log, or null when it has left.
		///
		/// This is a cached read — the file work happens on the background watcher started by the
		/// first caller — so it's safe to call from the UI thread every frame.
		/// </summary>
		public static string CurrentSessionId
		{
			get
			{
				EnsureWatching();
				lock (stateLock)
				{
					return sessionId;
				}
			}
		}

		/// <summary>
		/// Starts the background watcher if it isn't already going. Reading the log means hitting
		/// the disk, which mustn't happen on the UI thread, so the value is refreshed off-thread and
		/// callers only ever read what it last found.
		/// </summary>
		private static void EnsureWatching()
		{
			lock (stateLock)
			{
				if (watching) return;
				watching = true;
			}

			Task.Run(async () =>
			{
				try
				{
					while (Program.running)
					{
						Poll();
						await Task.Delay(PollIntervalMs);
					}
				}
				finally
				{
					lock (stateLock) { watching = false; }
				}
			});
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
				lastDirScan = DateTime.MinValue;
			}
		}

		private static void Poll()
		{
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
				bool sawLobbyId = false;
				foreach (string line in text.Split('\n'))
				{
					// The room updates run to a few hundred characters; the profile JSON the client
					// dumps runs to hundreds of KB on a single line and can't hold this pattern.
					if (line.Length > MaxScannedLineLength) continue;

					Match m = lobbyIdRegex.Match(line);
					if (!m.Success) continue;

					// Last one in the chunk wins — including the all-zero guid, which is how the
					// client says you've left rather than moved.
					string id = m.Groups[1].Value.ToUpperInvariant();
					sawLobbyId = true;
					found = id == NullSessionId ? null : id;
				}

				lock (stateLock)
				{
					readPosition = startAt + consumed;

					// Only overwrite when this chunk actually said something about the room; a chunk
					// with no lobbyid line at all means "no news", not "you've left".
					if (sawLobbyId) sessionId = found;
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
