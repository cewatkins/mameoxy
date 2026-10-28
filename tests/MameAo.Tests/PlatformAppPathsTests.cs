using System;
using System.Collections.Generic;
using System.IO;
using Spludlow.MameAO.Platform;

namespace MameAo.Tests
{
	public sealed class PlatformAppPathsTests
	{
		private static Func<string, string> Lookup(Dictionary<string, string> values)
		{
			return name => values.TryGetValue(name, out string value) ? value : null;
		}

		[Fact]
		public void Resolve_Explicit_Override_Beats_Every_Environment_Variable()
		{
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["HOME"] = "/home/tester",
				["MAMEAO_DATA_DIR"] = "/env/mameao-data",
				["MAMEAO_CONFIG_DIR"] = "/env/mameao-config",
				["MAMEAO_CACHE_DIR"] = "/env/mameao-cache",
				["XDG_DATA_HOME"] = "/xdg/data",
				["XDG_CONFIG_HOME"] = "/xdg/config",
				["XDG_CACHE_HOME"] = "/xdg/cache"
			};

			AppPaths paths = AppPaths.Resolve(
				"/explicit/data",
				"/explicit/config",
				"/explicit/cache",
				Lookup(environment));

			Assert.Equal(Path.GetFullPath("/explicit/data"), paths.DataDirectory);
			Assert.Equal(Path.GetFullPath("/explicit/config"), paths.ConfigDirectory);
			Assert.Equal(Path.GetFullPath("/explicit/cache"), paths.CacheDirectory);
		}

		[Fact]
		public void Resolve_MameAo_Directory_Beats_Xdg()
		{
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["HOME"] = "/home/tester",
				["MAMEAO_DATA_DIR"] = "/env/mameao-data",
				["MAMEAO_CONFIG_DIR"] = "/env/mameao-config",
				["MAMEAO_CACHE_DIR"] = "/env/mameao-cache",
				["XDG_DATA_HOME"] = "/xdg/data",
				["XDG_CONFIG_HOME"] = "/xdg/config",
				["XDG_CACHE_HOME"] = "/xdg/cache"
			};

			AppPaths paths = AppPaths.Resolve(null, null, null, Lookup(environment));

			Assert.Equal(Path.GetFullPath("/env/mameao-data"), paths.DataDirectory);
			Assert.Equal(Path.GetFullPath("/env/mameao-config"), paths.ConfigDirectory);
			Assert.Equal(Path.GetFullPath("/env/mameao-cache"), paths.CacheDirectory);
		}

		[Fact]
		public void Resolve_Xdg_Beats_The_Home_Default()
		{
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["HOME"] = "/home/tester",
				["XDG_DATA_HOME"] = "/xdg/data",
				["XDG_CONFIG_HOME"] = "/xdg/config",
				["XDG_CACHE_HOME"] = "/xdg/cache"
			};

			AppPaths paths = AppPaths.Resolve(null, null, null, Lookup(environment));

			Assert.Equal(Path.Combine("/xdg/data", "mame-ao"), paths.DataDirectory);
			Assert.Equal(Path.Combine("/xdg/config", "mame-ao"), paths.ConfigDirectory);
			Assert.Equal(Path.Combine("/xdg/cache", "mame-ao"), paths.CacheDirectory);
		}

		[Fact]
		public void Resolve_Falls_Back_To_The_Home_Directory()
		{
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["HOME"] = "/home/tester"
			};

			AppPaths paths = AppPaths.Resolve(null, null, null, Lookup(environment));

			Assert.Equal(Path.Combine("/home/tester", ".local", "share", "mame-ao"), paths.DataDirectory);
			Assert.Equal(Path.Combine("/home/tester", ".config", "mame-ao"), paths.ConfigDirectory);
			Assert.Equal(Path.Combine("/home/tester", ".cache", "mame-ao"), paths.CacheDirectory);
		}

		[Fact]
		public void Resolve_Derives_Config_File_And_Credentials_Paths()
		{
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["HOME"] = "/home/tester",
				["XDG_CONFIG_HOME"] = "/xdg/config"
			};

			AppPaths paths = AppPaths.Resolve(null, null, null, Lookup(environment));

			Assert.Equal(Path.Combine("/xdg/config", "mame-ao", "mame-ao.conf"), paths.ConfigFile);
			Assert.Equal(Path.Combine("/xdg/config", "mame-ao", "credentials"), paths.CredentialsFile);
		}

		[Fact]
		public void EnsureCreated_Creates_All_Three_Directories()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			AppPaths paths = new AppPaths(
				Path.Combine(temporary.Root, "data"),
				Path.Combine(temporary.Root, "config"),
				Path.Combine(temporary.Root, "cache"));

			paths.EnsureCreated();

			Assert.True(Directory.Exists(paths.DataDirectory));
			Assert.True(Directory.Exists(paths.ConfigDirectory));
			Assert.True(Directory.Exists(paths.CacheDirectory));
		}
	}
}
