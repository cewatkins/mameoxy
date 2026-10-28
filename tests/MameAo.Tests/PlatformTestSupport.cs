using System;
using System.IO;

namespace MameAo.Tests
{
	/// <summary>
	/// Throw-away directory under the system temp root, removed on dispose.
	/// </summary>
	internal sealed class PlatformTempDir : IDisposable
	{
		public string Root { get; }

		public PlatformTempDir()
		{
			Root = Path.Combine(
				Path.GetTempPath(),
				"mameao-platform-tests-" + Guid.NewGuid().ToString("N"));

			Directory.CreateDirectory(Root);
		}

		public string FilePath(string name)
		{
			return Path.Combine(Root, name);
		}

		public string DirectoryPath(string name)
		{
			string path = Path.Combine(Root, name);
			Directory.CreateDirectory(path);
			return path;
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(Root, recursive: true);
			}
			catch (Exception)
			{
				// Temp cleanup must never fail a test.
			}
		}
	}
}
