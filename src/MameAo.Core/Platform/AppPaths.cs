using System;
using System.IO;

namespace Spludlow.MameAO.Platform
{
	/// <summary>
	/// XDG-compliant layout for the Linux port.
	///
	///   data    $MAMEAO_DATA_DIR   > $XDG_DATA_HOME/mame-ao   > ~/.local/share/mame-ao
	///   config  $MAMEAO_CONFIG_DIR > $XDG_CONFIG_HOME/mame-ao > ~/.config/mame-ao
	///   cache   $MAMEAO_CACHE_DIR  > $XDG_CACHE_HOME/mame-ao  > ~/.cache/mame-ao
	///
	/// An explicit --data-dir / --config-dir wins over everything. All three are
	/// created with 0700 on first use. This class is pure (no I/O beyond mkdir) so
	/// it is unit-testable by passing an environment lookup function.
	/// </summary>
	public sealed class AppPaths
	{
		public string DataDirectory { get; }
		public string ConfigDirectory { get; }
		public string CacheDirectory { get; }

		public string ConfigFile => Path.Combine(ConfigDirectory, "mame-ao.conf");
		public string CredentialsFile => Path.Combine(ConfigDirectory, "credentials");

		public AppPaths(string dataDirectory, string configDirectory, string cacheDirectory)
		{
			DataDirectory = Path.GetFullPath(dataDirectory);
			ConfigDirectory = Path.GetFullPath(configDirectory);
			CacheDirectory = Path.GetFullPath(cacheDirectory);
		}

		public void EnsureCreated()
		{
			EnsureDirectory(DataDirectory);
			EnsureDirectory(ConfigDirectory);
			EnsureDirectory(CacheDirectory);
		}

		private static void EnsureDirectory(string directory)
		{
			Directory.CreateDirectory(directory);
			if (OperatingSystem.IsWindows() == false)
			{
				try
				{
					File.SetUnixFileMode(directory,
						UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
				catch (Exception)
				{
					// Non-POSIX filesystem (eg some network mounts); not fatal.
				}
			}
		}

		/// <summary>
		/// Resolve the layout. <paramref name="getEnv"/> is injectable for tests;
		/// pass null to use the real environment.
		/// </summary>
		public static AppPaths Resolve(
			string dataOverride,
			string configOverride,
			string cacheOverride,
			Func<string, string> getEnv = null)
		{
			getEnv ??= Environment.GetEnvironmentVariable;

			string home = getEnv("HOME");
			if (string.IsNullOrWhiteSpace(home))
				home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(home))
				throw new IOException("Cannot determine the user's home directory (HOME is not set).");

			string data = First(
				dataOverride,
				getEnv("MAMEAO_DATA_DIR"),
				Combine(getEnv("XDG_DATA_HOME"), "mame-ao"),
				Path.Combine(home, ".local", "share", "mame-ao"));

			string config = First(
				configOverride,
				getEnv("MAMEAO_CONFIG_DIR"),
				Combine(getEnv("XDG_CONFIG_HOME"), "mame-ao"),
				Path.Combine(home, ".config", "mame-ao"));

			string cache = First(
				cacheOverride,
				getEnv("MAMEAO_CACHE_DIR"),
				Combine(getEnv("XDG_CACHE_HOME"), "mame-ao"),
				Path.Combine(home, ".cache", "mame-ao"));

			return new AppPaths(data, config, cache);
		}

		private static string Combine(string root, string leaf)
		{
			if (string.IsNullOrWhiteSpace(root))
				return null;
			return Path.Combine(root, leaf);
		}

		private static string First(params string[] candidates)
		{
			foreach (string candidate in candidates)
			{
				if (string.IsNullOrWhiteSpace(candidate) == false)
					return candidate;
			}
			throw new IOException("No usable directory candidate.");
		}
	}
}
