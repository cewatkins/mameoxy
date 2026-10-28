using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using MonoTorrent;
using MonoTorrent.Client;

namespace dome_bt
{
	public class TorrentInfo
	{
		public string Core;
		public string Type;
		public string Name;
		public string Version;
		public string Hash;
		public string Magnet;
		public string DatFile;

		public Torrent Torrent;
		public TorrentManager TorrentManager;

		public MagnetLink MagnetLink;
	}

	public class Globals
	{
		public static string AssemblyVersion;

		public static HttpClient HttpClient;

		public static string DirectoryRoot;
		public static string DirectoryCache;
		public static string DirectoryDownloads;

		public static List<TorrentInfo> TorrentInfos = new List<TorrentInfo>();

		public const string DefaultListenAddress = "http://localhost:12381/";

		public static string ListenAddress = DefaultListenAddress;

		public static BitTorrent BitTorrent;

		public static DateTime StartTime = DateTime.Now;
		public static DateTime ReadyTime = StartTime;

		public static int Pid = Environment.ProcessId;

		public static List<string> Cores = new List<string>();

		public static Config Config;

		public static Dictionary<string, string> NameTypeLookup = new Dictionary<string, string>()
		{
			{ "ROMs (merged)", "mr" },
			{ "CHDs (merged)", "md" },
			{ "Software List ROMs (merged)", "sr" },
			{ "Software List CHDs (merged)", "sd" },
		};

		static Globals()
		{
			Version assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? throw new ApplicationException("Can't find Assembly Version");
			AssemblyVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}";

			DirectoryRoot = Environment.CurrentDirectory;
			Directory.CreateDirectory(DirectoryRoot);

			DirectoryCache = Path.Combine(DirectoryRoot, "_CACHE");
			Directory.CreateDirectory(DirectoryCache);

			DirectoryDownloads = Path.Combine(DirectoryRoot, "_DOWNLOADS");
			Directory.CreateDirectory(DirectoryDownloads);

			HttpClient = new HttpClient(new HttpClientHandler { UseCookies = false });
			HttpClient.DefaultRequestHeaders.Add("User-Agent", $"dome-bt/{AssemblyVersion} (https://github.com/sam-ludlow/dome-bt)");
			HttpClient.Timeout = TimeSpan.FromSeconds(180);     // metdata 3 minutes

			Config = new Config(Path.Combine(DirectoryRoot, "_config.txt"));

			//
			// The HTTP listener port is fixed at 12381 for MAME-AO compatibility,
			// it can be overridden for testing / multiple instances.
			//
			string httpPort = Environment.GetEnvironmentVariable("MAMEAO_BT_HTTP_PORT");
			if (string.IsNullOrWhiteSpace(httpPort) == true)
				httpPort = Config.Get("http-port-number");

			if (int.TryParse(httpPort, out int parsedPort) == true && parsedPort > 0 && parsedPort <= 65535)
				ListenAddress = $"http://localhost:{parsedPort}/";
		}
	}

	public class Processor
	{
		public Processor()
		{
			Tools.Log($"DOME-BT {Globals.AssemblyVersion} (MAME-AO BitTorrent client)");
			Tools.Log($"PID: {Globals.Pid}\tWorking Directory: {Globals.DirectoryRoot}");
			Tools.Log($"API: {Globals.ListenAddress}api/info");

			if (Globals.Config.ContainsKey("cores") == true)
			{
				foreach (string core in Globals.Config.Get("cores").Split(','))
					Globals.Cores.Add(core.Trim());
			}
			else
			{
				Globals.Cores.Add("mame");
			}

			Tools.Log($"Cores: {String.Join(", ", Globals.Cores)}");
		}

		public async Task<int> RunAsync()
		{
			WebServer webServer = new WebServer();
			webServer.StartListener();

			int exitCode = 0;

			try
			{
				Globals.BitTorrent = new BitTorrent();
				await Globals.BitTorrent.RunAsync();

				if (Globals.BitTorrent.FatalError == true)
					exitCode = 1;
			}
			finally
			{
				webServer.StopListener();
			}

			return exitCode;
		}

		public async Task<int> ConvertAsync(string targetDirectory)
		{
			string config = Globals.Config.Get("magnets");
			if (config == null)
				throw new ApplicationException("magnets is missing from _config.txt");

			string[] urls = config.Split(',').Select(x => x.Trim()).ToArray();

			if (urls.Length != 5)
				throw new ApplicationException("magnets should be 5 items (mame, hbmame, fbneo, pinmame, pinball)");

			Dictionary<string, TorrentInfo[]> coreMagnetInfos = new Dictionary<string, TorrentInfo[]>()
			{
				{ "mame", PleasureDome.ParseMameMagentLink("mame", urls[0]) },
				{ "hbmame", PleasureDome.ParseMameMagentLink("hbmame", urls[1]) },
				{ "fbneo", PleasureDome.ParseFBNeoMagentLink(urls[2]) },
				{ "pinmame", PleasureDome.ParsePinMAMEMagentLink(urls[3]) },
				{ "pinball", PleasureDome.ParsePinballMagentLink(urls[4]) },
			};

			int newCount = 0;

			BitTorrent bitTorrent = new BitTorrent();

			bitTorrent.Setup(coreMagnetInfos.Values.Sum(x => x.Length));

			Tools.Heading("Add Magnets");

			foreach (string core in coreMagnetInfos.Keys)
			{
				foreach (var magnetInfo in coreMagnetInfos[core])
				{
					Tools.Log($"{magnetInfo.Name} ...");
					var torrentManager = await bitTorrent.Engine.AddAsync(magnetInfo.MagnetLink, Globals.DirectoryDownloads, bitTorrent.TorrentSettings);
					magnetInfo.Name = torrentManager.Name;
					Tools.Log($"...done {magnetInfo.Name}");
				}
			}

			Tools.Heading("Get Metadata");

			foreach (var torrentManager in bitTorrent.Engine.Torrents)
			{
				if (torrentManager.HasMetadata == false)
				{
					Tools.Log($"{torrentManager.Name} ...");
					await torrentManager.StartAsync();
					await torrentManager.WaitForMetadataAsync();
					await torrentManager.StopAsync();
					Tools.Log($"...done {torrentManager.Name}");

					++newCount;
				}
				else
				{
					Tools.Log($"{torrentManager.Name} ...have");
				}
			}

			Tools.Log("Stopping Engine ...");
			await bitTorrent.Engine.StopAllAsync();
			bitTorrent.Engine.Dispose();
			Tools.Log("...done");

			Tools.Heading("Save Payloads");

			Directory.CreateDirectory(targetDirectory);

			foreach (string core in coreMagnetInfos.Keys)
			{
				var array = new JArray();

				foreach (var magnetInfo in coreMagnetInfos[core])
				{
					Tools.Log($"{magnetInfo.Type}\t{magnetInfo.Name}\t{magnetInfo.Hash}");

					string sourceFilename = Path.Combine(Globals.DirectoryCache, "metadata", magnetInfo.Hash + ".torrent");

					string zipFilename = Path.Combine(targetDirectory, $"{magnetInfo.Hash}.torrent.zip");
					File.Delete(zipFilename);
					Tools.CompressSingleFile(sourceFilename, zipFilename);

					dynamic json = new JObject();

					json.name = magnetInfo.Name;
					json.type = magnetInfo.Type;
					json.version = magnetInfo.Version;
					json.hash = magnetInfo.Hash;
					json.magnet = magnetInfo.Magnet;
					if (magnetInfo.DatFile != null)
						json.dat = magnetInfo.DatFile.Replace(" ", "%20");
					json.torrent = System.Convert.ToBase64String(File.ReadAllBytes(zipFilename));

					array.Add(json);

					File.Delete(zipFilename);
				}

				string targetFilename = Path.Combine(targetDirectory, core + ".json");
				File.Delete(targetFilename);
				File.WriteAllText(targetFilename, array.ToString(), Encoding.ASCII);

				Tools.Log($"{core}\t{targetFilename}");
			}

			return newCount;
		}
	}
}
