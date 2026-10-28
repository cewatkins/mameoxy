using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Spludlow.MameAO
{
	public enum MameProviderKind
	{
		ExplicitPath,
		PathLookup,
		Flatpak,
	}

	/// <summary>
	/// Thrown when no usable MAME can be found. Carries the attempts made so the
	/// message can tell the user exactly what was tried.
	/// </summary>
	public sealed class MameNotFoundException : Exception
	{
		public IReadOnlyList<string> Attempts { get; }

		public MameNotFoundException(string message, IReadOnlyList<string> attempts) : base(message)
		{
			Attempts = attempts;
		}
	}

	/// <summary>
	/// A resolved MAME installation. On Linux a "binary" is either a real
	/// executable or a launcher prefix (Flatpak), so everything goes through
	/// <see cref="Command"/> rather than assuming a file path.
	/// </summary>
	public sealed class MameInstall
	{
		public MameProviderKind Kind { get; init; }

		/// <summary>argv prefix, e.g. ["/usr/games/mame"] or ["flatpak","run","org.mamedev.MAME"].</summary>
		public IReadOnlyList<string> Prefix { get; init; }

		/// <summary>Human readable description of where this came from.</summary>
		public string Detail { get; init; }

		/// <summary>mame-ao directory naming, e.g. "0288".</summary>
		public string Version { get; init; }

		/// <summary>Raw text of `mame -version`.</summary>
		public string VersionRaw { get; init; }

		public string HashPath { get; init; }

		public string HomePath { get; init; }

		public string ChdmanCommand { get; init; }

		public IReadOnlyList<string> ChdmanPrefix { get; init; }

		public string[] Command(params string[] args)
		{
			return Prefix.Concat(args ?? Array.Empty<string>()).ToArray();
		}

		public string[] ChdmanCommandLine(params string[] args)
		{
			if (ChdmanPrefix == null)
				return null;
			return ChdmanPrefix.Concat(args ?? Array.Empty<string>()).ToArray();
		}

		public ProcessResult Run(string workingDirectory, TimeSpan timeout, params string[] args)
		{
			return MameProcess.Run(Command(args), workingDirectory, timeout);
		}

		public override string ToString()
		{
			return $"{string.Join(' ', Prefix)} ({Detail}) version {Version} [{VersionRaw}]";
		}
	}

	public sealed class ProcessResult
	{
		public int ExitCode { get; init; }
		public string StandardOutput { get; init; }
		public string StandardError { get; init; }
		public bool TimedOut { get; init; }

		public string Combined => (StandardOutput ?? "") + "\n" + (StandardError ?? "");
	}

	/// <summary>
	/// Runs a program without a shell, with an argv list. stderr is merged into
	/// stdout because MAME writes half its errors there.
	/// </summary>
	public static class MameProcess
	{
		public static ProcessResult Run(IReadOnlyList<string> argv, string workingDirectory, TimeSpan timeout)
		{
			if (argv == null || argv.Count == 0)
				throw new ArgumentException("No command supplied.", nameof(argv));

			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = argv[0],
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				StandardOutputEncoding = System.Text.Encoding.UTF8,
				StandardErrorEncoding = System.Text.Encoding.UTF8,
				CreateNoWindow = true,
			};

			for (int index = 1; index < argv.Count; ++index)
				startInfo.ArgumentList.Add(argv[index]);

			if (string.IsNullOrEmpty(workingDirectory) == false)
				startInfo.WorkingDirectory = workingDirectory;

			using (Process process = Process.Start(startInfo) ?? throw new IOException($"Could not start '{argv[0]}'."))
			{
				process.StandardInput.Close();

				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();

				bool exited = process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));

				if (exited == false)
				{
					try { process.Kill(entireProcessTree: true); } catch (Exception) { }
					return new ProcessResult
					{
						ExitCode = -1,
						TimedOut = true,
						StandardOutput = SafeResult(stdout),
						StandardError = SafeResult(stderr),
					};
				}

				return new ProcessResult
				{
					ExitCode = process.ExitCode,
					StandardOutput = SafeResult(stdout),
					StandardError = SafeResult(stderr),
				};
			}
		}

		private static string SafeResult(System.Threading.Tasks.Task<string> task)
		{
			try { return task.GetAwaiter().GetResult() ?? ""; }
			catch (Exception) { return ""; }
		}
	}

	/// <summary>
	/// Finds a MAME. Order: explicit path (--mame-path / config / $MAME),
	/// then PATH, then Flatpak. Never downloads anything.
	/// </summary>
	public static class MameLocator
	{
		public static MameInstall Locate(string explicitPath, Func<string, string> getEnv = null)
		{
			getEnv ??= Environment.GetEnvironmentVariable;

			List<string> attempts = new List<string>();

			if (string.IsNullOrWhiteSpace(explicitPath) == true)
			{
				string fromEnv = getEnv("MAME");
				if (string.IsNullOrWhiteSpace(fromEnv) == false)
				{
					explicitPath = fromEnv;
					attempts.Add($"$MAME = {fromEnv}");
				}
			}

			if (string.IsNullOrWhiteSpace(explicitPath) == false)
			{
				attempts.Add($"explicit path '{explicitPath}'");

				if (File.Exists(explicitPath) == false)
					throw new MameNotFoundException($"MAME not found at '{explicitPath}'.", attempts);

				if (IsExecutable(explicitPath) == false)
					throw new MameNotFoundException($"'{explicitPath}' is not executable.", attempts);

				return Probe(MameProviderKind.ExplicitPath, new[] { explicitPath }, $"explicit path {explicitPath}");
			}

			string onPath = Which("mame", getEnv);
			attempts.Add("PATH lookup of 'mame'");
			if (onPath != null)
				return Probe(MameProviderKind.PathLookup, new[] { onPath }, $"found on PATH as {onPath}");

			attempts.Add("Flatpak org.mamedev.MAME");
			if (FlatpakInstalled(getEnv))
			{
				string[] prefix = { "flatpak", "run", "org.mamedev.MAME" };
				return Probe(MameProviderKind.Flatpak, prefix, "Flatpak org.mamedev.MAME");
			}

			throw new MameNotFoundException(
				"No MAME found. Tried: " + string.Join("; ", attempts) +
				". Install MAME (distro package or Flatpak), or pass --mame-path /path/to/mame, or set $MAME.",
				attempts);
		}

		private static MameInstall Probe(MameProviderKind kind, string[] prefix, string detail)
		{
			ProcessResult version = MameProcess.Run(
				prefix.Concat(new[] { "-version" }).ToArray(), null, TimeSpan.FromSeconds(30));

			if (version.TimedOut == true)
				throw new MameNotFoundException($"'{string.Join(' ', prefix)} -version' timed out.", new[] { detail });

			string versionText = (version.StandardOutput ?? "").Trim();
			if (version.ExitCode != 0 || versionText.Length == 0)
			{
				string error = string.IsNullOrWhiteSpace(version.StandardError) ? versionText : version.StandardError.Trim();
				throw new MameNotFoundException(
					$"'{string.Join(' ', prefix)} -version' failed (exit {version.ExitCode}): {Trim(error)}",
					new[] { detail });
			}

			string versionRaw = versionText.Split('\n')[0].Trim();
			string directoryVersion = VersionToDirectoryName(versionRaw);

			ProcessResult config = MameProcess.Run(
				prefix.Concat(new[] { "-showconfig" }).ToArray(), null, TimeSpan.FromSeconds(60));

			string homePath = ParseConfigValue(config.StandardOutput, "homepath");
			string hashPathRaw = ParseConfigValue(config.StandardOutput, "hashpath");

			string hashPath = ResolveHashPath(hashPathRaw, homePath, prefix);

			string chdmanOnPath = Which("chdman", null);
			string chdmanNextToBinary = null;
			if (kind != MameProviderKind.Flatpak && prefix.Length > 0)
			{
				string candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prefix[0])) ?? "", "chdman");
				if (File.Exists(candidate) == true)
					chdmanNextToBinary = candidate;
			}

			string[] chdmanPrefix = null;
			string chdmanCommand = null;
			if (chdmanNextToBinary != null)
			{
				chdmanPrefix = new[] { chdmanNextToBinary };
				chdmanCommand = chdmanNextToBinary;
			}
			else if (chdmanOnPath != null)
			{
				chdmanPrefix = new[] { chdmanOnPath };
				chdmanCommand = chdmanOnPath;
			}
			else if (kind == MameProviderKind.Flatpak)
			{
				chdmanPrefix = new[] { "flatpak", "run", "--command=chdman", "org.mamedev.MAME" };
				chdmanCommand = string.Join(' ', chdmanPrefix);
			}

			return new MameInstall
			{
				Kind = kind,
				Prefix = prefix,
				Detail = detail,
				Version = directoryVersion,
				VersionRaw = versionRaw,
				HashPath = hashPath,
				HomePath = homePath,
				ChdmanPrefix = chdmanPrefix,
				ChdmanCommand = chdmanCommand,
			};
		}

		/// <summary>
		/// "0.288 (mame0288-847-g84e1e905e45-dirty)" -> "0288".
		/// Falls back to the git describe tag if the leading semver is missing.
		/// </summary>
		public static string VersionToDirectoryName(string versionText)
		{
			if (string.IsNullOrWhiteSpace(versionText) == false)
			{
				Match semver = Regex.Match(versionText, @"^(\d+)\.(\d+)");
				if (semver.Success == true)
				{
					string major = semver.Groups[1].Value;
					string minor = semver.Groups[2].Value;
					return major + minor.PadLeft(3, '0');
				}

				Match tag = Regex.Match(versionText, @"mame(\d{4})");
				if (tag.Success == true)
					return tag.Groups[1].Value;
			}

			throw new MameNotFoundException($"Cannot parse a MAME version from '{versionText}'.", new[] { versionText });
		}

		/// <summary>
		/// MAME resolves a relative hashpath against the *current directory*, not
		/// homepath (measured). So try, in order: absolute hashpath, homepath +
		/// hashpath, directory of the binary + hashpath, and the usual distro data
		/// directories.
		/// </summary>
		public static string ResolveHashPath(string hashPathRaw, string homePath, string[] prefix)
		{
			List<string> candidates = new List<string>();

			if (string.IsNullOrWhiteSpace(hashPathRaw) == false && Path.IsPathRooted(hashPathRaw) == true)
				candidates.Add(hashPathRaw);

			if (string.IsNullOrWhiteSpace(homePath) == false && string.IsNullOrWhiteSpace(hashPathRaw) == false)
				candidates.Add(Path.Combine(homePath.Trim(), hashPathRaw));

			if (prefix != null && prefix.Length > 0 && File.Exists(prefix[0]) == true)
			{
				string binaryDirectory = Path.GetDirectoryName(Path.GetFullPath(prefix[0]));
				if (string.IsNullOrWhiteSpace(hashPathRaw) == false)
					candidates.Add(Path.Combine(binaryDirectory, hashPathRaw));

				candidates.Add(Path.Combine(binaryDirectory, "hash"));
				candidates.Add(Path.Combine(binaryDirectory, "..", "share", "games", "mame", "hash"));
				candidates.Add(Path.Combine(binaryDirectory, "..", "share", "mame", "hash"));
			}

			candidates.Add("/usr/share/games/mame/hash");
			candidates.Add("/usr/share/mame/hash");

			foreach (string candidate in candidates)
			{
				string full = Path.GetFullPath(candidate);
				if (Directory.Exists(full) == false)
					continue;
				if (Directory.EnumerateFiles(full, "*.xml").Any() == true)
					return full;
			}

			// Not fatal: the database build reports a clear error if software lists
			// are unavailable. Return the first existing candidate or null.
			foreach (string candidate in candidates)
			{
				if (Directory.Exists(candidate) == true)
					return Path.GetFullPath(candidate);
			}

			return null;
		}

		public static string ParseConfigValue(string showConfigOutput, string key)
		{
			if (string.IsNullOrWhiteSpace(showConfigOutput) == true)
				return null;

			foreach (string rawLine in showConfigOutput.Split('\n'))
			{
				string line = rawLine.TrimEnd('\r');
				int index = 0;
				while (index < line.Length && char.IsWhiteSpace(line[index]) == false)
					++index;

				if (string.Equals(line.Substring(0, index), key, StringComparison.OrdinalIgnoreCase) == false)
					continue;

				return line.Substring(index).Trim();
			}

			return null;
		}

		public static bool FlatpakInstalled(Func<string, string> getEnv = null)
		{
			string flatpak = Which("flatpak", getEnv);
			if (flatpak == null)
				return false;

			ProcessResult result = MameProcess.Run(
				new[] { flatpak, "info", "org.mamedev.MAME" }, null, TimeSpan.FromSeconds(30));

			return result.ExitCode == 0;
		}

		/// <summary>PATH search honouring the executable bit (no shell).</summary>
		public static string Which(string executable, Func<string, string> getEnv)
		{
			if (string.IsNullOrWhiteSpace(executable) == true)
				return null;

			if (executable.Contains('/') == true)
				return File.Exists(executable) && IsExecutable(executable) ? Path.GetFullPath(executable) : null;

			getEnv ??= Environment.GetEnvironmentVariable;
			string path = getEnv("PATH") ?? "";
			char separator = OperatingSystem.IsWindows() ? ';' : ':';

			foreach (string directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
			{
				string candidate;
				try { candidate = Path.Combine(directory, executable); }
				catch (ArgumentException) { continue; }

				if (OperatingSystem.IsWindows() == true)
				{
					foreach (string extension in new[] { ".exe", ".cmd", ".bat", "" })
					{
						if (File.Exists(candidate + extension) == true)
							return candidate + extension;
					}
				}
				else if (File.Exists(candidate) == true && IsExecutable(candidate) == true)
				{
					return candidate;
				}
			}

			return null;
		}

		public static bool IsExecutable(string filename)
		{
			if (OperatingSystem.IsWindows() == true)
				return File.Exists(filename);

			try
			{
				UnixFileMode mode = File.GetUnixFileMode(filename);
				return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static string Trim(string text)
		{
			if (string.IsNullOrWhiteSpace(text) == true)
				return "";
			text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
			return text.Length <= 400 ? text : text.Substring(0, 400) + "…";
		}
	}
}
