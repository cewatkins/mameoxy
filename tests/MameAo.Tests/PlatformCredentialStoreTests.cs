using System;
using System.Collections.Generic;
using System.IO;
using Spludlow.MameAO.Platform;

namespace MameAo.Tests
{
	public sealed class PlatformCredentialStoreTests
	{
		private static Func<string, string> DictionaryLookup(Dictionary<string, string> values)
		{
			return name => values.TryGetValue(name, out string value) ? value : null;
		}

		[Fact]
		public void Get_Prefers_MameAo_Environment_Then_Raw_Then_File()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			File.WriteAllText(path, "archive.org=from-file\n");

			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["MAMEAO_ARCHIVE_ORG"] = "from-prefixed",
				["archive.org"] = "from-raw"
			};

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, DictionaryLookup(environment));

			Assert.Equal("from-prefixed", store.Get("archive.org"));
			Assert.True(store.Has("archive.org"));

			environment.Remove("MAMEAO_ARCHIVE_ORG");
			Assert.Equal("from-raw", store.Get("archive.org"));

			environment.Remove("archive.org");
			Assert.Equal("from-file", store.Get("archive.org"));

			Assert.Null(store.Get("missing"));
			Assert.False(store.Has("missing"));
		}

		[Fact]
		public void Get_Maps_Non_Alphanumerics_To_Underscores()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			Dictionary<string, string> environment = new Dictionary<string, string>
			{
				["MAMEAO_INTERNET_ARCHIVE_ORG_USER"] = "prefixed"
			};

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, DictionaryLookup(environment));

			Assert.Equal("prefixed", store.Get("internet-archive.org user"));
		}

		[Fact]
		public void Get_Uses_The_Real_Environment_By_Default()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			string variable = "MAMEAO_PLATFORM_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
			string name = variable.Substring("MAMEAO_".Length).ToLowerInvariant();

			try
			{
				Environment.SetEnvironmentVariable(variable, "from-real-env");

				EnvFileCredentialStore store = new EnvFileCredentialStore(path);
				Assert.Equal("from-real-env", store.Get(name));
			}
			finally
			{
				Environment.SetEnvironmentVariable(variable, null);
			}
		}

		[Fact]
		public void Set_Creates_The_File_With_0600()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string directory = temporary.DirectoryPath("config");
			string path = Path.Combine(directory, "credentials");

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);
			store.Set("archive.org", "secret-value");

			Assert.True(File.Exists(path));
			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite,
				File.GetUnixFileMode(path));
			Assert.Equal("secret-value", store.Get("archive.org"));
		}

		[Fact]
		public void Set_Creates_Missing_Parent_Directories()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = Path.Combine(temporary.Root, "a", "b", "credentials");

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);
			store.Set("name", "value");

			Assert.True(File.Exists(path));
		}

		[Fact]
		public void Set_Preserves_Comments_And_Other_Entries()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			File.WriteAllText(path, "# keep this comment\nother=keep\narchive.org=old\n\n");

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);

			store.Set("archive.org", "new");
			store.Set("added", "value");

			string[] lines = File.ReadAllLines(path);

			Assert.Contains("# keep this comment", lines);
			Assert.Contains("other=keep", lines);
			Assert.Contains("archive.org=new", lines);
			Assert.Contains("added=value", lines);
			Assert.DoesNotContain("archive.org=old", lines);
			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite,
				File.GetUnixFileMode(path));
		}

		[Fact]
		public void Set_Overwrite_Leaves_No_Temporary_Files()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			File.WriteAllText(path, "first=one\n");

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);

			store.Set("first", "updated");
			store.Set("second", "two");

			Assert.Single(Directory.GetFileSystemEntries(temporary.Root));
			Assert.Equal("updated", store.Get("first"));
			Assert.Equal("two", store.Get("second"));
		}

		[Fact]
		public void Set_Tightens_Loose_File_Permissions()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			File.WriteAllText(path, "name=old\n");

			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead
				| UnixFileMode.UserWrite
				| UnixFileMode.GroupRead
				| UnixFileMode.OtherRead);

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);
			store.Set("name", "new");

			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite,
				File.GetUnixFileMode(path));
			Assert.Equal("new", store.Get("name"));
		}

		[Fact]
		public void Set_Error_Messages_Never_Contain_The_Value()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);

			const string secret = "super-secret-token";

			Exception lineBreak = Assert.Throws<ArgumentException>(
				() => store.Set("name", secret + "\nsecond-line"));

			Assert.DoesNotContain(secret, lineBreak.Message);
			Assert.DoesNotContain(secret, lineBreak.ToString());

			Exception badName = Assert.Throws<ArgumentException>(
				() => store.Set("bad=name", secret));

			Assert.DoesNotContain(secret, badName.Message);
			Assert.DoesNotContain(secret, badName.ToString());

			Assert.False(File.Exists(path));
		}

		[Fact]
		public void StorePath_Comes_From_AppPaths()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			AppPaths paths = new AppPaths(
				temporary.DirectoryPath("data"),
				temporary.DirectoryPath("config"),
				temporary.DirectoryPath("cache"));

			EnvFileCredentialStore store = new EnvFileCredentialStore(paths, _ => null);

			Assert.Equal(paths.CredentialsFile, store.StorePath);
			Assert.Equal(Path.Combine(paths.ConfigDirectory, "credentials"), store.StorePath);
		}

		[Fact]
		public void Get_Ignores_Comments_And_Malformed_Lines()
		{
			using PlatformTempDir temporary = new PlatformTempDir();

			string path = temporary.FilePath("credentials");
			File.WriteAllText(path, "# comment\n\ngarbage-line\n=nameless\nvalid=yes\n");

			EnvFileCredentialStore store = new EnvFileCredentialStore(path, _ => null);

			Assert.Equal("yes", store.Get("valid"));
			Assert.Null(store.Get("garbage-line"));
			Assert.Null(store.Get(""));
			Assert.Null(store.Get(string.Empty));
		}
	}
}
