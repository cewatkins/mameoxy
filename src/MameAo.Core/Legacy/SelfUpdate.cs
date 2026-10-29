using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spludlow.MameAO
{
	/// <summary>
	/// Self update.
	///
	/// Windows keeps the original behaviour: extract the zip and let a child
	/// process replace the exe after this one is killed.
	///
	/// Linux uses the release tarball for the current architecture, replaces the
	/// executable with an atomic rename and then re-executes itself in place with
	/// execv(2). Re-exec keeps the PID, so a systemd user unit does not have to
	/// restart and no second process ever races for the data directory.
	/// </summary>
	public class SelfUpdate
	{
		public static void Update(int startingPid)
		{
			if (OperatingSystem.IsWindows() == true)
			{
				UpdateWindows(startingPid);
				return;
			}

			UpdateLinux();
		}

		//
		// Linux
		//

		private static void UpdateLinux()
		{
			if (Globals.GitHubRepos.TryGetValue("mame-ao", out GitHubRepo repo) == false)
				throw new ApplicationException(
					"Self-update is not configured for this build. " +
					"Set MAME_AO_UPDATE_REPO=owner/repo to a repository that publishes mame-ao-linux-<arch>.tar.gz releases, " +
					"or update with your package manager.");

			if (string.IsNullOrEmpty(repo.tag_name) == true)
				throw new ApplicationException(
					"Cannot check for a new version: GitHub metadata is unavailable (offline or rate limited). " +
					(repo.LoadError ?? ""));

			if (MameAOProcessor.NormaliseVersion(repo.tag_name) == Globals.AssemblyVersion)
			{
				Console.WriteLine($"mame-ao is already up to date ({Globals.AssemblyVersion}).");
				return;
			}

			string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
			string suffix = $"-linux-{architecture}.tar.gz";

			KeyValuePair<string, string> asset = repo.Assets
				.FirstOrDefault(pair => pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

			if (string.IsNullOrEmpty(asset.Key) == true)
				throw new ApplicationException(
					$"Release {repo.tag_name} has no Linux {architecture} asset (expected *{suffix}). " +
					$"Assets: {string.Join(", ", repo.Assets.Keys)}");

			string executable = Environment.ProcessPath;

			if (string.IsNullOrEmpty(executable) == true || File.Exists(executable) == false)
				throw new ApplicationException(
					"Cannot update: the running executable's path is unknown. " +
					"Update with your package manager or unpack the release tarball yourself.");

			executable = ResolveSymlinks(executable);

			if (Path.GetFileNameWithoutExtension(executable) != "mame-ao")
				throw new ApplicationException(
					$"Refusing to overwrite '{executable}': that is not the mame-ao executable. " +
					"Run the released binary, or update with your package manager.");

			string updateDirectory = Path.Combine(Globals.TempDirectory, "UPDATE", repo.tag_name);

			if (Directory.Exists(updateDirectory) == true)
				Directory.Delete(updateDirectory, true);

			Directory.CreateDirectory(updateDirectory);

			string archiveFilename = Path.Combine(Globals.TempDirectory, Path.GetFileName(asset.Key));

			Console.WriteLine($"Updating mame-ao {Globals.AssemblyVersion} => {repo.tag_name} ({architecture})");
			Console.WriteLine(asset.Value);

			Tools.Download(asset.Value, archiveFilename);

			Console.Write($"Extracting {archiveFilename} ...");
			ExtractTarGz(archiveFilename, updateDirectory);
			Console.WriteLine("...done");

			string newExecutable = FindExecutable(updateDirectory);

			if (newExecutable == null)
				throw new ApplicationException($"The update archive did not contain a mame-ao executable. Looked in {updateDirectory}.");

			string staged = executable + ".new";

			File.Copy(newExecutable, staged, true);

			if (OperatingSystem.IsWindows() == false)
				File.SetUnixFileMode(staged,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
					UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
					UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

			try
			{
				// Same directory, so this is a rename, not a copy: the old binary is
				// replaced atomically.
				File.Move(staged, executable, true);
			}
			catch (Exception e)
			{
				File.Delete(staged);
				throw new ApplicationException(
					$"Could not replace '{executable}': {e.Message}. " +
					$"If mame-ao came from a package manager or /usr/local, update it that way instead.", e);
			}

			CopySideFiles(updateDirectory, Path.GetDirectoryName(executable));

			File.Delete(archiveFilename);

			Console.WriteLine($"Updated {executable}. Re-executing...");

			ReExec(executable);
		}

		/// <summary>
		/// The UI is a separate file next to the executable, so it has to be
		/// updated as well; leaving the old one would mean a new binary serving
		/// an old UI.
		/// </summary>
		private static void CopySideFiles(string updateDirectory, string targetDirectory)
		{
			if (string.IsNullOrWhiteSpace(targetDirectory) == true)
				return;

			foreach (string name in new string[] { "UI.html", "LINUX.md", "mame-ao.service" })
			{
				string source = null;

				foreach (string candidate in Directory.GetFiles(updateDirectory, name, SearchOption.AllDirectories))
				{
					source = candidate;
					break;
				}

				if (source == null)
					continue;

				try
				{
					File.Copy(source, Path.Combine(targetDirectory, name), true);
				}
				catch (Exception e)
				{
					Console.WriteLine($"Could not update '{name}': {e.Message}");
				}
			}
		}

		private static string ResolveSymlinks(string filename)
		{
			string current = Path.GetFullPath(filename);

			for (int guard = 0; guard < 16; ++guard)
			{
				FileInfo info = new FileInfo(current);
				FileSystemInfo target = info.ResolveLinkTarget(true);

				if (target == null)
					break;

				current = target.FullName;
			}

			return current;
		}

		private static void ExtractTarGz(string archiveFilename, string targetDirectory)
		{
			using (FileStream file = File.OpenRead(archiveFilename))
			using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
			{
				TarFile.ExtractToDirectory(gzip, targetDirectory, overwriteFiles: true);
			}
		}

		/// <summary>
		/// Releases are packed with the binary at the archive root, but accept one
		/// directory level too so a differently packed archive still updates.
		/// </summary>
		private static string FindExecutable(string directory)
		{
			string direct = Path.Combine(directory, "mame-ao");

			if (File.Exists(direct) == true)
				return direct;

			foreach (string candidate in Directory.GetFiles(directory, "mame-ao", SearchOption.AllDirectories))
				return candidate;

			return null;
		}

		[DllImport("libc", SetLastError = true)]
		private static extern int execv(string path, IntPtr[] argv);

		/// <summary>
		/// Replace this process image with the new binary. If execv fails for any
		/// reason, start a child with the same arguments and exit, which still ends
		/// up running the new version.
		/// </summary>
		private static void ReExec(string executable)
		{
			string[] arguments = Environment.GetCommandLineArgs();

			IntPtr[] argv = new IntPtr[arguments.Length + 1];

			try
			{
				for (int index = 0; index < arguments.Length; ++index)
					argv[index] = Marshal.StringToHGlobalAnsi(arguments[index]);

				argv[arguments.Length] = IntPtr.Zero;

				execv(executable, argv);
			}
			catch (Exception)
			{
				// Fall through to the spawn path.
			}
			finally
			{
				foreach (IntPtr pointer in argv)
				{
					if (pointer != IntPtr.Zero)
						Marshal.FreeHGlobal(pointer);
				}
			}

			ProcessStartInfo startInfo = new ProcessStartInfo(executable)
			{
				UseShellExecute = false,
				WorkingDirectory = Directory.GetCurrentDirectory(),
			};

			for (int index = 1; index < arguments.Length; ++index)
				startInfo.ArgumentList.Add(arguments[index]);

			Process.Start(startInfo);

			Environment.Exit(0);
		}

		//
		// Windows (unchanged upstream behaviour)
		//

		private static void UpdateWindows(int startingPid)
		{
			string updateDirectory = Path.Combine(Globals.TempDirectory, "UPDATE");

			if (startingPid <= 0)
			{
				if (Globals.GitHubRepos.TryGetValue("mame-ao", out GitHubRepo repo) == false)
					throw new ApplicationException(
						"Self-update is not configured for this build. Set MAME_AO_UPDATE_REPO=owner/repo.");

				if (repo.tag_name == Globals.AssemblyVersion)
				{
					Console.WriteLine($"MAME-AO is already up to date '{Globals.AssemblyVersion}'.");
					if (startingPid == 0)
						return;
				}

				Console.WriteLine($"Updating MAME-AO '{Globals.AssemblyVersion}' => '{repo.tag_name}'...");

				string archiveUrl = repo.Assets[repo.Assets.First().Key];
				string archiveFilename = Path.Combine(Globals.RootDirectory, $"mame-ao-{repo.tag_name}.zip");

				Tools.Download(archiveUrl, archiveFilename);

				if (Directory.Exists(updateDirectory) == true)
				{
					try
					{
						Directory.Delete(updateDirectory, true);
					}
					catch (UnauthorizedAccessException e)
					{
						throw new ApplicationException("Looks like an update is currently running, please kill all mame-ao processes and try again, " + e.Message, e);
					}
				}

				ZipFile.ExtractToDirectory(archiveFilename, updateDirectory);

				int pid = Process.GetCurrentProcess().Id;

				ProcessStartInfo startInfo = new ProcessStartInfo(Path.Combine(updateDirectory, "mame-ao.exe"))
				{
					WorkingDirectory = Globals.RootDirectory,
					UseShellExecute = true,
				};
				startInfo.ArgumentList.Add($"update={pid}");
				startInfo.ArgumentList.Add($"directory={Globals.RootDirectory}");

				Console.Write("Starting update process...");
				using (Process process = new Process())
				{
					process.StartInfo = startInfo;
					process.Start();

					if (process.HasExited == true)
						throw new ApplicationException($"Update process exited imediatly after starting {process.ExitCode}.");
				}
				Console.WriteLine("...done");

				Console.WriteLine("Waiting to be killed...");
				Thread.Sleep(Timeout.Infinite);
			}
			else
			{
				try
				{
					UpdateChild(updateDirectory, startingPid);
				}
				catch (Exception e)
				{
					Tools.ReportError(e, "UPDATE FATAL ERROR", true);
				}
			}
		}

		public static void UpdateChild(string updateDirectory, int startingPid)
		{
			Console.WriteLine($"MAME-AO UPDATER {Globals.AssemblyVersion}");
			Console.WriteLine($"Target Directory: {Globals.RootDirectory}, Update From Directory {updateDirectory}.");

			Console.WriteLine("Killing starting process...");
			using (Process startingProcess = Process.GetProcessById(startingPid))
			{
				startingProcess.Kill();
				startingProcess.WaitForExit();
			}
			Console.WriteLine("...done");

			try
			{
				File.Delete(Path.Combine(Globals.RootDirectory, "mame-ao.exe"));
			}
			catch (UnauthorizedAccessException e)
			{
				throw new ApplicationException("Looks like the starting process is currently running, please kill all mame-ao processes and try again, " + e.Message, e);
			}

			foreach (string sourceFilename in Directory.GetFiles(updateDirectory))
			{
				string targetFilename = Path.Combine(Globals.RootDirectory, Path.GetFileName(sourceFilename));

				File.Copy(sourceFilename, targetFilename, true);

				Console.WriteLine(targetFilename);
			}

			ProcessStartInfo startInfo = new ProcessStartInfo(Path.Combine(Globals.RootDirectory, "mame-ao.exe"))
			{
				WorkingDirectory = Globals.RootDirectory,
				UseShellExecute = true,
			};

			Console.Write("Starting updated mame-ao process...");
			using (Process process = new Process())
			{
				process.StartInfo = startInfo;
				process.Start();

				if (process.HasExited == true)
					throw new ApplicationException($"New mame-ao process exited imediatly after starting {process.ExitCode}.");
			}
			Console.WriteLine("...done");

			Console.WriteLine("Exiting this process...");
			Environment.Exit(0);
		}
	}
}
