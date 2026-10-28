using System.IO;

namespace Spludlow.MameAO.Platform
{
	/// <summary>
	/// Creates filesystem links (symlinks on Unix, symlinks/hardlinks on Windows).
	/// Implementations must never throw for "unsupported"; return false instead.
	/// </summary>
	public interface IFileLinker
	{
		/// <summary>True when this linker can create links in the target filesystem.</summary>
		bool IsSupported { get; }

		/// <summary>Create <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>.</summary>
		bool Create(string linkPath, string targetPath);
	}

	/// <summary>
	/// Opens a URL or file with the desktop's default handler. Must degrade to
	/// false (never throw) when headless or when no handler exists.
	/// </summary>
	public interface IShellOpener
	{
		bool TryOpen(string target);
	}

	/// <summary>
	/// Credential lookup for archive.org etc. Environment variables take
	/// precedence over the on-disk store, and values are never logged.
	/// </summary>
	public interface ICredentialStore
	{
		string Get(string name);
		bool Has(string name);
		void Set(string name, string value);
		string StorePath { get; }
	}

	/// <summary>
	/// The OS-specific services the core needs. Set once by the host at startup.
	/// </summary>
	public sealed class PlatformServices
	{
		public IFileLinker Links { get; init; }
		public IShellOpener Shell { get; init; }
		public ICredentialStore Credentials { get; init; }

		private static PlatformServices _Current;

		public static PlatformServices Current
		{
			get => _Current ?? throw new IOException("Platform services have not been initialised.");
			set => _Current = value;
		}

		public static bool IsInitialised => _Current != null;
	}
}
