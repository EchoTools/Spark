using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Spark
{
	/// <summary>
	/// One row in the reworked Event Log tab: a parsed game-clock time (when present), the message,
	/// and a best-effort category used to colour it.
	/// <para>
	/// The raw log is unstructured text from many call sites across the app (see LoggerEvents.cs and
	/// scattered LogRow(LogType.File, ...) calls) — there's no shared schema to key off. Every game-
	/// event line does follow one consistent shape ("{game_clock_display} - {message}", from
	/// LoggerEvents.Log), so lines matching that get a parsed time and are classified by keyword;
	/// everything else — camera-calibration diagnostics, system messages — falls through to a plain
	/// untagged row rather than being mis-classified.
	/// </para>
	/// </summary>
	public sealed class EventLogEntry
	{
		private static readonly Regex TimedLine = new Regex(@"^(\d{2}:\d{2}\.\d{2})\s*-\s*(.*)$", RegexOptions.Compiled);

		public string Time { get; }
		public string Message { get; }
		public string Category { get; }

		/// <summary>
		/// Resolved once, at classification time, from whatever theme is current then — not a
		/// DynamicResource, so a later theme change won't retint rows already in the list. Rows are
		/// historical and scroll off quickly; keeping this simple beats chasing live re-tinting for a
		/// scrolling log.
		/// </summary>
		public Brush Accent { get; }

		private EventLogEntry(string time, string message, string category, Brush accent)
		{
			Time = time;
			Message = message;
			Category = category;
			Accent = accent;
		}

		/// <summary>Classifies one already-trimmed log line. Never returns null.</summary>
		public static EventLogEntry Parse(string line)
		{
			Match match = TimedLine.Match(line);
			string time = match.Success ? match.Groups[1].Value : "";
			string message = match.Success ? match.Groups[2].Value : line;

			(string category, string brushKey) = Classify(message);
			Brush accent = Application.Current.TryFindResource(brushKey) as Brush ?? Brushes.Gray;
			return new EventLogEntry(time, message, category, accent);
		}

		/// <summary>
		/// Ordered keyword rules — first match wins. Ordering matters where phrases overlap (e.g.
		/// "Player Left" vs "Player switched"), so more specific phrases are checked first.
		/// </summary>
		private static (string category, string brushKey) Classify(string message)
		{
			if (Contains(message, "scored at") || Contains(message, "Goal angle") || Regex.IsMatch(message, @"^ORANGE:\s*\d+\s*BLUE:\s*\d+"))
				return ("GOAL", "ControlAccent");

			if (Contains(message, "made a save"))
				return ("SAVE", "StatusGood");

			if (Contains(message, "Player Joined") || message == "Joined game")
				return ("JOIN", "StatusGood");

			if (Contains(message, "Player Left") || message == "Left game")
				return ("LEAVE", "StatusWarn");

			if (Contains(message, "switched to") && Contains(message, "team"))
				return ("TEAM", "StatusWarn");

			if (Contains(message, "stunned"))
				return ("STUN", "TeamOrange");

			if (Contains(message, "threw the disk"))
				return ("THROW", "TeamBlue");

			if (Contains(message, "joust time"))
				return ("JOUST", "TeamBlue");

			if (Contains(message, "boosted to"))
				return ("BOOST", "TeamBlue");

			if (Contains(message, "made a catch") || Contains(message, "received a pass") || Contains(message, "intercepted a throw") || Contains(message, "turned over the disk") || Contains(message, "took a shot"))
				return ("PLAY", "TextDim");

			if (Contains(message, "paused the game") || Contains(message, "requested a pause") || Contains(message, "unpaused the game") || Contains(message, "restart request"))
				return ("MATCH", "StatusWarn");

			if (Contains(message, "used the") && Contains(message, "emote"))
				return ("EMOTE", "TextFaint");

			if (Contains(message, "ping went above"))
				return ("WARN", "StatusBad");

			if (Contains(message, "changed the private match rules"))
				return ("RULES", "StatusWarn");

			if (Contains(message, "abused their playspace"))
				return ("WARN", "StatusBad");

			return (null, "TextDim");
		}

		private static bool Contains(string haystack, string needle)
		{
			return haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
