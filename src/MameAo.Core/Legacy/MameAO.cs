using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using System.IO;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Net;
using System.Linq;

using Spludlow.MameAO.Platform;

namespace Spludlow.MameAO
{
	public enum ItemType
	{
		MachineRom,
		MachineDisk,
		SoftwareRom,
		SoftwareDisk,
		Support,
	};

	public class DataQueryProfile
	{
		public string Key;
		public string Text;
		public string Decription;
	}

	public static class Globals
	{
		public class TaskInfo
		{
			public string Command = "";
			public long BytesCurrent = 0;
			public long BytesTotal = 0;
		}

		static Globals()
		{
			Version assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
			AssemblyVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}";

			HttpClient = new HttpClient(new HttpClientHandler { UseCookies = false });
			HttpClient.DefaultRequestHeaders.Add("User-Agent", $"mame-ao/{Globals.AssemblyVersion} (https://github.com/sam-ludlow/mame-ao)");
			HttpClient.Timeout = TimeSpan.FromSeconds(180);		// metdata 3 minutes
		}

		public static readonly int AssetDownloadTimeoutMilliseconds = 6 * 60 * 60 * 1000;	// assets 6 hours

		/// <summary>
		/// Loopback only by default. The host passes --listen to widen this; the
		/// web API can start downloads and launch processes, so binding 0.0.0.0
		/// without being asked would be negligent.
		/// </summary>
		public static string ListenAddress = "http://127.0.0.1:12380/";

		/// <summary>Set by the host when there is no display and no terminal.</summary>
		public static bool Headless = false;

		/// <summary>
		/// --mame-path / config MamePath / $MAME. Empty means "search PATH then
		/// Flatpak". Kept here so the web API and .core can report where MAME came
		/// from.
		/// </summary>
		public static string MamePath = "";

		/// <summary>
		/// owner/repo that publishes this port's Linux release tarballs, from
		/// MAME_AO_UPDATE_REPO. Empty means self-update is disabled: the port
		/// version (0.10) and upstream's tag scheme (1.185) are not comparable, so
		/// offering ".up" against upstream would download a Windows build.
		/// </summary>
		public static string UpdateRepo = Environment.GetEnvironmentVariable("MAME_AO_UPDATE_REPO") ?? "";

		public static long DownloadDotSize = 1024 * 1024;

		public static string AssemblyVersion;

		public static HttpClient HttpClient;
		public static string AuthCookie = null;

		public static string DisplayName = TimeZoneInfo.Local.StandardName;

		public static Dictionary<string, string> Config = new Dictionary<string, string>();

		public static string RootDirectory;
		public static string TempDirectory;
		public static string CacheDirectory;
		public static string ReportDirectory;
		public static string BitTorrentDirectory;
		public static string SnapDirectory;

		public static string MameArguments = "";

		public static bool LinkingEnabled = false;
		public static bool BitTorrentAvailable = false;

		public static Dictionary<ItemType, ArchiveOrgItem[]> ArchiveOrgItems = new Dictionary<ItemType, ArchiveOrgItem[]>();
		public static Dictionary<string, GitHubRepo> GitHubRepos = new Dictionary<string, GitHubRepo>();

		public static HashStore RomHashStore;
		public static HashStore DiskHashStore;

		public static HashSet<string> AllSHA1 = new HashSet<string>();

		public static MameAOProcessor AO;

		public static Artwork Artwork;
		public static Favorites Favorites;
		public static Genre Genre;
		public static MameChdMan MameChdMan;
		public static Reports Reports;
		public static Samples Samples;
		public static Settings Settings;
		public static WebServer WebServer = null;

		public static Task WorkerTask = null;
		public static TaskInfo WorkerTaskInfo = new TaskInfo();
		public static DataSet WorkerTaskReport;

		public static PhoneHome PhoneHome;

		public static ICore Core = null;

		public static string[] CoreNames = new string[] { "mame", "hbmame", "fbneo" };
	}

	public class MameAOProcessor
	{
		public IntPtr ConsoleHandle = IntPtr.Zero;

		private readonly string WelcomeText = @"@VERSION

$$\      $$\  $$$$$$\  $$\      $$\ $$$$$$$$\        $$$$$$\   $$$$$$\  
$$$\    $$$ |$$  __$$\ $$$\    $$$ |$$  _____|      $$  __$$\ $$  __$$\ 
$$$$\  $$$$ |$$ /  $$ |$$$$\  $$$$ |$$ |            $$ /  $$ |$$ /  $$ |
$$\$$\$$ $$ |$$$$$$$$ |$$\$$\$$ $$ |$$$$$\          $$$$$$$$ |$$ |  $$ |
$$ \$$$  $$ |$$  __$$ |$$ \$$$  $$ |$$  __|         $$  __$$ |$$ |  $$ |
$$ |\$  /$$ |$$ |  $$ |$$ |\$  /$$ |$$ |            $$ |  $$ |$$ |  $$ |
$$ | \_/ $$ |$$ |  $$ |$$ | \_/ $$ |$$$$$$$$\       $$ |  $$ | $$$$$$  |
\__|     \__|\__|  \__|\__|     \__|\________|      \__|  \__| \______/ 

       Please wait the first time it has to prepare the data

         The Web User Interface will pop up when ready

              See the README for more information
             https://github.com/sam-ludlow/mame-ao

";

		public MameAOProcessor(string rootDirectory)
		{
			Globals.RootDirectory = rootDirectory;
			Directory.CreateDirectory(Globals.RootDirectory);

			Globals.TempDirectory = Path.Combine(Globals.RootDirectory, "_TEMP");
			Directory.CreateDirectory(Globals.TempDirectory);

			Globals.CacheDirectory = Path.Combine(Globals.TempDirectory, "CACHE");
			Directory.CreateDirectory(Globals.CacheDirectory);

			Globals.ReportDirectory = Path.Combine(Globals.RootDirectory, "_REPORTS");
			Directory.CreateDirectory(Globals.ReportDirectory);

			Globals.SnapDirectory = Path.Combine(Globals.RootDirectory, "_SNAP");
		}

		public void Run()
		{
			try
			{
				Initialize();

				Shell();
			}
			catch (Exception e)
			{
				Tools.ReportError(e, "FATAL ERROR", true);
			}
		}



		public void Initialize()
		{
			ConsoleHandle = Tools.GetConsoleWindowSafe();

			string title = $"MAME-AO {Globals.AssemblyVersion}";

			if (ConsoleHandle == IntPtr.Zero)
				Console.WriteLine($"{title} (No Console Window)");
			else
				Console.Title = title;

			Console.Write(WelcomeText.Replace("@VERSION", Globals.AssemblyVersion));
			Tools.ConsoleHeading(1, "Initializing");

			Globals.Settings = new Settings();

			string configFilename = Path.Combine(Globals.RootDirectory, "_config.txt");
			Globals.Config = Tools.LoadDictionary(configFilename);

			Globals.MameArguments = Globals.Config.ContainsKey("MameArguments") == true ? Globals.Config["MameArguments"] : "";

			// The host's --mame-path wins over the config file; an empty value means
			// "search PATH, then Flatpak".
			if (string.IsNullOrWhiteSpace(Globals.MamePath) == true && Globals.Config.ContainsKey("MamePath") == true)
				Globals.MamePath = Globals.Config["MamePath"];

			MameProvider.ExplicitPath = Globals.MamePath;

			if (Globals.Config.ContainsKey("MameArguments") == true && Globals.Headless == true)
				Console.WriteLine("NOTE: headless mode adds -video none -sound none on top of MameArguments.");

			if (Globals.Config.ContainsKey("BitTorrentUrl") == true)
				BitTorrent.ClientUrl = Globals.Config["BitTorrentUrl"];

			if (Globals.Config.ContainsKey("DisplayName") == true)
				Globals.DisplayName = Globals.Config["DisplayName"];

			//
			// Fixes
			//

			// ....

			//
			// Symbolic Links check
			//

			string linkFilename = Path.Combine(Globals.RootDirectory, @"_LINK_TEST.txt");
			string targetFilename = Path.Combine(Globals.RootDirectory, @"_TARGET_TEST.txt");

			File.Delete(linkFilename);
			File.WriteAllText(targetFilename, "TEST");

			Tools.LinkFiles(new string[][] { new string[] { linkFilename, targetFilename } });

			Globals.LinkingEnabled = File.Exists(linkFilename);

			File.Delete(linkFilename);
			File.Delete(targetFilename);

			if (Globals.LinkingEnabled == false)
				Tools.ConsoleHeading(3, "!!! You can save a lot of disk space by enabling symbolic links, see the README.");
			else
				Tools.ConsoleHeading(3, "Symbolic links are enabled, saving disk space.");

			//
			// GitHub Repos
			//

			string[] updateRepo = Globals.UpdateRepo.Split('/');
			if (updateRepo.Length == 2 && updateRepo[0].Length > 0 && updateRepo[1].Length > 0)
				Globals.GitHubRepos.Add("mame-ao", new GitHubRepo(updateRepo[0], updateRepo[1]));

			Globals.GitHubRepos.Add("dome-bt", new GitHubRepo("sam-ludlow", "dome-bt"));

			Globals.GitHubRepos.Add("mame", new GitHubRepo("mamedev", "mame"));

			Globals.GitHubRepos.Add("MAME_Dats", new GitHubRepo("AntoPISA", "MAME_Dats"));
			Globals.GitHubRepos.Add("MAME_SupportFiles", new GitHubRepo("AntoPISA", "MAME_SupportFiles"));

			//
			// Bit Torrent
			//

			Globals.BitTorrentDirectory = Globals.Config.ContainsKey("BitTorrentPath") == true ? Globals.Config["BitTorrentPath"] : Path.Combine(Globals.RootDirectory, "_BT");
			Directory.CreateDirectory(Globals.BitTorrentDirectory);

			if (Globals.Config.ContainsKey("BitTorrentRestartMinutes") == true)
				BitTorrent.RestartLimit = TimeSpan.FromMinutes(Double.Parse(Globals.Config["BitTorrentRestartMinutes"]));

			if (BitTorrent.IsInstalled() == true)
				BitTorrent.Initialize();

			//
			//	Archive.Org Credentials
			//

			if (Globals.BitTorrentAvailable == false)
				Globals.AuthCookie = ArchiveOrgAuth.GetCookie();

			//
			// Archive.Org Items
			//

			// Machine ROM
			Globals.ArchiveOrgItems.Add(ItemType.MachineRom, new ArchiveOrgItem[] {
				new ArchiveOrgItem("mame-merged", "mame-merged/", null),
			});
			// Machine DISK
			Globals.ArchiveOrgItems.Add(ItemType.MachineDisk, new ArchiveOrgItem[] {
				new ArchiveOrgItem("MAME_0.225_CHDs_merged", null, null),
			});
			// Software ROM
			Globals.ArchiveOrgItems.Add(ItemType.SoftwareRom, new ArchiveOrgItem[] {
				new ArchiveOrgItem("mame-sl", "mame-sl/", null),
			});
			// Software DISK
			Globals.ArchiveOrgItems.Add(ItemType.SoftwareDisk, new ArchiveOrgItem[] {
				new ArchiveOrgItem("mame-software-list-chds-2", null, "*"),
			});
			// Support (Artwork & Samples)
			Globals.ArchiveOrgItems.Add(ItemType.Support, new ArchiveOrgItem[] {
				new ArchiveOrgItem("mame-support", "Support/", null),
			});

			//
			// Bits & Bobs
			//

			Globals.Samples = new Samples();
			Globals.Artwork = new Artwork();
			Globals.Genre = new Genre();

			Globals.Reports = new Reports();

			//
			// Default Core MAME
			//

			Cores.EnableCore("mame", Globals.Config.ContainsKey("MameVersion") == true ? Globals.Config["MameVersion"] : null);

			//
			// CHD Manager - chdman may be absent (MAME without the tools package);
			// that only matters when a disk image has to be hashed.
			//

			Globals.MameChdMan = new MameChdMan(MameProvider.Get());

			if (Globals.MameChdMan.IsAvailable == true)
				Console.WriteLine($"chdman: {Globals.MameChdMan.Command}");
			else
				Console.WriteLine("chdman: not found (disk image hashing will report an error when used)");

			//
			// Hash Stores
			//

			string directory = Globals.Config.ContainsKey("StorePathRom") == true ? Globals.Config["StorePathRom"] : Path.Combine(Globals.RootDirectory, "_STORE");
			Directory.CreateDirectory(directory);
			Console.Write($"Loading Hash Store {directory} ...");
			Globals.RomHashStore = new HashStore(directory, Tools.SHA1HexFile);
			Console.WriteLine("...done.");

			directory = Globals.Config.ContainsKey("StorePathDisk") == true ? Globals.Config["StorePathDisk"] : Path.Combine(Globals.RootDirectory, "_STORE_DISK");
			Directory.CreateDirectory(directory);
			Console.Write($"Loading Hash Store {directory} ...");
			Globals.DiskHashStore = new HashStore(directory, Globals.MameChdMan.Hash);
			Console.WriteLine("...done.");

			//
			// New version Check
			//

			string tag_name = Globals.GitHubRepos.TryGetValue("mame-ao", out GitHubRepo updateRepository) == true ? updateRepository.tag_name : null;
			if (Globals.GitHubRepos.ContainsKey("mame-ao") == false)
			{
				Tools.ConsoleHeading(2, new string[] {
					"Self-update is not configured for this build.",
					"Set MAME_AO_UPDATE_REPO=owner/repo to enable \".up\" with this port's release tarballs.",
				});
			}
			else if (string.IsNullOrEmpty(tag_name) == true)
			{
				Tools.ConsoleHeading(2, new string[] {
					$"Could not check {Globals.UpdateRepo} for a new mame-ao release (offline or GitHub rate limited).",
					updateRepository.LoadError ?? "",
				});
			}
			else if (Globals.AssemblyVersion != tag_name)
				Tools.ConsoleHeading(1, new string[] {
					"New MAME-AO version available",
					"",
					$"{Globals.AssemblyVersion} => {tag_name}",
					"",
					"Automatically update with shell command \".up\".",
				});

			Console.WriteLine("");
		}

		/// <summary>
		/// Open the UI in a browser if there is a desktop; otherwise print the URL.
		/// Headless operation must never fail because xdg-open/dbus is missing.
		/// </summary>
		public void OpenListenerUrl()
		{
			IShellOpener shell = PlatformServices.IsInitialised ? PlatformServices.Current.Shell : null;

			if (shell != null && shell.TryOpen(Globals.ListenAddress) == true)
				return;

			Console.WriteLine($"Open this in a browser: {Globals.ListenAddress}");
		}

		public void Shell()
		{
			Globals.WebServer = new WebServer();

			try
			{
				Globals.WebServer.StartListener();
			}
			catch (HttpListenerException e) when (e.Message.Contains("in use", StringComparison.OrdinalIgnoreCase) == true)
			{
				Tools.ConsoleHeading(1, new string[] {
					"MAME-AO is already running",
					"this is not permitted",
					$"({e.Message})",
				});
				throw;
			}
			catch (HttpListenerException e)
			{
				Tools.ConsoleHeading(1, new string[] {
					"MAME-AO could not start its web listener",
					Globals.ListenAddress,
					e.Message,
					"Choose another address with --listen, e.g. --listen http://127.0.0.1:12381/",
				});
				throw;
			}

			Tools.ConsoleHeading(1, new string[] {
				"Remote Listener ready for commands",
				Globals.ListenAddress,
				$"e.g. {Globals.ListenAddress}api/command?line=a2600 et -window"

			});
			Console.WriteLine("");

			OpenListenerUrl();

			Tools.ConsoleHeading(1, "Shell ready for commands");
			Console.WriteLine("");

			bool promptShown = true;

			while (true)
			{
				if (promptShown == true)
					Console.Write($"{Globals.Core.Name.ToUpper()} {Globals.Core.Version}> ");

				string line = Console.ReadLine();

				if (line == null)
				{
					// No stdin (systemd, docker, a terminal that went away). Keep the
					// web server alive; say so once instead of re-prompting forever.
					if (promptShown == true)
					{
						promptShown = false;
						Console.WriteLine();
						Console.WriteLine("No console input; command the web UI instead. Press Ctrl+C to stop.");
					}

					System.Threading.Thread.Sleep(1000);
					continue;
				}

				promptShown = true;

				line = line.Trim();

				if (line.Length == 0)
					continue;

				if (RunLineTask(line) == true)
					Globals.WorkerTask.Wait();
				else
					Console.WriteLine("BUSY!");
			}
		}

		public bool RunLineTask(string line)
		{
			if (Globals.WorkerTask != null && Globals.WorkerTask.Status != TaskStatus.RanToCompletion)
				return false;

			Tools.ConsoleToFront();

			Globals.WorkerTask = new Task(() => {
				try
				{
					Globals.PhoneHome = new PhoneHome(line);

					string machine_name = RunLine(line);

					string snapFilename = null;

					if (machine_name != null && Globals.Settings.Options["SnapHome"] == "Yes")
					{
						if (Globals.Settings.Options["Artwork"] == "No")
							snapFilename = Snap.CollectSnaps(machine_name);
						else
							Console.WriteLine("!!! Snap Home does not work with Artwork enabled.");
					}
					
					Globals.PhoneHome.Success(snapFilename);
				}
				catch (ApplicationException e)
				{
					Globals.PhoneHome.Error(e);

					Console.WriteLine();
					Console.WriteLine("!!! ERROR: " + e.Message);
					Console.WriteLine();
				}
				catch (Exception e)
				{
					Globals.PhoneHome.Error(e);

					Tools.ReportError(e, "WORKER ERROR", false);
				}
				finally
				{
					lock (Globals.WorkerTaskInfo)
					{
						Globals.WorkerTaskInfo.Command = "";
						Globals.WorkerTaskInfo.BytesCurrent = 0;
						Globals.WorkerTaskInfo.BytesTotal = 0;
					}
				}
			});

			lock (Globals.WorkerTaskInfo)
			{
				Globals.WorkerTaskInfo.Command = line;
				Globals.WorkerTaskInfo.BytesCurrent = 0;
				Globals.WorkerTaskInfo.BytesTotal = 0;
			}

			Globals.WorkerTask.Start();

			return true;
		}

		public string RunLine(string line)
		{
			LineArguments args = new LineArguments(line);
			string[] parts;

			string machine = "";
			string software = "";
			string arguments = "";

			if (args.First.StartsWith(".") == true)
			{
				switch (args.First)
				{
					case ".list":
						ListSavedState();
						return null;

					case ".import":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <source directory>");
						Import.ImportDirectory(parts[1]);
						return null;

					case ".up":
						SelfUpdate.Update(0);
						return null;

					case ".upany":
						SelfUpdate.Update(-1);
						return null;

					case ".favm":
					case ".favmx":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <Machine Name>");

						machine = parts[1].ToLower();

						Favorites.ValidateFavorite(machine, null, null);

						if (parts[0].EndsWith("x") == true)
							Globals.Favorites.RemoveMachine(machine);
						else
							Globals.Favorites.AddMachine(machine);

						return null;

					case ".favs":
					case ".favsx":
						parts = args.Arguments(4);
						if (parts.Length != 4)
							throw new ApplicationException($"Usage: {parts[0]} <Machine Name> <List Name> <Software Name>");

						machine = parts[1].ToLower();
						string list = parts[2].ToLower();
						software = parts[3].ToLower();

						Favorites.ValidateFavorite(machine, list, software);

						if (parts[0].EndsWith("x") == true)
							Globals.Favorites.RemoveSoftware(machine, list, software);
						else
							Globals.Favorites.AddSoftware(machine, list, software);

						return null;

					case ".export":
						parts = args.Arguments(3);
						if (parts.Length != 3)
							throw new ApplicationException($"Usage: {parts[0]} <type: MR, MD, SR, SD, *> <target directory>");

						arguments = parts[2];

						if (Directory.Exists(arguments) == false)
							throw new ApplicationException($"Export directory does not exist: \"{arguments}\".");

						switch (parts[1].ToUpper())
						{
							case "MR":
								Export.MachineRoms(arguments);
								break;
							case "MD":
								Export.MachineDisks(arguments);
								break;
							case "SR":
								Export.SoftwareRoms(arguments);
								break;
							case "SD":
								Export.SoftwareDisks(arguments);
								break;

							case "*":
								Export.MachineRoms(arguments);
								Export.MachineDisks(arguments);
								Export.SoftwareRoms(arguments);
								Export.SoftwareDisks(arguments);
								break;

							default:
								throw new ApplicationException("Export Unknown type not (MR, MD, SR, SD, *).");

						}
						return null;

					case ".report":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <Report Code>" + Environment.NewLine + Environment.NewLine +
								String.Join(Environment.NewLine, Globals.Reports.ReportTypeText()) + Environment.NewLine
								);

						if (Globals.Reports.RunReport(parts[1]) == false)
							throw new ApplicationException("Report Unknown type.");
						return null;

					case ".snap":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <target directory>");

						Mame.CollectSnaps(Path.GetDirectoryName(Globals.Core.Directory), parts[1]);
						return null;

					case ".svg":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <filename or directory>");

						Tools.Bitmap2SVG(parts[1]);
						return null;

					case ".ui":
						OpenListenerUrl();
						return null;

					case ".r":
						Globals.WebServer.RefreshAssets();
						return null;

					case ".readme":
						Tools.OpenUrl("https://github.com/sam-ludlow/mame-ao#mame-ao");
						return null;

					case ".valid":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <rom, disk>");

						switch (parts[1].ToUpper())
						{
							case "ROM":
								HashStore.ValidateHashStore(Globals.RomHashStore, "ROM");
								break;

							case "DISK":
								HashStore.ValidateHashStore(Globals.DiskHashStore, "DISK");
								break;

							default:
								throw new ApplicationException("Valid Unknown store type (row, disk).");
						}
						return null;

					case ".what":
						Tools.OpenUrl(Globals.ListenAddress + "api/what");
						return null;

					case ".set":
						parts = args.Arguments(3);
						if (parts.Length != 3)
							throw new ApplicationException($"Usage: {parts[0]} <key> <value>");
						Globals.Settings.Set(parts[1], parts[2]);
						return null;

					case ".dbm":
					case ".dbs":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <command text>");

						Database.ConsoleQuery(Globals.Core, parts[0].Substring(3), parts[1]);
						return null;

					case ".upload":
						parts = args.Arguments(5);
						if (parts.Length != 4 && parts.Length != 5)
							throw new ApplicationException($"Usage: {parts[0]} <type> <archive.org item name> <batch size> [asset name]");

						switch (parts[1].ToUpper())
						{
							case "MR":
								Upload.MachineRom(parts[2], Int32.Parse(parts[3]));
								break;
							case "MD":
								throw new NotImplementedException("Coming soon.");
							case "SR":
								throw new NotImplementedException("Coming soon.");
							case "SD":
								throw new NotImplementedException("Coming soon.");

							default:
								throw new ApplicationException("Upload Unknown type not (MR, MD, SR, SD).");
						}
						return null;

					case ".aodel":
						parts = args.Arguments(3);
						if (parts.Length != 3)
							throw new ApplicationException($"Usage: {parts[0]} <archive.org item name> <filename>");

						Upload.DeleteFile(parts[1], parts[2]);
						return null;

					case ".bt":
						BitTorrent.Initialize();
						return null;

					case ".btx":
						BitTorrent.Remove();
						Tools.ConsoleHeading(1, "To use with archive.org enter the command '.creds' if you have not already entered your credentials");
						return null;

					case ".btr":
						BitTorrent.Restart();
						return null;

					case ".bts":
						BitTorrent.Stop();
						return null;

					case ".btrestart":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <hash>");
						BitTorrent.StopTorrent(parts[1]);
						BitTorrent.StartTorrent(parts[1]);
						return null;

					case ".btstop":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <hash>");
						BitTorrent.StopTorrent(parts[1]);
						return null;

					case ".btstart":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <hash>");
						BitTorrent.StartTorrent(parts[1]);
						return null;

					case ".btreload":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <hash>");
						BitTorrent.StopTorrent(parts[1]);
						BitTorrent.ReloadTorrent(parts[1]);
						BitTorrent.StartTorrent(parts[1]);
						return null;

					case ".creds":
						File.Delete(ArchiveOrgAuth.CacheFilename);
						Globals.AuthCookie = ArchiveOrgAuth.GetCookie();
						return null;

					case ".test":
						parts = args.Arguments(3);
						if (parts.Length != 3)
							throw new ApplicationException($"Usage: {parts[0]} <profile> <count>");
						Test.Run(parts[1], Int32.Parse(parts[2]));
						return null;

					case ".fetch":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <type>");

						switch (parts[1].ToUpper())
						{
							case "MR":
								Import.FetchMachineRom();
								break;
							case "MD":
								Import.FetchMachineDisk();
								break;
							case "SR":
								Import.FetchSoftwareRom();
								break;
							case "SD":
								Import.FetchSoftwareDisk();
								break;

							case "*":
								Import.FetchMachineRom();
								Import.FetchMachineDisk();
								Import.FetchSoftwareRom();
								Import.FetchSoftwareDisk();
								break;

							default:
								throw new ApplicationException("Upload Unknown type not (MR, MD, SR, SD, *).");

						}
						return null;

					case ".software":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <software list name>");
						Import.PlaceSoftwareList(parts[1], true);
						return null;

					case ".softname":
					case ".softnamed":
						parts = args.Arguments(3);
						if (parts.Length != 3)
							throw new ApplicationException($"Usage: {parts[0]} <software list name> <target directory>");
						Export.SoftwareListNamedExport(parts[1], parts[2], parts[0].EndsWith("d"));
						return null;

					case ".style":
						Globals.WebServer.SaveStyle();
						return null;

					case ".accdb":
					case ".accdbxml":
						throw new PlatformNotSupportedException(
							"The MS Access export is not available on Linux: it needs the Microsoft ACE/Jet provider and COM. " +
							"Use the SQLite databases in the MAME version directory.");

					case ".learn":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <directory>");
						CoreFbNeo.HashLookupLearn(parts[1]);
						return null;

					case ".learnsave":
						parts = args.Arguments(1);
						if (parts.Length != 1)
							throw new ApplicationException($"Usage: {parts[0]}");
						CoreFbNeo.HashLookupSave();
						return null;

					case ".learnload":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <filename>");
						CoreFbNeo.HashLookupLoad(parts[1]);
						return null;

					case ".core":
						parts = args.Arguments(3);
						if (parts.Length != 2 && parts.Length != 3)
							throw new ApplicationException($"Usage: {parts[0]} <core name> [version]");

						Cores.EnableCore(parts[1], parts.Length == 3 ? parts[2] : null);
						return null;

					case ".7z2zip":
						parts = args.Arguments(2);
						if (parts.Length != 2)
							throw new ApplicationException($"Usage: {parts[0]} <directory>");
						Tools.UtilConvert7Zip2Zip(parts[1], parts[1]);
						return null;

					case ".":
					default:
						parts = args.Arguments(2);
						if (parts.Length > 1)
							arguments = parts[1];

						string version = args.First == "." ? Globals.Core.Version : args.First.Substring(1);

						MameInstall launchInstall = MameProvider.Get();

						if (version != launchInstall.Version)
							throw new ApplicationException(
								$"MAME {version} is not the installed version ({launchInstall.VersionRaw}, {launchInstall.Detail}). " +
								"Install that version or point --mame-path at it.");

						Globals.PhoneHome.Ready();
						Mame.RunMame(launchInstall, Globals.Core.Directory, CommandLine.Split(arguments));
						return null;
				}
			}

			string core = Globals.Core.Name;
			arguments = null;

			parts = args.Arguments(3, true);

			if (parts[parts.Length - 1][0] == '-')
			{
				arguments = parts[parts.Length - 1];
				parts = parts.Take(parts.Length - 1).ToArray();
			}

			int index = parts[0].LastIndexOf("@");
			if (index != -1)
			{
				core = parts[0].Substring(index + 1);
				parts[0] = parts[0].Substring(0, index);
			}

			if (parts.Length != 1 && parts.Length != 2)
				throw new ApplicationException($"Bad line parts count: {parts.Length}");

			machine = parts[0];
			software = parts.Length > 1 ? parts[1] : "";

			machine = machine.ToLower();
			software = software.ToLower();

			string softwareList = null;
			if (software != null)
			{
				index = software.IndexOf("@");
				if (index != -1)
				{
					softwareList = software.Substring(index + 1);
					software = software.Substring(0, index);
				}
			}

			Cores.EnableCore(core, null);

			switch (Globals.Core.Name)
			{
				case "mame":
				case "hbmame":
					if (Globals.Settings.Options["SnapHome"] == "Yes")
						Snap.CollectSnaps(machine);

					Place.PlaceAssets(Globals.Core, machine, software);

					if (softwareList != null)
						software = Globals.Core.GetRequiredMedia(machine, softwareList, software);

					if (Globals.Settings.Options["Cheats"] == "Yes")
						arguments += " -cheat";

					if (Globals.Settings.Options["SnapNative"] == "Yes")
						arguments += " -snapview native";

					if (Globals.Settings.Options["Window"] == "Yes")
						arguments += " -window";

					Globals.PhoneHome.Ready();

					MameInstall runInstall = Globals.Core.Name == "mame"
						? MameProvider.Get()
						: Mame.FromExecutable(Path.Combine(Globals.Core.Directory, $"{Globals.Core.Name}.exe"));

					List<string> mameArguments = new List<string> { machine };

					if (string.IsNullOrWhiteSpace(software) == false)
						mameArguments.Add(software);

					if (string.IsNullOrWhiteSpace(arguments) == false)
						CommandLine.Append(mameArguments, arguments);

					if (Globals.Headless == true)
					{
						mameArguments.Add("-video");
						mameArguments.Add("none");
						mameArguments.Add("-sound");
						mameArguments.Add("none");
					}

					Mame.RunMame(runInstall, Globals.Core.Directory, mameArguments);
					break;

				case "fbneo":
					throw new PlatformNotSupportedException(
						"FBNeo is not available in the Linux port: it needs the Windows fbneo64.exe build.");

				case "pinball-visual":
					throw new PlatformNotSupportedException(
						"Visual Pinball support is not available in the Linux port: it needs VPinballX64/VPinMAME Windows binaries.");

				default:
					throw new ApplicationException("Bad core");
			}

			return machine;
		}

		public void ListSavedState()
		{
			Tools.ConsoleHeading(2, "Saved Games");

			DataTable table = Mame.ListSavedState(Globals.Core);

			StringBuilder line = new StringBuilder();

			foreach (DataColumn column in table.Columns)
			{
				if (line.Length > 0)
					line.Append("\t");
				line.Append(column.ColumnName);
			}
			Console.WriteLine(line.ToString());

			foreach (DataRow row in table.Rows)
			{
				line.Length = 0;

				foreach (DataColumn column in table.Columns)
				{
					if (line.Length > 0)
						line.Append("\t");
					line.Append(row[column.ColumnName]);
				}
				Console.WriteLine(line.ToString());
			}
			Console.WriteLine();
		}

	}
}
