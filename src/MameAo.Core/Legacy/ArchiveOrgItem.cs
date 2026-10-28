using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Spludlow.MameAO.Platform;

namespace Spludlow.MameAO
{
	public class ArchiveOrgAuth
	{
		public static string CacheFilename;

		private static HttpClient HttpClient;
		private static CookieContainer CookieContainer;

		static ArchiveOrgAuth()
		{
			CacheFilename = Path.Combine(Globals.CacheDirectory, "archive.org-auth-cookie.txt");

			CookieContainer = new CookieContainer();
			var handler = new HttpClientHandler()
			{
				CookieContainer = CookieContainer
			};

			HttpClient = new HttpClient(handler);
			HttpClient.DefaultRequestHeaders.Add("User-Agent", $"mame-ao/{Globals.AssemblyVersion} (https://github.com/sam-ludlow/mame-ao)");

			ServicePointManager.FindServicePoint(new Uri("https://archive.org")).Expect100Continue = false;
		}

		/// <summary>
		/// Return an archive.org auth cookie, from (in order) the 90-day cache,
		/// the credential store (environment or 0600 file) or an interactive
		/// prompt when there is a terminal.
		///
		/// Credentials are never printed, and a missing credential is not fatal:
		/// the downloaders report 401/403 when a protected item is requested.
		/// </summary>
		public static string GetCookie()
		{
			if (File.Exists(CacheFilename) == true && (DateTime.Now - File.GetLastWriteTime(CacheFilename)) <= TimeSpan.FromDays(90))
				return File.ReadAllText(CacheFilename);

			string username = Credential("IA_USERNAME");
			string password = Credential("IA_PASSWORD");

			if (string.IsNullOrEmpty(username) == true || string.IsNullOrEmpty(password) == true)
			{
				if (Console.IsInputRedirected == true)
				{
					Tools.ConsoleHeading(2, new string[] {
						"No archive.org credentials configured.",
						"Downloads from archive.org may return 401/403 without them.",
						$"Set IA_USERNAME and IA_PASSWORD in the environment, or add them to {CredentialStorePath()} (mode 0600).",
						$"Cached auth cookie: {CacheFilename}",
					});
					return null;
				}

				Console.WriteLine();
				Tools.ConsoleHeading(2, new string[] {
					"If you want to use BitTorrents you can skip this step, press ENTER twice. To enable enter the command '.bt'",
					"Please enter your archive.org credentials. You can create an account here https://archive.org/account/signup",
					"Your username & password are not stored just the auth cookie which is kept here",
					CacheFilename,
					"If you have archive.org download problems with a status of 401/403 delete this file then re-start MAME-AO."
				});

				string cookie = null;

				do
				{
					Console.WriteLine();
					Console.WriteLine("Enter your Archive.org username (or just ENTER to skip):");
					username = Console.ReadLine();
					if (string.IsNullOrEmpty(username) == true)
						break;

					Console.WriteLine("Enter your Archive.org password:");
					password = ReadPassword();

					try
					{
						cookie = GetAuthCookie(username, password);
					}
					catch (HttpRequestException e)
					{
						if (e.Message.Contains("401") == true)
							Console.WriteLine("Bad credentials try again. Hit enter to skip (You won't be able to download).");
						else
							throw;
					}

				} while (cookie == null);

				if (cookie != null)
					File.WriteAllText(CacheFilename, cookie);

				if (File.Exists(CacheFilename) == true)
					return File.ReadAllText(CacheFilename);

				return null;
			}

			try
			{
				string cookie = GetAuthCookie(username, password);
				File.WriteAllText(CacheFilename, cookie);
				Console.WriteLine("archive.org: signed in using the configured credentials.");
				return cookie;
			}
			catch (Exception e)
			{
				Console.WriteLine($"!!! archive.org sign-in failed: {e.Message}");
				return null;
			}
		}

		private static string Credential(string name)
		{
			if (PlatformServices.IsInitialised == true)
				return PlatformServices.Current.Credentials.Get(name);

			return Environment.GetEnvironmentVariable(name);
		}

		private static string CredentialStorePath()
		{
			if (PlatformServices.IsInitialised == true)
				return PlatformServices.Current.Credentials.StorePath;

			return "(credential store unavailable)";
		}

		private static string ReadPassword()
		{
			try { return Tools.ReadPassword(); }
			catch (Exception) { return Console.ReadLine(); }
		}

		private static string GetAuthCookie(string username, string password)
		{
			string token;

			using (Task<HttpResponseMessage> responseMessageTask = HttpClient.GetAsync("https://archive.org/services/csrf-token"))
			{
				responseMessageTask.Wait();

				HttpResponseMessage responseMessage = responseMessageTask.Result;
				responseMessage.EnsureSuccessStatusCode();

				Task<string> bodyTask = responseMessage.Content.ReadAsStringAsync();
				bodyTask.Wait();

				dynamic body = JsonConvert.DeserializeObject<dynamic>(bodyTask.Result);

				if ((bool)body.success != true)
					throw new ApplicationException("Archive.org auth get token not success.");

				token = (string)body.value.token;
			}

			dynamic payload = new JObject();

			payload.username = username;
			payload.password = password;
			payload.remember = "true";
			payload.t = token;

			using (var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://archive.org/services/account/login/"))
			{
				requestMessage.Headers.Add("x-csrf-token", token);

				requestMessage.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

				using (Task<HttpResponseMessage> requestTask = HttpClient.SendAsync(requestMessage))
				{
					requestTask.Wait();
					HttpResponseMessage responseMessage = requestTask.Result;

					if (responseMessage.StatusCode != HttpStatusCode.OK)
						throw new HttpRequestException("Dummy 401");
				}
			}

			List<string> cookies = new List<string>();
			foreach (Cookie cookie in CookieContainer.GetCookies(new Uri("https://archive.org")))
				cookies.Add($"{cookie.Name}={cookie.Value}");

			return String.Join("; ", cookies.ToArray());
		}
	}

	public class ArchiveOrgFile
	{
		public ArchiveOrgFile(dynamic file)
		{
			name = file.name;
			size = Int64.Parse((string)file.size);
			sha1 = file.sha1;
			mtime = Tools.FromEpochDate((string)file.mtime);
		}

		public string name { get; set; }
		public long size { get; set; }
		public string sha1 { get; set; }
		public DateTime mtime { get; set; }
	}

	public class ArchiveOrgItem
	{
		public string Key;

		public string SubDirectory;
		public string Tag;

		public Dictionary<string, ArchiveOrgFile> Files = null;

		public string UrlDetails;
		public string UrlMetadata;
		public string UrlDownload;

		public string Title;
		public DateTime ItemLastUpdated;

		public string Status = "";

		public bool DontCache = false;
		public bool DontIgnore = false;

		private readonly List<string> AcceptedExtentions = new List<string>(new string[] { ".zip", ".chd" });

		public ArchiveOrgItem(string key, string subDirectory, string tag)
		{
			Key = key;
			SubDirectory = subDirectory;
			Tag = tag;

			UrlDetails = $"https://archive.org/details/{Key}";
			UrlMetadata = $"https://archive.org/metadata/{Key}";
			UrlDownload = $"https://archive.org/download/{Key}";
		}

		public ArchiveOrgFile GetFile(string key)
		{
			if (Files == null)
				Initialize();

			if (key == null)
				return null;

			if (Files.ContainsKey(key) == false)
				return null;

			return Files[key];
		}

		public static ArchiveOrgItem[] GetItems(ItemType itemType, string tag)
		{
			List<ArchiveOrgItem> results = new List<ArchiveOrgItem>();

			foreach (string tagQuery in new string[] { tag, "*" })
			{
				foreach (ArchiveOrgItem sourceItem in Globals.ArchiveOrgItems[itemType].Where(item => item.Tag == tagQuery))
					results.Add(sourceItem);
			}

			if (results.Count == 0)
				throw new ApplicationException($"Did not find any source sets: {itemType}");

			return results.ToArray();
		}

		public string DownloadLink(ArchiveOrgFile file)
		{
			return $"{UrlDownload}/{file.name}";
		}

		private void Initialize()
		{
			Files = new Dictionary<string, ArchiveOrgFile>();

			string json = DontCache == true ? Tools.Query(UrlMetadata) : Tools.FetchTextCached(UrlMetadata);

			if (json == null || json == "{}")
			{
				Status = "bad";
				Console.WriteLine($"WARNING archive.org item not available: {Key}");
				return;
			}

			dynamic metadata = JsonConvert.DeserializeObject<dynamic>(json);

			Title = (string)metadata.metadata.title;
			ItemLastUpdated = Tools.FromEpochDate((double)metadata.item_last_updated);

			foreach (dynamic file in metadata.files)
			{
				string name = (string)file.name;
				string extention = Path.GetExtension(name);

				if (file.sha1 == null)
					continue;

				if (name.StartsWith($"{Key}_") == true)
					continue;

				if (DontIgnore == false)
				{
					if (SubDirectory != null && name.StartsWith(SubDirectory) == false)
						continue;

					if (AcceptedExtentions.Contains(extention) == false && name != "_manifest-sha1.txt")
						continue;
				}

				if (SubDirectory != null)
					name = name.Substring(SubDirectory.Length);

				name = name.Substring(0, name.Length - extention.Length);

				Files.Add(name, new ArchiveOrgFile(file));
			}

			Status = "ok";
		}

		public Dictionary<string, long> GetZipContentsSizes(ArchiveOrgFile file, int offset, int chopEnd)
		{
			string url = DownloadLink(file) + "/";

			string html = DontCache == true ? Tools.Query(url) : Tools.FetchTextCached(url);

			if (html == null)
			{
				Console.WriteLine($"!!! Can not get ZIP contents: {url}");
				return null;
			}

			Dictionary<string, long> result = new Dictionary<string, long>();

			using (StringReader reader = new StringReader(html))
			{
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					line = line.Trim();
					if (line.StartsWith("<tr><td><a href=\"//archive.org/download/") == false)
						continue;

					string[] parts = line.Split(new char[] { '<' });

					string name = null;
					string size = null;

					foreach (string part in parts)
					{
						int index = part.LastIndexOf(">");
						if (index == -1)
							continue;
						++index;

						if (part.StartsWith("a href=") == true)
							name = part.Substring(index);

						if (part.StartsWith("td id=\"size\"") == true)
							size = part.Substring(index);
					}

					if (name == null || size == null)
						throw new ApplicationException($"Bad html line {line}");

					if (offset != 0)
						name = name.Substring(offset);

					if (chopEnd != 0)
						name = name.Substring(0, name.Length - chopEnd);

					result.Add(name, Int64.Parse(size));
				}
			}

			return result;
		}

		public static void UtilGetItemsinSubject()
		{
			string subject = "noaen";
			string search = "redump";

			string url = $"https://archive.org/advancedsearch.php?q=subject:%22{subject}%22+{search}&fl[]=identifier&fl[]=title&sort[]=titleSorter%20asc&rows=1000&page=1&output=json";

			string json = Tools.FetchTextCached(url) ?? throw new ApplicationException("Can't get subject JSON");
			dynamic metadata = JObject.Parse(json);
			JArray docs = metadata.response.docs;

			SortedDictionary<string, string> results = new SortedDictionary<string, string>();

			foreach (dynamic doc in docs)
			{
				results.Add((string)doc.identifier, (string)doc.title);
			}

			Tools.PopText(metadata.ToString());
			Tools.PopText(String.Join(Environment.NewLine, results.Select(pair => $"{pair.Key}"))); //	\t{pair.Value}
		}

	}
}
