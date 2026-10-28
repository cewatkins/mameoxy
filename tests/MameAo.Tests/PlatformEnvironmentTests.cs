using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Spludlow.MameAO.Platform;

namespace MameAo.Tests
{
	public sealed class PlatformEnvironmentTests
	{
		[Fact]
		public void Which_Finds_A_Known_Executable()
		{
			string shell = LinuxEnvironment.Which("/bin/sh");

			Assert.NotNull(shell);
			Assert.Equal("/bin/sh", shell);
			Assert.True(File.Exists(shell));
		}

		[Fact]
		public void Which_Returns_Null_For_A_Nonsense_Name()
		{
			Assert.Null(LinuxEnvironment.Which("mameao-no-such-executable-" + Guid.NewGuid().ToString("N")));
		}

		[Fact]
		public void Which_Returns_Null_For_Empty_Input()
		{
			Assert.Null(LinuxEnvironment.Which(string.Empty));
			Assert.Null(LinuxEnvironment.Which(null));
		}

		[Fact]
		public void Which_Finds_Executables_On_Path_By_Bare_Name()
		{
			// /bin/sh is guaranteed on Linux and PATH normally contains /bin or /usr/bin.
			string shell = LinuxEnvironment.Which("sh");

			if (shell == null)
				return;

			Assert.True(Path.IsPathRooted(shell));
			Assert.Equal("sh", Path.GetFileName(shell));
		}

		[Fact]
		public void MameCandidates_Are_Ordered_Environment_Path_Flatpak()
		{
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["MAME"] = "/opt/mame/mame"
			};

			IReadOnlyList<MameCandidate> candidates = LinuxEnvironment.MameCandidates(
				name => environment.TryGetValue(name, out string value) ? value : null,
				name => name == "mame" ? "/usr/bin/mame" : name == "flatpak" ? "/usr/bin/flatpak" : null,
				() => true);

			Assert.Equal(3, candidates.Count);

			Assert.Equal(ProviderKind.Environment, candidates[0].Kind);
			Assert.Equal("/opt/mame/mame", candidates[0].PathOrWrapper);

			Assert.Equal(ProviderKind.Path, candidates[1].Kind);
			Assert.Equal("/usr/bin/mame", candidates[1].PathOrWrapper);

			Assert.Equal(ProviderKind.Flatpak, candidates[2].Kind);
			Assert.Equal("flatpak run --command=mame org.mamedev.MAME", candidates[2].PathOrWrapper);
		}

		[Fact]
		public void MameCandidates_Skips_Unset_Providers()
		{
			IReadOnlyList<MameCandidate> candidates = LinuxEnvironment.MameCandidates(
				_ => null,
				_ => null,
				() => false);

			Assert.Empty(candidates);
		}

		[Fact]
		public void MameCandidates_Omits_Flatpak_When_Not_Installed()
		{
			IReadOnlyList<MameCandidate> candidates = LinuxEnvironment.MameCandidates(
				_ => null,
				name => name == "mame" ? "/usr/bin/mame" : "/usr/bin/flatpak",
				() => false);

			Assert.Single(candidates);
			Assert.Equal(ProviderKind.Path, candidates[0].Kind);
		}

		[Fact]
		public void MameCandidates_Omits_Flatpak_When_The_Binary_Is_Missing()
		{
			IReadOnlyList<MameCandidate> candidates = LinuxEnvironment.MameCandidates(
				_ => null,
				_ => null,
				() => true);

			Assert.Empty(candidates);
		}

		[Fact]
		public void MameCandidates_Deduplicates_Identical_Executables()
		{
			IReadOnlyList<MameCandidate> candidates = LinuxEnvironment.MameCandidates(
				_ => "/usr/bin/mame",
				_ => "/usr/bin/mame",
				() => false);

			Assert.Single(candidates);
			Assert.Equal(ProviderKind.Environment, candidates[0].Kind);
		}

		[Fact]
		public void TryOpen_Returns_False_When_Headless()
		{
			XdgShellOpener opener = new XdgShellOpener(
				_ => "/usr/bin/xdg-open",
				() => false);

			Assert.False(opener.TryOpen("https://example.invalid/"));
			Assert.False(opener.TryOpen("/tmp/some-file.txt"));
		}

		[Fact]
		public void TryOpen_Returns_False_Without_XdgOpen()
		{
			XdgShellOpener opener = new XdgShellOpener(
				_ => null,
				() => true);

			Assert.False(opener.TryOpen("https://example.invalid/"));
		}

		[Fact]
		public void TryOpen_Returns_False_For_Empty_Target()
		{
			XdgShellOpener opener = new XdgShellOpener(
				_ => "/usr/bin/xdg-open",
				() => true);

			Assert.False(opener.TryOpen(null));
			Assert.False(opener.TryOpen(""));
			Assert.False(opener.TryOpen("   "));
		}
	}
}
