using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

using MonoTorrent;
using MonoTorrent.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dome_bt
{
	public class BitTorrent
	{
		private const string DefaultTorrentFeed = "https://data.spludlow.co.uk/api/torrents";

		private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
		private static readonly TimeSpan StatusDelay = TimeSpan.FromMinutes(1);

		public ClientEngine Engine;

		public bool AskStop = false;

		public bool FatalError = false;

		public int PortNumber = 0;

		private int MaximumConnectionsPerTorrent = 80;

		private readonly double MegaBitsToBytes = 125000.0;
		private double MaximumDownloadRate = 0;
		private double MaximumUploadRate = 0;

		public TorrentSettings TorrentSettings;

		//
		// Completed by AskStopNow() so any pending delay returns immediately when
		// /api/stop or SIGTERM/SIGINT arrives.
		//
		private readonly TaskCompletionSource<bool> StopSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		private static PropertyInfo FilePriorityProperty = null;

		public BitTorrent()
		{
		}

		public void AskStopNow()
		{
			AskStop = true;
			StopSignal.TrySetResult(true);
		}

		public void Setup(int count)
		{
			string config;

			config = Globals.Config.Get("maximum-connections-per-torrent");
			if (config != null)
				MaximumConnectionsPerTorrent = Int32.Parse(config);

			config = Globals.Config.Get("maximum-download-rate-mbps");
			if (config != null)
				MaximumDownloadRate = Double.Parse(config);

			config = Globals.Config.Get("maximum-upload-rate-mbps");
			if (config != null)
				MaximumUploadRate = Double.Parse(config);

			if (Globals.Config.ContainsKey("port-number") == false)
			{
				PortNumber = new Random().Next(49152, 65536);
				Globals.Config.Set("port-number", PortNumber.ToString());
			}
			else
			{
				PortNumber = Int32.Parse(Globals.Config.Get("port-number"));
			}

			var torrentSettings = new TorrentSettingsBuilder
			{
				MaximumConnections = MaximumConnectionsPerTorrent,
				AllowPeerExchange = true,
				AllowDht = true,
			};
			TorrentSettings = torrentSettings.ToSettings();

			Dictionary<string, IPEndPoint> listenEndPoints = new Dictionary<string, IPEndPoint>()
			{
				{ "ipv4", new IPEndPoint(IPAddress.Any, PortNumber) },
			};

			if (Socket.OSSupportsIPv6 == true)
				listenEndPoints.Add("ipv6", new IPEndPoint(IPAddress.IPv6Any, PortNumber));

			//
			// Port forwarding uses Mono.Nat which on Linux shells out to iptables and
			// normally has no privileges under systemd. Off unless explicitly asked for.
			//
			bool allowPortForwarding = Environment.GetEnvironmentVariable("MAMEAO_BT_PORT_FORWARDING") == "1";

			var engineSettings = new EngineSettingsBuilder
			{
				AllowPortForwarding = allowPortForwarding,
				AllowLocalPeerDiscovery = false,
				AutoSaveLoadDhtCache = true,
				AutoSaveLoadFastResume = true,
				AutoSaveLoadMagnetLinkMetadata = true,
				FastResumeMode = FastResumeMode.BestEffort,

				CacheDirectory = Globals.DirectoryCache,

				ListenEndPoints = listenEndPoints,

				DhtEndPoint = new IPEndPoint(IPAddress.Any, PortNumber),

				MaximumConnections = Math.Max(count, 1) * MaximumConnectionsPerTorrent * 2,
				MaximumDownloadRate = (int)(MaximumDownloadRate * MegaBitsToBytes),
				MaximumUploadRate = (int)(MaximumUploadRate * MegaBitsToBytes),

				MaximumHalfOpenConnections = 16,

				// The private dome-bt fork had no partial file support, keep writing
				// straight to the final path so /api/file.filename is stable.
				UsePartialFiles = false,
			};

			Tools.Heading("Engine Settings");
			Tools.Log("0 = No limit");

			Tools.Log($"Max Connections per Torrent : {MaximumConnectionsPerTorrent}");
			Tools.Log($"Max Download Rate           : {engineSettings.MaximumDownloadRate} B/s ({MaximumDownloadRate} Mbit/s)");
			Tools.Log($"Max Upload Rate             : {engineSettings.MaximumUploadRate} B/s ({MaximumUploadRate} Mbit/s)");
			Tools.Log($"Torrent port                : {PortNumber}");
			Tools.Log($"Port forwarding             : {allowPortForwarding}");

			Engine = new ClientEngine(engineSettings.ToSettings());
		}

		public async Task RunAsync()
		{
			Exception error = null;

			using (PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal))
			using (PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal))
			{
				try
				{
					await WorkerAsync();
				}
				catch (Exception e)
				{
					Tools.ReportError(e, "Fatal Error");
					error = e;
				}
				finally
				{
					await ShutdownAsync();
				}
			}

			if (error != null)
				FatalError = true;
		}

		private void OnPosixSignal(PosixSignalContext context)
		{
			context.Cancel = true;      // shut down cleanly instead of the default terminate
			Tools.Log($"Received {context.Signal}, stopping");
			AskStopNow();
		}

		public async Task WorkerAsync()
		{
			//
			// Setup can fail because data.spludlow.co.uk is unreachable (e.g. at boot
			// before the network is up). Keep the HTTP API answering and try again.
			//
			while (AskStop == false && Globals.ReadyTime == Globals.StartTime)
			{
				try
				{
					await SetupTorrentsAsync();
				}
				catch (Exception e)
				{
					Tools.ReportError(e, "Torrent Setup Failed");
					Tools.Log($"Will retry in {RetryDelay.TotalMinutes} minutes. The HTTP API remains available.");

					await DisposeEngineAsync();
					await WaitOrStopAsync(RetryDelay);
				}
			}

			if (AskStop == true)
				return;

			Tools.Heading("All Torrents Ready");
			await MonitorAsync();
		}

		private async Task MonitorAsync()
		{
			while (AskStop == false)
			{
				await WaitOrStopAsync(StatusDelay);

				if (AskStop == true)
					break;

				LogStatus();
			}
		}

		private void LogStatus()
		{
			try
			{
				ClientEngine engine = Engine;

				if (engine == null)
				{
					Tools.Log("engine: not running");
					return;
				}

				long dataBytesReceived = 0;
				long dataBytesSent = 0;

				foreach (TorrentManager manager in engine.Torrents)
				{
					dataBytesReceived += manager.Monitor.DataBytesReceived;
					dataBytesSent += manager.Monitor.DataBytesSent;
				}

				Tools.Log($"DOME-BT {Globals.AssemblyVersion}\tstart:{Globals.StartTime}\trun:{Tools.TimeTookText(DateTime.Now - Globals.StartTime)}\tport:{PortNumber}");
				Tools.Log($"connections:{engine.ConnectionManager.OpenConnections}\tdownload:{Tools.DataSizeText(engine.TotalDownloadRate)}/s\tupload:{Tools.DataSizeText(engine.TotalUploadRate)}/s\treceived:{Tools.DataSizeText(dataBytesReceived)}\tsent:{Tools.DataSizeText(dataBytesSent)}");

				foreach (TorrentManager manager in engine.Torrents)
				{
					Tools.Log($"{manager.Name}\t{manager.State}\t{manager.OpenConnections}\t" +
						$"{Tools.DataSizeText(manager.Monitor.DataBytesReceived)}\t{Tools.DataSizeText(manager.Monitor.DataBytesSent)}");
				}
			}
			catch (Exception e)
			{
				Tools.ReportError(e, "Status");
			}
		}

		private async Task WaitOrStopAsync(TimeSpan span)
		{
			Task delay = Task.Delay(span);
			await Task.WhenAny(delay, StopSignal.Task);
		}

		public static string TorrentFeedUrl()
		{
			string url = Environment.GetEnvironmentVariable("MAMEAO_TORRENT_FEED");

			if (string.IsNullOrWhiteSpace(url) == true)
				url = DefaultTorrentFeed;

			return url.TrimEnd('/');
		}

		private static readonly string PayloadKey = "RRt08v+YWc2+910RGOhZO7DrNVnHKae8MDJyJNOd950=";

		private static string Decrypt(string input, string iv)
		{
			using (var aes = Aes.Create())
			{
				aes.Key = System.Convert.FromBase64String(PayloadKey);
				aes.IV = System.Convert.FromBase64String(iv);
				aes.Mode = CipherMode.CBC;
				aes.Padding = PaddingMode.PKCS7;

				using (var decryptor = aes.CreateDecryptor())
				using (var stream = new MemoryStream(System.Convert.FromBase64String(input)))
				using (var cryStream = new CryptoStream(stream, decryptor, CryptoStreamMode.Read))
				using (var reader = new StreamReader(cryStream))
					return reader.ReadToEnd();
			}
		}

		private async Task SetupTorrentsAsync()
		{
			string metadataDirectory = Path.Combine(Globals.DirectoryCache, "metadata");

			//
			// Existing Torrents
			//
			HashSet<string> existingHashes = new HashSet<string>();
			if (Directory.Exists(metadataDirectory) == true)
				existingHashes = Directory.GetFiles(metadataDirectory, "*.torrent").Select(filename => Path.GetFileNameWithoutExtension(filename)).ToHashSet();

			//
			// Download torrents
			//
			Tools.Heading("Obtain Torrents");

			string feedUrl = TorrentFeedUrl();
			Tools.Log($"Feed: {feedUrl}");

			List<TorrentInfo> torrentInfos = new List<TorrentInfo>();

			foreach (string core in Globals.Cores)
			{
				for (int pass = 0; pass < 2; ++pass)
				{
					string url = $"{feedUrl}/{core}";
					if (pass == 0)
						url += ".peek";

					dynamic json = JsonConvert.DeserializeObject<dynamic>(Tools.FetchCached(url) ?? throw new ApplicationException($"Can't fetch Torrents: {url}"));

					string body = Decrypt((string)json.body, (string)json.iv);

					JArray items = JArray.Parse(body);

					if (pass == 0)
					{
						HashSet<string> currentHashes = new HashSet<string>(items.Select(item => (string)item["hash"]));

						if (existingHashes.IsSupersetOf(currentHashes) == true)
						{
							foreach (dynamic item in items)
							{
								Tools.Log($"HAVE\t{core}\t{item.type}\t{item.name}");

								Torrent torrent = await Torrent.LoadAsync(Path.Combine(metadataDirectory, $"{item.hash}.torrent"));
								torrentInfos.Add(MakeTorrentInfo(core, item, torrent));
							}
							break;
						}
					}
					else
					{
						foreach (dynamic item in items)
						{
							Tools.Log($"NEW\t{core}\t{item.type}\t{item.name}");

							Torrent torrent;
							using (var targetStream = new MemoryStream())
							{
								using (var sourceStream = new MemoryStream(System.Convert.FromBase64String((string)item.torrent)))
									using (var zipArchive = new ZipArchive(sourceStream))
										using (var zipStream = zipArchive.Entries[0].Open())
											zipStream.CopyTo(targetStream);

								targetStream.Position = 0;
								torrent = Torrent.Load(targetStream);
							}
							torrentInfos.Add(MakeTorrentInfo(core, item, torrent));
						}
					}
				}
			}

			//
			// Setup Engine
			//
			Setup(torrentInfos.Count);

			//
			// Setup Torrents
			//
			Tools.Heading("Setup Torrents");

			foreach (TorrentInfo torrentInfo in torrentInfos)
			{
				Tools.Log($"{torrentInfo.Name} ...");
				var torrentManager = await Engine.AddAsync(torrentInfo.Torrent, Globals.DirectoryDownloads, TorrentSettings);
				torrentInfo.TorrentManager = torrentManager;

				//
				// The private MonoTorrent fork used by dome-bt set every file to
				// Priority.DoNotDownload by default. Public MonoTorrent defaults to
				// Normal (i.e. download the whole torrent), so do it explicitly here,
				// before the torrent is started.
				//
				await SetAllFilesDoNotDownloadAsync(torrentManager);

				Tools.Log($"...done {torrentManager.Name}");
			}

			Tools.Heading("Starting Torrents");

			//
			// Clear old downloads
			//
			if (Directory.Exists(Globals.DirectoryDownloads) == true)
			{
				List<string> currentNames = new List<string>(torrentInfos.Select(x => x.Name));

				foreach (string directory in Directory.GetDirectories(Globals.DirectoryDownloads))
				{
					if (currentNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase) == false)
					{
						Tools.Log($"Remove old directory {directory} ...");
						Directory.Delete(directory, true);
						Tools.Log("...done");
					}
				}
			}

			//
			// Start Torrents
			//
			HashSet<string> hashes = new HashSet<string>();

			foreach (TorrentInfo torrentInfo in torrentInfos)
			{
				var manager = torrentInfo.TorrentManager;

				Tools.Log($"{manager.Name}\tStarting\t{manager.Files.Count}");

				await manager.StartAsync();

				if (manager.HasMetadata == false)
				{
					Tools.Log($"{manager.Name}\tWaiting for Metadata");
					await manager.WaitForMetadataAsync();
					Tools.Log($"{manager.Name}\tMetadata\t{manager.Files.Count}");

					// Files only exist now, apply the DoNotDownload default.
					await SetAllFilesDoNotDownloadAsync(manager);
				}

				if (manager.InfoHashes.V1OrV2.ToHex() != torrentInfo.Hash)
					throw new ApplicationException("hash mismatch");

				hashes.Add(torrentInfo.Hash);

				lock (Globals.TorrentInfos)
					Globals.TorrentInfos.Add(torrentInfo);

				Tools.Log($"{manager.Name}\tReady\t{torrentInfo.Hash}");
			}

			//
			// Clear old cache
			//
			foreach (string directory in new string[] { Path.Combine(Globals.DirectoryCache, "fastresume"), Path.Combine(Globals.DirectoryCache, "metadata") })
			{
				if (Directory.Exists(directory) == true)
				{
					foreach (string filename in Directory.GetFiles(directory))
					{
						if (hashes.Contains(Path.GetFileNameWithoutExtension(filename)) == false)
						{
							Tools.Log($"Remove old cache file {filename} ...");
							File.Delete(filename);
							Tools.Log("...done");
						}
					}
				}
			}

			Globals.ReadyTime = DateTime.Now;

			Tools.Log($"Ready in {Tools.TimeTookText(Globals.ReadyTime - Globals.StartTime)}");
		}

		/// <summary>
		/// Set every file in the torrent to DoNotDownload. This reproduces the only
		/// behavioural change in dome-bt's private MonoTorrent fork: huge torrents
		/// are advertised, but only the files requested through /api/file transfer.
		/// </summary>
		public async Task SetAllFilesDoNotDownloadAsync(TorrentManager manager)
		{
			if (manager == null || manager.HasMetadata == false)
				return;

			int changed = 0;

			PropertyInfo priorityProperty = GetFilePriorityProperty(manager);

			if (priorityProperty != null)
			{
				//
				// Public SetFilePriorityAsync is O(files) per call (Files.Contains plus a
				// selector rebuild), so setting every file on the 90,000 file software
				// list torrents takes about 12 minutes. The concrete file objects expose
				// a public Priority setter, so the bulk change is done directly; the
				// piece picker re-reads ITorrentManagerFile.Priority when it next picks
				// a piece, and /api/file still goes through SetFilePriorityAsync.
				//
				foreach (ITorrentManagerFile file in manager.Files)
				{
					if (file.Priority == Priority.DoNotDownload)
						continue;

					priorityProperty.SetValue(file, Priority.DoNotDownload);
					++changed;
				}
			}
			else
			{
				foreach (ITorrentManagerFile file in manager.Files)
				{
					if (file.Priority == Priority.DoNotDownload)
						continue;

					await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
					++changed;
				}
			}

			Tools.Log($"{manager.Name}\tfiles:{manager.Files.Count}\tDoNotDownload:{changed} changed");
		}

		private static PropertyInfo GetFilePriorityProperty(TorrentManager manager)
		{
			if (FilePriorityProperty != null)
				return FilePriorityProperty;

			if (manager.Files.Count == 0)
				return null;

			PropertyInfo property = manager.Files[0].GetType().GetProperty("Priority", BindingFlags.Public | BindingFlags.Instance);

			if (property == null || property.CanWrite == false || property.PropertyType != typeof(Priority))
				return null;

			FilePriorityProperty = property;

			return FilePriorityProperty;
		}

		private async Task ShutdownAsync()
		{
			Tools.Heading("Shutdown");

			ClientEngine engine = Engine;

			if (engine == null)
			{
				Tools.Log("...done (engine was not running)");
				return;
			}

			foreach (TorrentManager torrentManager in engine.Torrents)
			{
				Tools.Log($"stop torrent {torrentManager.Name} ...");

				try
				{
					// Make sure any buffered data is on disk before the torrent stops.
					await engine.DiskManager.FlushAsync(torrentManager);
					await torrentManager.StopAsync();
				}
				catch (Exception e)
				{
					Tools.ReportError(e, $"Stop Torrent {torrentManager.Name}");
				}

				Tools.Log("...done");
			}

			Tools.Log("stop engine ...");

			try
			{
				await engine.StopAllAsync();
			}
			catch (Exception e)
			{
				Tools.ReportError(e, "Stop Engine");
			}

			engine.Dispose();
			Engine = null;

			Tools.Log("...done");
		}

		private async Task DisposeEngineAsync()
		{
			lock (Globals.TorrentInfos)
				Globals.TorrentInfos.Clear();

			ClientEngine engine = Engine;
			if (engine == null)
				return;

			try
			{
				await engine.StopAllAsync();
			}
			catch (Exception e)
			{
				Tools.ReportError(e, "Dispose Engine");
			}

			engine.Dispose();
			Engine = null;
		}

		private TorrentInfo MakeTorrentInfo(string core, dynamic item, Torrent torrent)
		{
			return new TorrentInfo()
			{
				Core = core,
				Type = (string)item.type,
				Name = (string)item.name,
				Version = (string)item.version,
				Hash = (string)item.hash,
				Magnet = (string)item.magnet,
				DatFile = (string)item.dat,
				Torrent = torrent,
			};
		}
	}
}
