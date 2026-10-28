using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;


namespace Spludlow.MameAO
{
	public class Mame
	{
		/// <summary>
		/// Run MAME with an argument list. No shell, no string concatenation, no
		/// reliance on the working directory to find the binary.
		///
		/// <paramref name="workingDirectory"/> is the per-version core directory:
		/// that is where the generated mame.ini lives and where MAME's relative
		/// defaults would otherwise be resolved, so the caller passes it explicitly
		/// rather than inheriting whatever the parent process had.
		/// </summary>
		public static void RunMame(MameInstall install, string workingDirectory, IReadOnlyList<string> arguments)
		{
			if (install == null)
				throw new ArgumentNullException(nameof(install));

			List<string> argv = new List<string>(install.Prefix);

			foreach (string argument in arguments)
				argv.Add(argument);

			if (Globals.MameArguments.Length > 0)
				CommandLine.Append(argv, Globals.MameArguments);

			RunProcess(argv, workingDirectory);
		}

		/// <summary>
		/// Legacy entry point kept for the Windows-only cores (FBNeo, Visual
		/// Pinball) that are disabled on Linux. Arguments are tokenised here rather
		/// than handed to a shell.
		/// </summary>
		public static void RunMame(string binFilename, string arguments)
		{
			List<string> argv = new List<string> { binFilename };
			CommandLine.Append(argv, arguments);

			if (Globals.MameArguments.Length > 0)
				CommandLine.Append(argv, Globals.MameArguments);

			RunProcess(argv, Path.GetDirectoryName(binFilename));
		}

		private static void RunProcess(IReadOnlyList<string> argv, string workingDirectory)
		{
			Tools.ConsoleHeading(1, new string[] {
				"Starting CORE",
				string.Join(' ', argv),
				workingDirectory ?? "",
			});
			Console.WriteLine();

			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = argv[0],
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			for (int index = 1; index < argv.Count; ++index)
				startInfo.ArgumentList.Add(argv[index]);

			if (string.IsNullOrWhiteSpace(workingDirectory) == false && Directory.Exists(workingDirectory) == true)
				startInfo.WorkingDirectory = workingDirectory;

			using (Process process = new Process())
			{
				process.StartInfo = startInfo;

				process.OutputDataReceived += new DataReceivedEventHandler((sender, e) =>
				{
					if (e.Data == null)
						return;
					Console.WriteLine($"MAME output:{e.Data}");
					Globals.PhoneHome?.MameOutputLine(e.Data);
				});

				process.ErrorDataReceived += new DataReceivedEventHandler((sender, e) =>
				{
					if (e.Data == null)
						return;
					Console.WriteLine($"MAME error:{e.Data}");
					Globals.PhoneHome?.MameErrorLine(e.Data);
				});

				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();

				Globals.PhoneHome?.MameExitCode(process.ExitCode, Globals.Core?.Name, Globals.Core?.Version, string.Join(' ', argv));

				Console.WriteLine();
				if (process.ExitCode == 0)
					Console.WriteLine("MAME Shell Exit OK.");
				else
					Console.WriteLine($"MAME Shell Exit BAD: {process.ExitCode}");
			}

			Console.WriteLine();
		}

		/// <summary>
		/// Run <c>-listxml</c> / <c>-listsoftware</c> and write the result.
		///
		/// The hash path is passed explicitly: on Linux MAME resolves a relative
		/// hashpath against the *process* working directory, not its homepath, so
		/// -listsoftware silently emits an empty document when started from the
		/// wrong place. <paramref name="arguments"/> are real arguments, not a
		/// command line string.
		/// </summary>
		public static void ExtractXML(MameInstall install, string workingDirectory, string outputFilename, params string[] arguments)
		{
			if (install == null)
				throw new ArgumentNullException(nameof(install));

			if (Directory.Exists(workingDirectory) == false)
				throw new ApplicationException($"MAME working directory does not exist: {workingDirectory}");

			string tempFilename = outputFilename + ".tmp";
			File.Delete(tempFilename);

			List<string> argv = new List<string>(install.Prefix);
			argv.AddRange(arguments);

			if (string.IsNullOrWhiteSpace(install.HashPath) == false)
			{
				argv.Add("-hashpath");
				argv.Add(install.HashPath);
			}

			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = argv[0],
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				WorkingDirectory = workingDirectory,
			};

			for (int index = 1; index < argv.Count; ++index)
				startInfo.ArgumentList.Add(argv[index]);

			StringBuilder error = new StringBuilder();

			using (Process process = new Process())
			{
				process.StartInfo = startInfo;

				using (StreamWriter writer = new StreamWriter(tempFilename, false, new UTF8Encoding(false)))
				{
					process.OutputDataReceived += new DataReceivedEventHandler((sender, e) =>
					{
						if (e.Data != null)
							writer.WriteLine(e.Data);
					});

					process.ErrorDataReceived += new DataReceivedEventHandler((sender, e) =>
					{
						if (e.Data != null)
							error.AppendLine(e.Data);
					});

					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();
					process.WaitForExit();

					if (process.ExitCode != 0)
					{
						File.Delete(tempFilename);
						throw new ApplicationException(
							$"'{string.Join(' ', argv)}' failed with exit code {process.ExitCode}: {Trim(error.ToString())}");
					}
				}
			}

			// -listsoftware can legitimately be empty (no hash directory found).
			// HBMAME also writes non-XML here. Write a valid empty document rather
			// than letting the XML reader fail later.
			if (HasSoftwareListsArgument(arguments) == true)
			{
				FileInfo info = new FileInfo(tempFilename);
				if (info.Exists == false || info.Length < 48)
					File.WriteAllText(tempFilename, "<?xml version=\"1.0\"?>\n<softwarelists></softwarelists>" + Environment.NewLine);
			}

			File.Move(tempFilename, outputFilename, true);
		}

		private static bool HasSoftwareListsArgument(string[] arguments)
		{
			if (arguments == null)
				return false;

			foreach (string argument in arguments)
			{
				if (string.Equals(argument, "-listsoftware", StringComparison.OrdinalIgnoreCase) == true)
					return true;
			}

			return false;
		}

		private static string Trim(string text)
		{
			if (string.IsNullOrWhiteSpace(text) == true)
				return "";

			text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
			return text.Length <= 800 ? text : text.Substring(0, 800) + "…";
		}

		/// <summary>
		/// Windows-only helper retained for the cores that are disabled on Linux.
		/// </summary>
		[Obsolete("Use the MameInstall overload.")]
		public static void ExtractXML(string binFilename, string outputFilename, string arguments)
		{
			List<string> argv = new List<string> { binFilename };
			CommandLine.Append(argv, arguments);

			MameInstall install = new MameInstall
			{
				Prefix = new[] { binFilename },
				Kind = MameProviderKind.ExplicitPath,
				Detail = binFilename,
			};

			ExtractXML(install, Path.GetDirectoryName(binFilename), outputFilename, argv.GetRange(1, argv.Count - 1).ToArray());
		}

		public static MameInstall FromExecutable(string filename)
		{
			return new MameInstall
			{
				Kind = MameProviderKind.ExplicitPath,
				Prefix = new[] { filename },
				Detail = filename,
				Version = "unknown",
				VersionRaw = "unknown",
			};
		}

		public static void RunSelfExtract(string filename)
		{
			if (OperatingSystem.IsWindows() == false)
				throw new PlatformNotSupportedException("Self-extracting MAME archives are a Windows-only code path.");

			using (Process process = new Process())
			{
				process.StartInfo = new ProcessStartInfo(filename)
				{
					WorkingDirectory = Path.GetDirectoryName(filename),
					UseShellExecute = false,
				};
				process.StartInfo.ArgumentList.Add("-y");

				process.Start();
				process.WaitForExit();

				if (process.ExitCode != 0)
					throw new ApplicationException($"Self-extract failed with exit code {process.ExitCode}: {filename}");
			}
		}

		public static string LatestLocal()
		{
			List<string> versions = new List<string>();

			foreach (string versionDirectory in Directory.GetDirectories(Globals.RootDirectory))
			{
				if (IsCoreDirectory(versionDirectory) == false)
					continue;

				versions.Add(Path.GetFileName(versionDirectory));
			}

			if (versions.Count == 0)
				return null;

			versions.Sort();

			return versions[versions.Count - 1];
		}

		/// <summary>A version directory is one we have prepared (ini written) or an old Windows one.</summary>
		internal static bool IsCoreDirectory(string directory)
		{
			return File.Exists(Path.Combine(directory, "mame.ini")) == true ||
				File.Exists(Path.Combine(directory, "mame.exe")) == true;
		}

		public static DataTable ListSavedState(ICore core)
		{
			DataTable table = Tools.MakeDataTable(
				"version	binary_time	sta_name	sta_time	sta_machine	sta_description",
				"String		DateTime	String		DateTime	String		String");

			foreach (string mameDirectory in Directory.GetDirectories(Path.GetDirectoryName(core.Directory)))
			{
				string version = Path.GetFileName(mameDirectory);

				if (version.StartsWith("_") == true)
					continue;

				if (IsCoreDirectory(mameDirectory) == false)
					continue;

				DateTime lastWriteTime = Directory.GetLastWriteTime(mameDirectory);

				string staDirectory = Path.Combine(mameDirectory, "sta");

				bool found = false;

				if (Directory.Exists(staDirectory) == true)
				{
					foreach (string staMachineDirectory in Directory.GetDirectories(staDirectory))
					{
						string staMachine = Path.GetFileName(staMachineDirectory);

						foreach (string staFilename in Directory.GetFiles(staMachineDirectory, "*.sta"))
						{
							found = true;

							string staName = Path.GetFileNameWithoutExtension(staFilename).ToUpper();

							DateTime staLastWriteTime = File.GetLastWriteTime(staFilename);

							string description = "";
							DataRow machineRow = core.GetMachine(staMachine);
							if (machineRow != null)
								description = (string)machineRow["description"];

							table.Rows.Add(version, lastWriteTime, staName, staLastWriteTime, staMachine, description);
						}
					}
				}

				if (found == false)
					table.Rows.Add(version, lastWriteTime);
			}

			DataView view = new DataView(table)
			{
				Sort = "version, sta_time"
			};

			DataTable sortTable = table.Clone();
			foreach (DataRowView rowView in view)
				sortTable.ImportRow(rowView.Row);

			return sortTable;
		}

		public static void CollectSnaps(string rootDirectory, string targetDirectory)
		{
			// The original expected the caller to have made this directory; making
			// it here turns "DirectoryNotFoundException" into a working command.
			Directory.CreateDirectory(targetDirectory);

			DataTable table = Tools.MakeDataTable(
				"Version	Machine	LastWriteTime	SourceFilename	TargetFilename",
				"String		String	DateTime		String			String");

			foreach (string versionDirectory in Directory.GetDirectories(rootDirectory))
			{
				string version = Path.GetFileName(versionDirectory);

				if (version.StartsWith("_") == true)
					continue;

				if (IsCoreDirectory(versionDirectory) == false)
					continue;

				string snapDirectory = Path.Combine(versionDirectory, "snap");

				if (Directory.Exists(snapDirectory) == false)
					continue;

				foreach (string machineDirectory in Directory.GetDirectories(snapDirectory))
				{
					string machineName = Path.GetFileName(machineDirectory);

					CollectSnaps(Directory.GetFiles(machineDirectory, "*.png"), targetDirectory, machineName, version, table);
				}
			}

			if (table.Rows.Count > 0)
				Globals.Reports.SaveHtmlReport(table, $"Collect Snaps ({table.Rows.Count})");

			Console.WriteLine($"Collected {table.Rows.Count} Snaps.");
		}

		public static string[] CollectSnaps(string[] sourceFilenames, string targetDirectory, string machineName, string version, DataTable table)
		{
			var targetFilenames = new List<string>();

			foreach (string snapFilename in sourceFilenames)
			{
				DateTime lastWriteTime = File.GetLastWriteTime(snapFilename);

				string stamp = lastWriteTime.ToString("s").Replace(":", "-");

				string name = Path.GetFileNameWithoutExtension(snapFilename);

				string targetFilename = Path.Combine(targetDirectory, $"{machineName}.{version}.{stamp}.{name}.png");

				File.Move(snapFilename, targetFilename, true);
				targetFilenames.Add(targetFilename);

				Console.WriteLine($"{snapFilename}\t=>\t{targetFilename}");

				if (table != null)
					table.Rows.Add(version, machineName, lastWriteTime, snapFilename, targetFilename);
			}

			return targetFilenames.ToArray();
		}
	}
}
