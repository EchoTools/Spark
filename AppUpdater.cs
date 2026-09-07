using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Spark
{
	/// <summary>
	/// Background-checks GitHub for a newer Spark release and hands back what was found so the
	/// caller can decide how to surface it (a popup, a footer badge, etc.) — this class does no UI.
	/// </summary>
	public static class AppUpdater
	{
		public class UpdateInfo
		{
			public string Version;
			public string Changelog;
			public string DownloadUrl;
			public string FileName;
		}

		/// <summary>The most recently found update, if any — kept so a dismissed prompt can be reopened later.</summary>
		public static UpdateInfo PendingUpdate { get; private set; }

		/// <summary>Checks the latest GitHub release for this repo. Returns null if up to date, ignored, or the check failed.</summary>
		public static async Task<UpdateInfo> CheckForUpdatesAsync()
		{
			try
			{
				using (var client = new WebClient())
				{
					client.Headers.Add("User-Agent", "Spark-Updater");
					client.Headers.Add("Accept", "application/vnd.github.v3+json");

					string latestReleaseUrl = "https://api.github.com/repos/heisthecat31/Spark/releases/latest";
					string json = await client.DownloadStringTaskAsync(latestReleaseUrl);
					var release = JObject.Parse(json);

					string titleName = release["name"]?.ToString();
					if (string.IsNullOrWhiteSpace(titleName))
					{
						titleName = release["tag_name"]?.ToString();
					}

					string latestVersionStr = "0.0.0";
					var match = System.Text.RegularExpressions.Regex.Match(titleName ?? "", @"\d+\.\d+(\.\d+)?");
					if (match.Success)
					{
						latestVersionStr = match.Value;
					}

					var currentAssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
					string currentVersionStr = $"{currentAssemblyVersion.Major}.{currentAssemblyVersion.Minor}.{currentAssemblyVersion.Build}";

					if (latestVersionStr.Equals(SparkSettings.instance.ignoredUpdateVersion, StringComparison.OrdinalIgnoreCase))
					{
						Logger.Error($"[Updater] Skipping update check because version {latestVersionStr} is marked as ignored.");
						return null;
					}

					if (!Version.TryParse(latestVersionStr, out Version latestVersion) ||
						!Version.TryParse(currentVersionStr, out Version currentVersion) ||
						latestVersion <= currentVersion)
					{
						return null;
					}

					var assets = release["assets"] as JArray;
					var targetAsset = assets?.FirstOrDefault(asset =>
					{
						string name = asset["name"]?.ToString();
						return name != null && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
					});

					string downloadUrl = targetAsset?["browser_download_url"]?.ToString();
					string installerFileName = targetAsset?["name"]?.ToString();

					if (string.IsNullOrEmpty(downloadUrl))
					{
						Logger.Error("[Updater] Newer release found but it has no .msi asset attached.");
						return null;
					}

					PendingUpdate = new UpdateInfo
					{
						Version = latestVersionStr,
						Changelog = release["body"]?.ToString() ?? "No release notes provided.",
						DownloadUrl = downloadUrl,
						FileName = installerFileName
					};
					return PendingUpdate;
				}
			}
			catch (Exception ex)
			{
				Logger.Error($"[Updater] Background update check failed: {ex.Message}\n{ex.StackTrace}");
				return null;
			}
		}
	}
}
