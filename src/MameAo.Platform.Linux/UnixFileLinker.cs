using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Spludlow.MameAO.Platform
{
	/// <summary>
	/// Unix implementation of <see cref="IFileLinker"/>. Symlinks first; hard
	/// links are offered as a best-effort fallback for callers that need them.
	/// Nothing here throws: every failure is reported as false.
	/// </summary>
	public sealed class UnixFileLinker : IFileLinker
	{
		private const string ProbePrefix = "mameao-link-probe-";

		// .NET has no File.CreateHardLink; link(2) is the only portable Unix way.
		[DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
		private static extern int link(string existingPath, string newPath);

		private readonly string _ProbeRoot;
		private readonly Lazy<bool> _Supported;

		/// <param name="probeRoot">
		/// Directory used for the one-off symlink capability probe. Defaults to
		/// the system temp directory; tests can point it somewhere private.
		/// </param>
		public UnixFileLinker(string probeRoot = null)
		{
			_ProbeRoot = string.IsNullOrWhiteSpace(probeRoot) ? Path.GetTempPath() : probeRoot;
			_Supported = new Lazy<bool>(Probe, isThreadSafe: true);
		}

		public bool IsSupported => _Supported.Value;

		public bool Create(string linkPath, string targetPath)
		{
			if (IsSupported == false)
				return false;

			return CreateLink(linkPath, targetPath, hardLink: false);
		}

		/// <summary>
		/// Best-effort hard link. Returns false (never throws) when the
		/// filesystem, platform or permissions do not allow it.
		/// </summary>
		public bool CreateHardLink(string linkPath, string targetPath)
		{
			return CreateLink(linkPath, targetPath, hardLink: true);
		}

		private bool CreateLink(string linkPath, string targetPath, bool hardLink)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath))
					return false;

				string link = Path.GetFullPath(linkPath);
				string target = Path.GetFullPath(targetPath);

				if (string.Equals(link, target, StringComparison.Ordinal))
					return false;

				if (PrepareLinkPath(link) == false)
					return false;

				try
				{
					if (hardLink)
						CreateHardLinkNative(link, target);
					else
						File.CreateSymbolicLink(link, target);

					if (LinkExists(link, hardLink))
						return true;

					// Half-created: remove whatever is there so we do not leave debris.
					TryDeleteLink(link);
					return false;
				}
				catch (Exception)
				{
					TryDeleteLink(link);
					return false;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static void CreateHardLinkNative(string linkPath, string targetPath)
		{
			if (OperatingSystem.IsLinux() == false && OperatingSystem.IsFreeBSD() == false && OperatingSystem.IsMacOS() == false)
				throw new PlatformNotSupportedException("Hard links are not supported on this platform.");

			// link(2): the existing path comes first, the new name second.
			if (link(targetPath, linkPath) != 0)
			{
				int error = Marshal.GetLastPInvokeError();
				throw new IOException("Could not create hard link (errno " + error + ").");
			}
		}

		/// <summary>
		/// Make <paramref name="linkPath"/> available for creation. Only an
		/// existing symlink may be removed; a real file or directory blocks us.
		/// </summary>
		private static bool PrepareLinkPath(string linkPath)
		{
			try
			{
				FileSystemInfo entry = new FileInfo(linkPath);

				if (entry.LinkTarget != null)
				{
					if (TryDeleteLink(linkPath) == false)
						return false;
					// Fall through and let the create call verify the path is gone.
				}
				else if (entry.Exists || Directory.Exists(linkPath))
				{
					return false;
				}

				// A dangling symlink is still a link entry even though Exists is false.
				if (new FileInfo(linkPath).LinkTarget != null)
					return false;

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool LinkExists(string linkPath, bool hardLink)
		{
			try
			{
				if (hardLink)
					return File.Exists(linkPath) || Directory.Exists(linkPath);

				FileSystemInfo entry = new FileInfo(linkPath);
				return entry.LinkTarget != null || entry.Exists || Directory.Exists(linkPath);
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool TryDeleteLink(string linkPath)
		{
			try
			{
				File.Delete(linkPath);
				return true;
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (IOException)
			{
			}

			// File.Delete refuses a link that resolves to a directory on some
			// runtimes; Directory.Delete unlinks the link itself in that case.
			try
			{
				Directory.Delete(linkPath, recursive: false);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private bool Probe()
		{
			string directory = null;

			try
			{
				Directory.CreateDirectory(_ProbeRoot);

				directory = Path.Combine(_ProbeRoot, ProbePrefix + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(directory);

				string target = Path.Combine(directory, "target");
				string link = Path.Combine(directory, "link");

				File.WriteAllText(target, string.Empty);
				File.CreateSymbolicLink(link, target);

				return new FileInfo(link).LinkTarget != null;
			}
			catch (Exception)
			{
				return false;
			}
			finally
			{
				if (directory != null)
				{
					try
					{
						Directory.Delete(directory, recursive: true);
					}
					catch (Exception)
					{
						// Best effort; the probe directory lives under temp anyway.
					}
				}
			}
		}
	}
}
