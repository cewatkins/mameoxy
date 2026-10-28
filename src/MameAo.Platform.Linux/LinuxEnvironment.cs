using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Spludlow.MameAO.Platform
{
	public enum ProviderKind
	{
		Environment,
		Path,
		Flatpak
	}

	/// <summary>
	/// One way of running MAME. <see cref="PathOrWrapper"/> is either an
	/// executable path or an argv-style wrapper command line.
	/// </summary>
	public sealed record MameCandidate(ProviderKind Kind, string PathOrWrapper, string Detail);

	/// <summary>
	/// Small, dependency-free probes for the Linux host. Nothing here starts
	/// MAME or otherwise launches a game; the Flatpak check only reads metadata.
	/// </summary>
	public static class LinuxEnvironment
	{
		private const string FlatpakAppId = "org.mamedev.MAME";
		private const string FlatpakWrapper = "flatpak run --command=mame " + FlatpakAppId;

		[DllImport("libc", EntryPoint = "geteuid")]
		private static extern uint geteuid();

		public static bool IsRoot()
		{
			if (OperatingSystem.IsLinux() == false && OperatingSystem.IsFreeBSD() == false)
				return false;

			try
			{
				return geteuid() == 0;
			}
			catch (DllNotFoundException)
			{
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
		}

		public static bool HasGraphicalSession()
		{
			return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) == false
				|| string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) == false;
		}

		/// <summary>
		/// POSIX-style "which": search PATH in order and return the first regular
		/// file with an executable bit. Returns null when nothing matches.
		/// </summary>
		public static string Which(string exe)
		{
			if (string.IsNullOrWhiteSpace(exe))
				return null;

			if (exe.Contains('/'))
				return IsExecutableFile(exe) ? Path.GetFullPath(exe) : null;

			string path = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrEmpty(path))
				return null;

			foreach (string entry in path.Split(':'))
			{
				string directory = string.IsNullOrEmpty(entry) ? "." : entry;

				string candidate;
				try
				{
					candidate = Path.Combine(directory, exe);
				}
				catch (ArgumentException)
				{
					continue;
				}

				if (IsExecutableFile(candidate))
					return Path.GetFullPath(candidate);
			}

			return null;
		}

		private static bool IsExecutableFile(string path)
		{
			try
			{
				if (File.Exists(path) == false)
					return false;

				UnixFileMode mode = File.GetUnixFileMode(path);
				UnixFileMode execute =
					UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

				return (mode & execute) != 0;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		/// Ordered provider candidates: $MAME, then mame on PATH, then Flatpak.
		/// </summary>
		public static IReadOnlyList<MameCandidate> MameCandidates()
		{
			return MameCandidates(
				Environment.GetEnvironmentVariable,
				Which,
				IsFlatpakInstalled);
		}

		public static IReadOnlyList<MameCandidate> MameCandidates(
			Func<string, string> getEnv,
			Func<string, string> which,
			Func<bool> flatpakInstalled)
		{
			if (getEnv == null)
				throw new ArgumentNullException(nameof(getEnv));
			if (which == null)
				throw new ArgumentNullException(nameof(which));

			List<MameCandidate> candidates = new List<MameCandidate>();

			string fromEnvironment = getEnv("MAME");
			if (string.IsNullOrWhiteSpace(fromEnvironment) == false)
				candidates.Add(new MameCandidate(ProviderKind.Environment, fromEnvironment, "MAME environment variable"));

			string fromPath = which("mame");
			if (string.IsNullOrWhiteSpace(fromPath) == false && ContainsExecutable(candidates, fromPath) == false)
				candidates.Add(new MameCandidate(ProviderKind.Path, fromPath, "mame found on PATH"));

			if (flatpakInstalled != null)
			{
				bool installed;
				try
				{
					installed = flatpakInstalled();
				}
				catch (Exception)
				{
					installed = false;
				}

				if (installed && which("flatpak") != null)
					candidates.Add(new MameCandidate(ProviderKind.Flatpak, FlatpakWrapper, FlatpakAppId + " installed via Flatpak"));
			}

			return candidates;
		}

		private static bool ContainsExecutable(List<MameCandidate> candidates, string path)
		{
			foreach (MameCandidate candidate in candidates)
			{
				if (candidate.Kind != ProviderKind.Flatpak
					&& string.Equals(candidate.PathOrWrapper, path, StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Read-only Flatpak metadata probe. Runs "flatpak info" with a short
		/// timeout; never runs the application itself.
		/// </summary>
		private static bool IsFlatpakInstalled()
		{
			string flatpak = Which("flatpak");
			if (flatpak == null)
				return false;

			try
			{
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = flatpak,
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};
				startInfo.ArgumentList.Add("info");
				startInfo.ArgumentList.Add(FlatpakAppId);

				using (Process process = Process.Start(startInfo))
				{
					if (process == null)
						return false;

					Task<string> output = process.StandardOutput.ReadToEndAsync();
					Task<string> error = process.StandardError.ReadToEndAsync();

					if (process.WaitForExit(5000) == false)
					{
						try
						{
							process.Kill(entireProcessTree: true);
						}
						catch (Exception)
						{
						}
						return false;
					}

					try
					{
						Task.WaitAll(new Task[] { output, error }, 1000);
					}
					catch (Exception)
					{
					}

					return process.ExitCode == 0;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
