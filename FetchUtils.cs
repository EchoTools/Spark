using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Spark
{
	public static class FetchUtils
	{
		/// <summary>
		/// Client used for one-off requests
		/// </summary>
		public static readonly HttpClient client = new HttpClient();

		/// <summary>
		/// Generic method for getting data from a web url
		/// </summary>
		/// <param name="headers">Key-value pairs for headers. Leave null if none.</param>
		public static void GetRequestCallback(string uri, Dictionary<string, string> headers, Action<string> callback)
		{
			Task.Run(async () =>
			{
				string resp = await GetRequestAsync(uri, headers);
				callback(resp);
			});
		}

		/// <summary>
		/// Generic method for getting data from a web url
		/// </summary>
		/// <param name="uri">The URL to GET</param>
		/// <param name="headers">Key-value pairs for headers. Leave null if none.</param>
		public static async Task<string> GetRequestAsync(string uri, Dictionary<string, string> headers = null)
		{
			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);

				if (headers != null)
				{
					foreach ((string key, string value) in headers)
					{
						request.Headers.Add(key, value);
					}
				}

				HttpResponseMessage response = await client.SendAsync(request);

				response.EnsureSuccessStatusCode();

				return await response.Content.ReadAsStringAsync();
			}
			catch (Exception)
			{
				return string.Empty;
			}
		}
		
		public static async Task<Stream> GetRequestAsyncStream(string uri, Dictionary<string, string> headers)
		{
			try
			{
				HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
				if (headers != null)
				{
					foreach ((string key, string value) in headers)
					{
						request.Headers.Add(key, value);
					}
				}

				request.Headers.UserAgent.ParseAdd($"Spark/{Program.AppVersionString()}");
				HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
				response.EnsureSuccessStatusCode();
				return await response.Content.ReadAsStreamAsync();
			}
			catch (Exception e)
			{
				Console.WriteLine($"Can't get data\n{e}");
			}
			
			return null;
		}

		public static async Task DownloadFileAsync(string uri, string outputPath, Action<int> onProgress)
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
			request.Headers.UserAgent.ParseAdd("Spark-Updater");
			using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();

			long? totalBytes = response.Content.Headers.ContentLength;
			using Stream contentStream = await response.Content.ReadAsStreamAsync();
			using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

			byte[] buffer = new byte[8192];
			long totalRead = 0;
			int bytesRead;

			while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
			{
				await fileStream.WriteAsync(buffer, 0, bytesRead);
				totalRead += bytesRead;
				if (totalBytes.HasValue && onProgress != null)
				{
					int percentage = (int)((totalRead * 100) / totalBytes.Value);
					onProgress(percentage);
				}
			}
		}

		/// <summary>
		/// Generic method for posting data to a web url
		/// </summary>
		/// <param name="headers">Key-value pairs for headers. Leave null if none.</param>
		public static void PostRequestCallback(string uri, Dictionary<string, string> headers, string body, Action<string> callback)
		{
			Task.Run(async () =>
			{
				string resp = await PostRequestAsync(uri, headers, body);
				callback?.Invoke(resp);
			});
		}

		/// <summary>
		/// Generic method for posting data to a web url
		/// </summary>
		/// <param name="headers">Key-value pairs for headers. Leave null if none.</param>
		public static async Task<string> PostRequestAsync(string uri, Dictionary<string, string> headers, string body, bool readResponse = true)
		{
			try
			{
				if (headers != null)
				{
					foreach (KeyValuePair<string, string> header in headers)
					{
						client.DefaultRequestHeaders.Remove(header.Key);
						client.DefaultRequestHeaders.Add(header.Key, header.Value);
					}
				}
				StringContent content = new StringContent(body, Encoding.UTF8, "application/json");
				HttpResponseMessage response = await client.PostAsync(uri, content);
				if (readResponse)
				{
					return await response.Content.ReadAsStringAsync();
				}

				return string.Empty;
			}
			catch (Exception e)
			{
				Console.WriteLine($"Can't get data\n{e}");
				return string.Empty;
			}
		}
	}
}