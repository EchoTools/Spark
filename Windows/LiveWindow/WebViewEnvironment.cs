using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Spark
{
	/// <summary>
	/// One shared CoreWebView2Environment for every in-window view.
	/// <para>
	/// A user-data folder can only back one environment; creating a second against the same folder
	/// fails, which silently left the chrome views blank. Sharing one also keeps all the views on a
	/// single browser process instead of one each.
	/// </para>
	/// </summary>
	public static class WebViewEnvironment
	{
		private static readonly object gate = new object();
		private static Task<CoreWebView2Environment> creation;

		public static Task<CoreWebView2Environment> GetAsync()
		{
			lock (gate)
			{
				return creation ??= CoreWebView2Environment.CreateAsync(null, UserDataFolder());
			}
		}

		private static string UserDataFolder()
		{
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"IgniteVR", "Spark", "WebViewDashboard");
		}
	}
}
