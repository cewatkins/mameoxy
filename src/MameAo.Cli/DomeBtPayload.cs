using System;
using System.IO;

namespace Spludlow.MameAO.Cli
{
	/// <summary>
	/// The BitTorrent client (dome-bt) is embedded in the mame-ao executable at
	/// build time so a release is a single file, then unpacked on first use.
	///
	/// Extraction goes to the cache directory, not the data directory: the
	/// BitTorrent directory is wiped by "DOME-BT remove", and the cache belongs
	/// to this machine rather than to user data.
	///
	/// Builds without -p:DomeBtBinary=... simply have no BitTorrent client; that
	/// is reported, not fatal, because archive.org downloads do not need it.
	/// </summary>
	internal static class DomeBtPayload
	{
		public const string ResourceName = "dome-bt";

		public const string BinaryName = "dome-bt";

		public static bool IsEmbedded
		{
			get { return typeof(DomeBtPayload).Assembly.GetManifestResourceInfo(ResourceName) != null; }
		}

		/// <summary>
		/// Unpack the client if it is not already there and export
		/// MAME_AO_DOME_BT so the core can find it. Returns the path, or null when
		/// this build has no client embedded.
		/// </summary>
		public static string EnsureExtracted(string cacheDirectory)
		{
			if (IsEmbedded == false)
				return null;

			using Stream resource = typeof(DomeBtPayload).Assembly.GetManifestResourceStream(ResourceName);

			if (resource == null)
				return null;

			string directory = Path.Combine(cacheDirectory, "bin");
			Directory.CreateDirectory(directory);

			string filename = Path.Combine(directory, BinaryName);

			FileInfo existing = new FileInfo(filename);

			if (existing.Exists == true && existing.Length == resource.Length)
			{
				Environment.SetEnvironmentVariable("MAME_AO_DOME_BT", filename);
				return filename;
			}

			string temporary = filename + ".tmp";

			using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
				resource.CopyTo(stream);

			if (OperatingSystem.IsWindows() == false)
			{
				File.SetUnixFileMode(temporary,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
					UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
					UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			}

			File.Move(temporary, filename, true);

			Environment.SetEnvironmentVariable("MAME_AO_DOME_BT", filename);

			return filename;
		}
	}
}
