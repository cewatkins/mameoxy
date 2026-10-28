using System;
using System.Collections.Generic;
using System.IO;

using Spludlow.MameAO.Platform;

namespace Spludlow.MameAO
{
	/// <summary>
	/// The on-disk layout of one MAME version, e.g.
	/// <c>$XDG_DATA_HOME/mame-ao/mame/0288/</c>.
	///
	/// The directory is self-contained: it holds the generated ini, the extracts
	/// of -listxml / -listsoftware, the SQLite databases, and every directory MAME
	/// writes to (roms, samples, snap, sta, nvram, cfg...). MAME itself stays
	/// wherever the distribution put it; we link it in only so that the running
	/// process and any tooling can see which binary belongs to which version.
	/// </summary>
	public sealed class MameCoreLayout
	{
		public string CoreDirectory { get; }

		public string IniFilename { get; }
		public string InfoFilename { get; }

		public string RomDirectory { get; }
		public string SampleDirectory { get; }
		public string ArtDirectory { get; }
		public string CtrlrDirectory { get; }
		public string FontDirectory { get; }
		public string CheatDirectory { get; }
		public string CrosshairDirectory { get; }
		public string PluginDirectory { get; }
		public string LanguageDirectory { get; }
		public string SoftwareDirectory { get; }

		public string ConfigDirectory { get; }
		public string NvramDirectory { get; }
		public string InputDirectory { get; }
		public string StateDirectory { get; }
		public string SnapshotDirectory { get; }
		public string DiffDirectory { get; }
		public string CommentDirectory { get; }
		public string ShareDirectory { get; }

		/// <summary>&lt;core&gt;/mame (or mame.exe on Windows) - a link to the real binary.</summary>
		public string BinaryLink { get; }

		/// <summary>&lt;core&gt;/hash - a link to the native hash directory, when one exists.</summary>
		public string HashLink { get; }

		public MameCoreLayout(string coreDirectory)
		{
			if (string.IsNullOrWhiteSpace(coreDirectory) == true)
				throw new ArgumentException("Core directory is required.", nameof(coreDirectory));

			CoreDirectory = Path.GetFullPath(coreDirectory);
			IniFilename = Path.Combine(CoreDirectory, "mame.ini");
			InfoFilename = Path.Combine(CoreDirectory, "ao_info.txt");
			BinaryLink = Path.Combine(CoreDirectory, OperatingSystem.IsWindows() ? "mame.exe" : "mame");
			HashLink = Path.Combine(CoreDirectory, "hash");

			RomDirectory = Path.Combine(CoreDirectory, "roms");
			SampleDirectory = Path.Combine(CoreDirectory, "samples");
			ArtDirectory = Path.Combine(CoreDirectory, "artwork");
			CtrlrDirectory = Path.Combine(CoreDirectory, "ctrlr");
			FontDirectory = Path.Combine(CoreDirectory, "font");
			CheatDirectory = Path.Combine(CoreDirectory, "cheat");
			CrosshairDirectory = Path.Combine(CoreDirectory, "crosshair");
			PluginDirectory = Path.Combine(CoreDirectory, "plugins");
			LanguageDirectory = Path.Combine(CoreDirectory, "language");
			SoftwareDirectory = Path.Combine(CoreDirectory, "software");

			ConfigDirectory = Path.Combine(CoreDirectory, "cfg");
			NvramDirectory = Path.Combine(CoreDirectory, "nvram");
			InputDirectory = Path.Combine(CoreDirectory, "inp");
			StateDirectory = Path.Combine(CoreDirectory, "sta");
			SnapshotDirectory = Path.Combine(CoreDirectory, "snap");
			DiffDirectory = Path.Combine(CoreDirectory, "diff");
			CommentDirectory = Path.Combine(CoreDirectory, "comments");
			ShareDirectory = Path.Combine(CoreDirectory, "share");
		}

		public static MameCoreLayout ForVersion(string rootDirectory, string version)
		{
			if (string.IsNullOrWhiteSpace(version) == true)
				throw new ArgumentException("MAME version is required.", nameof(version));

			return new MameCoreLayout(Path.Combine(rootDirectory, version));
		}

		public IEnumerable<string> Directories
		{
			get
			{
				yield return CoreDirectory;
				yield return RomDirectory;
				yield return SampleDirectory;
				yield return ArtDirectory;
				yield return CtrlrDirectory;
				yield return FontDirectory;
				yield return CheatDirectory;
				yield return CrosshairDirectory;
				yield return PluginDirectory;
				yield return LanguageDirectory;
				yield return SoftwareDirectory;
				yield return ConfigDirectory;
				yield return NvramDirectory;
				yield return InputDirectory;
				yield return StateDirectory;
				yield return SnapshotDirectory;
				yield return DiffDirectory;
				yield return CommentDirectory;
				yield return ShareDirectory;
			}
		}

		public void EnsureDirectories()
		{
			foreach (string directory in Directories)
				Directory.CreateDirectory(directory);
		}

		/// <summary>
		/// Prepare everything a run needs: directories, links to the native MAME,
		/// the generated ini, and an ao_info.txt recording which binary produced
		/// this version directory.
		///
		/// Returns human readable warnings (empty when everything worked). It never
		/// throws for a link failure: MAME is launched from its real path anyway, so
		/// a missing convenience link is not a reason to refuse to start.
		/// </summary>
		public IReadOnlyList<string> Prepare(MameInstall install)
		{
			if (install == null)
				throw new ArgumentNullException(nameof(install));

			EnsureDirectories();

			List<string> warnings = new List<string>();

			LinkBinary(install, warnings);
			LinkHash(install, warnings);

			MameIni.Write(this, install.HashPath);

			Dictionary<string, string> info = new Dictionary<string, string>
			{
				{ "MameVersion", install.Version },
				{ "MameVersionRaw", install.VersionRaw ?? "" },
				{ "MameProvider", install.Kind.ToString() },
				{ "MameCommand", string.Join(' ', install.Prefix ?? Array.Empty<string>()) },
				{ "MameHashPath", install.HashPath ?? "" },
				{ "MameHomePath", install.HomePath ?? "" },
				{ "ChdmanCommand", install.ChdmanCommand ?? "" },
				{ "PreparedAt", DateTime.UtcNow.ToString("o") },
			};

			Tools.SaveDictionary(InfoFilename, info);

			return warnings;
		}

		private void LinkBinary(MameInstall install, List<string> warnings)
		{
			// A Flatpak "binary" is a command prefix, not a file.
			if (install.Prefix == null || install.Prefix.Count == 0)
				return;

			string target = install.Prefix[0];
			if (File.Exists(target) == false)
				return;

			if (LinkPointsAt(BinaryLink, target) == true)
				return;

			if (TryLink(BinaryLink, target, false) == false)
				warnings.Add($"Could not link {BinaryLink} -> {target}; MAME will still run from {target}.");
		}

		private void LinkHash(MameInstall install, List<string> warnings)
		{
			string target = install.HashPath;

			if (string.IsNullOrWhiteSpace(target) == true || Directory.Exists(target) == false)
				return;

			string normalisedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
			string normalisedLink = Path.TrimEndingDirectorySeparator(HashLink);

			// A native MAME whose hash path already is inside this directory needs nothing.
			if (normalisedTarget == normalisedLink || normalisedTarget.StartsWith(normalisedLink + Path.DirectorySeparatorChar) == true)
				return;

			if (LinkPointsAt(HashLink, target) == true)
				return;

			if (TryLink(HashLink, target, true) == false)
				warnings.Add($"Could not link {HashLink} -> {target}; the generated mame.ini hashpath is still used.");
		}

		private static bool LinkPointsAt(string linkPath, string targetPath)
		{
			try
			{
				FileSystemInfo info = Directory.Exists(linkPath) ? new DirectoryInfo(linkPath) : new FileInfo(linkPath);

				if (info.Exists == false)
					return false;

				FileSystemInfo resolved = info.ResolveLinkTarget(true);
				if (resolved == null)
					return false;

				return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName)) ==
					Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool TryLink(string linkPath, string targetPath, bool targetIsDirectory)
		{
			try
			{
				if (PlatformServices.IsInitialised == true)
				{
					IFileLinker linker = PlatformServices.Current.Links;
					if (linker != null && linker.Create(linkPath, targetPath) == true)
						return true;
				}

				if (targetIsDirectory == true)
					Directory.CreateSymbolicLink(linkPath, targetPath);
				else
					File.CreateSymbolicLink(linkPath, targetPath);

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
