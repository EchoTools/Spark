using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Spark
{
	/// <summary>
	/// Turns the combat API's internal gear names into the names players see in game.
	/// <para>
	/// The API reports weapons, ordnance and tac mods by internal name — "assault", "det", "wraith" —
	/// and a kill's killed_with by the damage source, which is often not the weapon's own name at all:
	/// a Comet kill can arrive as "scout rifle explosion" or "scout_aoe", a Meteor kill as
	/// "rocket_aoe". The table here is the game's own (rad15\json\balance\mp_equipment.json), whose
	/// aliases list exactly those damage sources, so every one resolves to the weapon that caused it.
	/// </para>
	/// <para>
	/// It comes from the Quest APK's source copy rather than the compiled PC data, because only the
	/// source still has the cut gear — Starburst, Translocator, Attack Drone and the rest — sitting in
	/// it as commented-out entries, which compiling strips. Those entries carry "cut": true, and are
	/// otherwise exactly as the source has them.
	/// </para>
	/// </summary>
	public static class GearNames
	{
		private const string ResourceName = "Spark.mp_equipment.json";

		private static readonly Lazy<(Dictionary<string, string> exact, Dictionary<string, string> items)> table =
			new Lazy<(Dictionary<string, string>, Dictionary<string, string>)>(Load);

		/// <summary>
		/// The in-game name for <paramref name="apiName"/>, or <paramref name="apiName"/> unchanged when
		/// it isn't in the table — so a weapon added after this table was extracted still shows up,
		/// just under its raw name, instead of disappearing.
		/// </summary>
		public static string Display(string apiName)
		{
			if (string.IsNullOrWhiteSpace(apiName)) return apiName;

			string key = Normalise(apiName);
			if (table.Value.exact.TryGetValue(key, out string display)) return display;

			// A damage source is named after the item that caused it: every alias the game lists begins
			// with its item's internal name ("scout rifle explosion", "scout_aoe", "rocket_aoe",
			// "blaster_cone"). The cut gear comes with no aliases at all, so rather than guess which
			// variants the engine emits for it — magnum_aoe? magnum explosion? — apply that same rule.
			int space = key.IndexOf(' ');
			if (space > 0 && table.Value.items.TryGetValue(key.Substring(0, space), out display)) return display;

			return apiName;
		}

		/// <summary>
		/// Lookup key. Case-insensitive, and underscores, hyphens and spaces are treated alike: the
		/// table itself mixes "scout rifle explosion" with "scout_aoe", so the API can't be relied on to
		/// use whichever separator a given alias happens to be written with.
		/// </summary>
		private static string Normalise(string name)
		{
			char[] chars = name.Trim().ToLowerInvariant().Select(c => c == '_' || c == '-' ? ' ' : c).ToArray();
			return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
		}

		/// <summary>
		/// The table stores names the way the game's HUD shows them, in capitals. Spark's UI is title
		/// case throughout, so "STUN FIELD" becomes "Stun Field" to sit alongside everything else.
		/// </summary>
		private static string TitleCase(string upper)
		{
			return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(upper.ToLowerInvariant());
		}

		private static (Dictionary<string, string> exact, Dictionary<string, string> items) Load()
		{
			// exact: every name the table gives, aliases and display names included.
			// items: internal item names only — the words a damage source is allowed to start with.
			Dictionary<string, string> map = new Dictionary<string, string>();
			Dictionary<string, string> items = new Dictionary<string, string>();
			try
			{
				using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
				if (stream == null)
				{
					Logger.LogRow(Logger.LogType.Error, $"Gear name table missing from the build ({ResourceName}); showing API names.");
					return (map, items);
				}

				using StreamReader reader = new StreamReader(stream);
				JToken gear = JObject.Parse(reader.ReadToEnd())["gear_table"];

				foreach (string category in new[] { "weapons", "ordnances", "tacmods" })
				{
					if (gear?[category] is not JArray entries) continue;

					foreach (JToken item in entries)
					{
						string display = item["display_name"]?.ToString();
						if (string.IsNullOrWhiteSpace(display)) continue;
						display = TitleCase(display);

						string internalName = item["name"]?.ToString();
						if (!string.IsNullOrWhiteSpace(internalName)) items.TryAdd(Normalise(internalName), display);

						// The internal name, every damage-source alias, and the display name itself —
						// the last so an API that already sends "Pulsar" still comes out consistently.
						IEnumerable<string> keys = new[] { item["name"]?.ToString(), item["display_name"]?.ToString() }
							.Concat(item["aliases"] is JArray aliases ? aliases.Select(a => a.ToString()) : Enumerable.Empty<string>());

						foreach (string key in keys)
						{
							if (string.IsNullOrWhiteSpace(key)) continue;
							// First writer wins, so a later category can't silently repoint a name.
							map.TryAdd(Normalise(key), display);
						}
					}
				}
			}
			catch (Exception e)
			{
				Logger.LogRow(Logger.LogType.Error, $"Couldn't read the gear name table; showing API names.\n{e}");
			}

			return (map, items);
		}
	}
}
