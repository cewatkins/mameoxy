using System;

namespace Spludlow.MameAO.Platform
{
	/// <summary>
	/// The Linux host's composition root for the core platform services.
	///
	/// Create() is deliberately I/O-free: it does not probe the desktop, create
	/// directories or touch the credential store. A headless host therefore gets
	/// a fully populated <see cref="PlatformServices"/> without any chance of a
	/// startup failure from a missing display or xdg-open.
	/// </summary>
	public static class LinuxPlatform
	{
		/// <summary>
		/// Build the Linux implementations of links, shell opening and credentials.
		/// The caller typically installs the result as
		/// <see cref="PlatformServices.Current"/>.
		/// </summary>
		public static PlatformServices Create(AppPaths paths)
		{
			if (paths == null)
				throw new ArgumentNullException(nameof(paths));

			return new PlatformServices
			{
				Links = new UnixFileLinker(paths.CacheDirectory),
				Shell = new XdgShellOpener(),
				Credentials = new EnvFileCredentialStore(paths),
			};
		}
	}
}
