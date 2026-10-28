using System;
using System.IO;

namespace Spludlow.MameAO
{
	/// <summary>
	/// Process-wide holder for the resolved native MAME. The location is resolved
	/// once (it costs two MAME invocations) and reused for the rest of the run.
	///
	/// Nothing here downloads anything: the user installs MAME with their package
	/// manager, Flatpak or from source, and this only finds it.
	/// </summary>
	public static class MameProvider
	{
		private static readonly object _Lock = new object();

		/// <summary>--mame-path / config MamePath / $MAME. Null means "search".</summary>
		public static string ExplicitPath { get; set; }

		public static MameInstall Install { get; private set; }

		public static bool IsResolved
		{
			get { lock (_Lock) return Install != null; }
		}

		public static MameInstall Get()
		{
			lock (_Lock)
			{
				if (Install != null)
					return Install;

				Install = MameLocator.Locate(ExplicitPath);
				return Install;
			}
		}

		/// <summary>Forget the resolved install (tests, .up re-exec, --mame-path change).</summary>
		public static void Reset()
		{
			lock (_Lock)
				Install = null;
		}
	}
}
