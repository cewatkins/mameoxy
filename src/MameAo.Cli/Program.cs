using System;
using System.Collections.Generic;
using System.IO;

using Spludlow.MameAO;
using Spludlow.MameAO.Platform;

namespace Spludlow.MameAO.Cli
{
	/// <summary>
	/// Linux host: resolves the XDG layout, installs the platform services, then
	/// hands over to the shared processor.
	///
	/// The core project deliberately knows nothing about Unix: it talks to
	/// PlatformServices for links, the desktop and credentials. That keeps the
	/// upstream code close to its original shape and leaves the Windows build a
	/// possibility.
	/// </summary>
	internal static class Program
	{
		private static int Main(string[] args)
		{
			HostOptions options;

			try
			{
				options = HostOptions.Parse(args);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e.Message);
				Console.Error.WriteLine();
				Console.Error.WriteLine(HostOptions.Usage);
				return 2;
			}

			if (options.ShowHelp == true)
			{
				Console.WriteLine(HostOptions.Usage);
				return 0;
			}

			if (options.ShowVersion == true)
			{
				Console.WriteLine($"mame-ao {Globals.AssemblyVersion}");
				Console.WriteLine(HostOptions.DescribeHost());
				return 0;
			}

			try
			{
				return Run(options);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"!!! {e.Message}");
				Console.Error.WriteLine();
				Console.Error.WriteLine(e.ToString());
				return 1;
			}
		}

		private static int Run(HostOptions options)
		{
			// systemd stops the unit with SIGTERM. The console loop holds stdin, so
			// the default handler can take a while; exit explicitly and promptly.
			using System.Runtime.InteropServices.PosixSignalRegistration sigterm =
				System.Runtime.InteropServices.PosixSignalRegistration.Create(
					System.Runtime.InteropServices.PosixSignal.SIGTERM,
					context =>
					{
						context.Cancel = true;
						Console.WriteLine("SIGTERM received, stopping.");
						Environment.Exit(0);
					});

			AppPaths paths = AppPaths.Resolve(options.DataDirectory, options.ConfigDirectory, options.CacheDirectory);
			paths.EnsureCreated();

			PlatformServices.Current = LinuxPlatform.Create(paths);

			if (string.IsNullOrWhiteSpace(options.MamePath) == false)
				Globals.MamePath = options.MamePath;

			Globals.ListenAddress = HostOptions.NormaliseListen(options.Listen);

			bool hasGraphicalSession = LinuxEnvironment.HasGraphicalSession();
			Globals.Headless = options.Headless || hasGraphicalSession == false;

			// One instance per data directory. The HttpListener bind would fail later
			// with a less helpful message, and the SQLite store must not be opened by
			// two processes.
			using FileStream instanceLock = AcquireInstanceLock(paths);

			if (OperatingSystem.IsWindows() == false)
				DomeBtPayload.EnsureExtracted(paths.CacheDirectory);

			Globals.AO = new MameAOProcessor(paths.DataDirectory);

			if (options.OperationArguments.ContainsKey("operation") == true)
			{
				Dictionary<string, string> parameters = new Dictionary<string, string>(options.OperationArguments, StringComparer.OrdinalIgnoreCase);

				if (parameters.ContainsKey("directory") == false)
					parameters["directory"] = paths.DataDirectory;

				if (parameters.ContainsKey("version") == false)
					parameters["version"] = "0";

				return Operations.ProcessOperation(parameters);
			}

			if (Globals.Headless == true)
				Console.WriteLine("Running headless: no browser will be opened, MAME gets -video none -sound none.");

			Globals.AO.Run();

			return 0;
		}

		private static FileStream AcquireInstanceLock(AppPaths paths)
		{
			string filename = Path.Combine(paths.ConfigDirectory, "mame-ao.lock");

			try
			{
				return new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException)
			{
				throw new ApplicationException(
					$"Another mame-ao is already using this data directory ({paths.DataDirectory}). " +
					$"Stop it first, or use a different --data-dir. Lock file: {filename}");
			}
		}
	}
}
