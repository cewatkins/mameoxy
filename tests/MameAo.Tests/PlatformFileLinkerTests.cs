using System;
using System.IO;
using Spludlow.MameAO.Platform;

namespace MameAo.Tests
{
	public sealed class PlatformFileLinkerTests
	{
		[Fact]
		public void Create_Makes_A_Symlink_That_Reads_Back()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string target = temporary.FilePath("target.bin");
			string link = temporary.FilePath("link.bin");

			File.WriteAllText(target, "payload");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);

			Assert.True(linker.IsSupported, "the test filesystem should support symlinks");
			Assert.True(linker.Create(link, target));

			FileSystemInfo entry = new FileInfo(link);
			Assert.Equal(Path.GetFullPath(target), entry.LinkTarget);
			Assert.Equal("payload", File.ReadAllText(link));
		}

		[Fact]
		public void Create_Absolutises_A_Relative_Target()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string target = temporary.FilePath("target.bin");
			string link = temporary.FilePath("link.bin");

			File.WriteAllText(target, "relative");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);
			Assert.True(linker.Create(link, target));

			Assert.Equal(Path.GetFullPath(target), new FileInfo(link).LinkTarget);
		}

		[Fact]
		public void Create_Refuses_To_Overwrite_A_Real_File()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string target = temporary.FilePath("target.bin");
			string link = temporary.FilePath("existing.bin");

			File.WriteAllText(target, "new");
			File.WriteAllText(link, "keep-me");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);

			Assert.False(linker.Create(link, target));
			Assert.Null(new FileInfo(link).LinkTarget);
			Assert.Equal("keep-me", File.ReadAllText(link));
		}

		[Fact]
		public void Create_Replaces_An_Existing_Symlink()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string first = temporary.FilePath("first.bin");
			string second = temporary.FilePath("second.bin");
			string link = temporary.FilePath("link.bin");

			File.WriteAllText(first, "first");
			File.WriteAllText(second, "second");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);

			Assert.True(linker.Create(link, first));
			Assert.True(linker.Create(link, second));

			Assert.Equal(Path.GetFullPath(second), new FileInfo(link).LinkTarget);
			Assert.Equal("second", File.ReadAllText(link));
			Assert.Equal("first", File.ReadAllText(first));
		}

		[Fact]
		public void Create_Replaces_A_Symlink_To_A_Directory()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string first = temporary.DirectoryPath("first");
			string second = temporary.DirectoryPath("second");
			string link = temporary.FilePath("link");

			File.WriteAllText(Path.Combine(first, "marker"), "first");
			File.WriteAllText(Path.Combine(second, "marker"), "second");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);

			Assert.True(linker.Create(link, first));
			Assert.True(linker.Create(link, second));

			Assert.Equal(Path.GetFullPath(second), new FileInfo(link).LinkTarget);
			Assert.Equal("second", File.ReadAllText(Path.Combine(link, "marker")));
			Assert.True(Directory.Exists(first));
			Assert.Equal("first", File.ReadAllText(Path.Combine(first, "marker")));
		}

		[Fact]
		public void Create_Returns_False_When_The_Target_Directory_Is_Missing()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string target = temporary.FilePath("target.bin");
			string link = Path.Combine(temporary.Root, "missing", "link.bin");

			File.WriteAllText(target, "payload");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);

			Assert.False(linker.Create(link, target));
			Assert.False(File.Exists(link));
		}

		[Fact]
		public void IsSupported_Probe_Leaves_Nothing_Behind()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string probeRoot = temporary.DirectoryPath("probe");
			UnixFileLinker linker = new UnixFileLinker(probeRoot);

			bool supported = linker.IsSupported;

			Assert.True(supported);
			Assert.Equal(supported, linker.IsSupported);
			Assert.Empty(Directory.GetFileSystemEntries(probeRoot));
		}

		[Fact]
		public void CreateHardLink_Is_Best_Effort()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string target = temporary.FilePath("target.bin");
			string link = temporary.FilePath("hard.bin");

			File.WriteAllText(target, "same-inode");

			UnixFileLinker linker = new UnixFileLinker(temporary.Root);

			bool created = linker.CreateHardLink(link, target);
			if (created == false)
				return;

			Assert.True(File.Exists(link));
			Assert.Null(new FileInfo(link).LinkTarget);
			Assert.Equal("same-inode", File.ReadAllText(link));
		}
	}
}
