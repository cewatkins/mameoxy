using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Spludlow.MameAO
{
	public class BitTorrentFile
	{
		public string Filename;
		public long Length;

		public BitTorrentFile(string filename, long length)
		{
			Filename = filename;
			Length = length;
		}
	}

	public class BitTorrent
	{
		public static string ClientUrl = "http://localhost:12381";

		/// <summary>
		/// The packaged client is a managed apphost: "dome-bt.exe" on Windows,
		/// "dome-bt" on Linux. It is looked up next to mame-ao, or in the
		/// BitTorrent directory, or wherever MAME_AO_DOME_BT points.
		/// </summary>
		public static string ExecutableName => OperatingSystem.IsWindows() ? "dome-bt.exe" : "dome-bt";

		/// <summary>Port the child client binds; must match ClientUrl.</summary>
		public static int ClientPort
		{
			get
			{
				string configured = Environment.GetEnvironmentVariable("MAMEAO_BT_HTTP_PORT");

				if (int.TryParse(configured, out int port) == true && port > 0 && port <= 65535)
					return port;

				return 12381;
			}
		}

		/// <summary>
		/// Where the Linux client binary is, or null when nothing was found.
		/// </summary>
		public static string FindExecutable()
		{
			string configured = Environment.GetEnvironmentVariable("MAME_AO_DOME_BT");

			string[] candidates = new string[]
			{
				configured,
				Path.Combine(AppContext.BaseDirectory, ExecutableName),
				Path.Combine(Globals.BitTorrentDirectory ?? "", ExecutableName),
			};

			foreach (string candidate in candidates)
			{
				if (string.IsNullOrWhiteSpace(candidate) == false && File.Exists(candidate) == true)
					return Path.GetFullPath(candidate);
			}

			return null;
		}


		public static TimeSpan RestartLimit = TimeSpan.FromMinutes(5);

		public static dynamic DomeInfo()
		{
			try
			{
				string info = Tools.Query($"{ClientUrl}/api/info");
				return JsonConvert.DeserializeObject<dynamic>(info);
			}
			catch (TaskCanceledException)
			{
				return null;
			}
			catch (HttpRequestException)
			{
				return null;
			}
		}

		public static bool IsInstalled()
		{
			string exeFilename = FindExecutable();

			return exeFilename != null;
		}

		public static void Remove()
		{
			dynamic info = DomeInfo();

			if (info != null)
			{
				int pid = (int)info.pid;

				Console.Write("Killing DOME-BT...");
				using (Process startingProcess = Process.GetProcessById(pid))
				{
					startingProcess.Kill();
					startingProcess.WaitForExit();
				}
				Console.WriteLine("...done");
			}

			Console.Write("Removing DOME-BT...");
			try
			{
				DeleteBitTorrentDirectory(Globals.BitTorrentDirectory);
			}
			catch (UnauthorizedAccessException e)
			{
				throw new ApplicationException("Looks like DOME-BT is currently running, please kill all DOME-BT processes and try again, " + e.Message, e);
			}

			Directory.CreateDirectory(Globals.BitTorrentDirectory);
			Console.WriteLine("...done");

			Globals.BitTorrentAvailable = false;
		}

		public static void Initialize()
		{
			Tools.ConsoleHeading(2, "DOME-BT Bit Torrents");

			if (OperatingSystem.IsWindows() == false)
			{
				InitializeLinux();
				return;
			}

			GitHubRepo repo = Globals.GitHubRepos["dome-bt"];

			string zipName = $"dome-bt-{repo.tag_name}.zip";

			string exeFilename = Path.Combine(Globals.BitTorrentDirectory, "dome-bt.exe");

			string remoteVersion = repo.tag_name;

			string localVersion = null;
			if (File.Exists(exeFilename) == true)
			{
				Version version = AssemblyName.GetAssemblyName(exeFilename).Version;
				localVersion = $"{version.Major}.{version.Minor}";

				Console.WriteLine($"DOME-BT Installed version:	{localVersion}");
			}

			dynamic info = DomeInfo();

			if (info != null && (string)info.version == remoteVersion)
			{
				Console.WriteLine($"DOME-BT Already running {info.version}");
			}
			else
			{
				if (info != null)
				{
					int pid = (int)info.pid;

					Console.Write("Killing DOME-BT...");
					using (Process startingProcess = Process.GetProcessById(pid))
					{
						startingProcess.Kill();
						startingProcess.WaitForExit();
					}
					Console.WriteLine("...done");
				}

				if (localVersion == null || localVersion != remoteVersion)
				{
					Console.Write("Installing DOME-BT...");

					try
					{
						DeleteBitTorrentDirectory(Globals.BitTorrentDirectory);
					}
					catch (UnauthorizedAccessException e)
					{
						throw new ApplicationException("Looks like DOME-BT is currently running, please kill all DOME-BT processes and try again, " + e.Message, e);
					}

					Directory.CreateDirectory(Globals.BitTorrentDirectory);

					string zipFilename = Path.Combine(Globals.BitTorrentDirectory, zipName);

					Tools.Download(repo.Assets[zipName], zipFilename);

					ZipFile.ExtractToDirectory(zipFilename, Globals.BitTorrentDirectory);
					Console.WriteLine("...done");
				}

				Start();
			}

			Globals.BitTorrentAvailable = true;
		}

		/// <summary>
		/// Linux: the client ships with mame-ao (or is pointed at with
		/// MAME_AO_DOME_BT). There is nothing to download and nothing to install,
		/// so a missing client is a warning, not an error: archive.org downloads
		/// still work without it.
		/// </summary>
		private static void InitializeLinux()
		{
			string exeFilename = FindExecutable();

			if (exeFilename == null)
			{
				Tools.ConsoleHeading(3, new string[] {
					"BitTorrent client not found, CHD downloads will use archive.org only.",
					$"Place '{ExecutableName}' next to mame-ao, or set MAME_AO_DOME_BT to its path.",
				});

				Globals.BitTorrentAvailable = false;
				return;
			}

			Console.WriteLine($"DOME-BT client: {exeFilename}");

			ClientUrl = $"http://localhost:{ClientPort}";

			dynamic info = DomeInfo();

			if (info != null)
			{
				Console.WriteLine($"DOME-BT already running {info.version}");
				Globals.BitTorrentAvailable = true;
				return;
			}

			Start();
			Globals.BitTorrentAvailable = true;
		}

		public static void DeleteBitTorrentDirectory(string directory)
		{
			if (Directory.Exists(directory) == false)
				return;

			List<string> keepFilenames = new List<string>(new string[] { "_config.txt" });

			foreach (string filename in Directory.GetFiles(directory))
			{
				if (keepFilenames.Contains(Path.GetFileName(filename)) == true)
					continue;

				File.Delete(filename);
			}

			foreach (string subDirectory in Directory.GetDirectories(directory))
				Directory.Delete(subDirectory, true);
		}

		public static void Restart()
		{
			Stop();
			Start();
			WaitReady();
		}

		public static void WaitReady()
		{
			Console.Write("Waiting for DOME-BT to be ready ...");
			
			dynamic info = null;

			for (int retry = 0; retry < 12; ++retry)
			{
				info = DomeInfo();
				if (info != null)
					break;

				Console.Write("o");
				Thread.Sleep(2000);
			}

			if (info == null)
				throw new ApplicationException("Failed to connect to DOME-BT");

			while (true)
			{
				info = DomeInfo() ?? throw new ApplicationException("Lost connection to DOME-BT");

				if (info.ready_seconds != null)
					break;

				Console.Write(".");
				Thread.Sleep(4000);
			}

			Console.WriteLine("...done");
		}

		public static void Start()
		{
			string exeFilename = FindExecutable() ?? Path.Combine(Globals.BitTorrentDirectory, ExecutableName);

			Console.Write("Starting DOME-BT...");
			ProcessStartInfo startInfo = new ProcessStartInfo(exeFilename)
			{
				WorkingDirectory = Globals.BitTorrentDirectory,
				// On Unix the child is exec'd directly; the shell is only used on
				// Windows where the .exe path handling expects it.
				UseShellExecute = OperatingSystem.IsWindows(),
			};

			startInfo.Environment["MAMEAO_BT_HTTP_PORT"] = ClientPort.ToString();

			using (Process process = new Process())
			{
				process.StartInfo = startInfo;
				process.Start();

				if (process.HasExited == true)
					throw new ApplicationException($"DOME-BT exited imediatly after starting {process.ExitCode}.");
			}
			Console.WriteLine("...done");
		}

		public static void Stop()
		{
			dynamic info = DomeInfo();

			if (info != null)
			{
				Console.WriteLine("Asking DOME-BT to stop");
				Tools.Query($"{ClientUrl}/api/stop");

				int pid = (int)info.pid;

				Console.Write("Waiting for DOME-BT to stop ...");
				using (Process startingProcess = Process.GetProcessById(pid))
				{
					startingProcess.WaitForExit();
				}
				Console.WriteLine("...done");
			}
		}

		public static void StopTorrent(string hash)
		{
			dynamic info = DomeInfo();

			if (info != null)
			{
				Console.Write("Stopping Torrent...");
				Tools.Query($"{ClientUrl}/api/stop_torrent?hash={hash}");

				WaitTorrentState(hash, new string[] { "Stopped" });

				Console.WriteLine("...done");
			}
		}

		public static void StartTorrent(string hash)
		{
			dynamic info = DomeInfo();

			if (info != null)
			{
				Console.Write("Starting Torrent...");
				Tools.Query($"{ClientUrl}/api/start_torrent?hash={hash}");

				WaitTorrentState(hash, new string[] { "Downloading", "Seeding" });

				Console.WriteLine("...done");
			}
		}
		public static void WaitTorrentState(string hash, string[] states)
		{
			var statesSet = new HashSet<string>(states);

			string state = null;
			while (statesSet.Contains(state) == false)
			{
				if (state != null)
					Thread.Sleep(1000);

				dynamic info = DomeInfo();

				var torrent = ((JArray)info.torrents).SingleOrDefault(t => (string)t["hash"] == hash);

				state = torrent != null ? (string)torrent["state"] : "";

				Console.Write(".");
			}
		}

		public static void ReloadTorrent(string hash)
		{
			dynamic info = DomeInfo();

			if (info != null)
			{
				Console.Write("Reloading Torrent...");
				Tools.Query($"{ClientUrl}/api/reload_torrent?hash={hash}");

				WaitTorrentState(hash, new string[] { "Stopped" });

				Console.WriteLine("...done");
			}
		}


		public static void EnableCore(string coreName)
		{
			dynamic info = JsonConvert.DeserializeObject<dynamic>(Tools.Query($"{ClientUrl}/api/info"));

			List<string> btCores = ((JArray)info.cores).ToObject<List<string>>();

			if (btCores.Contains(coreName) == true)
				return;

			btCores.Add(coreName);
			string value = String.Join(", ", btCores);

			string filename = Path.Combine(Globals.BitTorrentDirectory, "_config.txt");
			var dictionary = Tools.LoadDictionary(filename);

			if (dictionary.ContainsKey("cores") == false)
				dictionary.Add("cores", value);
			else
				dictionary["cores"] = value;

			Tools.SaveDictionary(filename, dictionary);

			Tools.ConsoleHeading(2, new string[] { "Restarting BOME-BT to enable core, configuration updated.", filename });

			Restart();
		}

		public static readonly Dictionary<string, ItemType> ShortNameItemTypeLookup = new Dictionary<string, ItemType>()
		{
			{ "mr", ItemType.MachineRom },
			{ "md", ItemType.MachineDisk },
			{ "sr", ItemType.SoftwareRom },
			{ "sd", ItemType.SoftwareDisk },
		};

		public static Dictionary<ItemType, string> TorrentHashes(string coreName)
		{
			Dictionary<ItemType, string> result = new Dictionary<ItemType, string>();

			dynamic info = JsonConvert.DeserializeObject<dynamic>(Tools.Query($"{ClientUrl}/api/info"));

			foreach (dynamic torrent in info.torrents)
			{
				if (coreName != (string)torrent.core)
					continue;

				ItemType type = ShortNameItemTypeLookup[(string)torrent.type];
				result.Add(type, (string)torrent.hash);
			}
			
			return result;
		}

		public static JArray Files(string hash)
		{
			return JsonConvert.DeserializeObject<dynamic>(Tools.Query($"{ClientUrl}/api/files?hash={hash}"));
		}

		public static BitTorrentFile MachineRom(string core, string machine)
		{
			return Download($"{ClientUrl}/api/file?core={core}&machine={HttpUtility.UrlEncode(machine)}");
		}

		public static BitTorrentFile MachineDisk(string core, string machine, string disk)
		{
			return Download($"{ClientUrl}/api/file?core={core}&machine={HttpUtility.UrlEncode(machine)}&disk={HttpUtility.UrlEncode(disk)}");
		}

		public static BitTorrentFile SoftwareRom(string core, string list, string software)
		{
			return Download($"{ClientUrl}/api/file?core={core}&list={HttpUtility.UrlEncode(list)}&software={HttpUtility.UrlEncode(software)}");
		}

		public static BitTorrentFile SoftwareDisk(string core, string list, string software, string disk)
		{
			return Download($"{ClientUrl}/api/file?core={core}&list={HttpUtility.UrlEncode(list)}&software={HttpUtility.UrlEncode(software)}&disk={HttpUtility.UrlEncode(disk)}");
		}

		public static BitTorrentFile Download(string apiUrl)
		{
			long expectedSize = -1;

			float percent_complete_previous = -1.0f;

			DateTime changeTime = DateTime.Now;

			int restartAttempt = 1;

			while (true)
			{
				dynamic fileInfo;

				try
				{
					fileInfo = JsonConvert.DeserializeObject<dynamic>(Tools.Query(apiUrl));
				}
				catch (HttpRequestException e)
				{
					if (e.Message.Contains("404") == true)
						return null;
					else
						throw e;
				}

				if (expectedSize == -1)
				{
					expectedSize = (long)fileInfo.length;
					lock (Globals.WorkerTaskInfo)
						Globals.WorkerTaskInfo.BytesTotal = expectedSize;
				}

				float percent_complete = (float)fileInfo.percent_complete;

				lock (Globals.WorkerTaskInfo)
					Globals.WorkerTaskInfo.BytesCurrent = (long)(expectedSize / 100.0 * (long)percent_complete);

				TimeSpan waitSpan = DateTime.Now - changeTime;

				Console.WriteLine($"Torrent:\t{DateTime.Now}\t{(long)fileInfo.length}\t{percent_complete}\t{Math.Round(waitSpan.TotalSeconds, 0)}/{RestartLimit.TotalSeconds}\t{apiUrl}");

				if (percent_complete == 100.0f)
					return new BitTorrentFile((string)fileInfo.filename, (long)fileInfo.length);

				if (percent_complete == percent_complete_previous)
				{
					if (waitSpan > RestartLimit)
					{
						string hash = (string)fileInfo.torrent_hash;

						Tools.ConsoleHeading(2, new string[] {
							$"DOME-BT is not downloading. Restarting attempt: {restartAttempt}",
							hash
						});

						switch (restartAttempt++)
						{
							case 1:
								StopTorrent(hash);
								StartTorrent(hash);
								break;

							case 2:
								StopTorrent(hash);
								ReloadTorrent(hash);
								StartTorrent(hash);
								break;

							default:
								Restart();
								break;
						}

						changeTime = DateTime.Now;
					}
				}
				else
				{
					changeTime = DateTime.Now;
				}
				percent_complete_previous = percent_complete;

				Thread.Sleep(5000);
			}
		}
	}
}
