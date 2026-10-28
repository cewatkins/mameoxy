using System;
using System.Collections.Generic;
using System.IO;


namespace Spludlow.MameAO
{
	/// <summary>
	/// CHD hashing/verification via the chdman that belongs to the installed
	/// MAME (or the one on PATH). On Linux chdman is a separate distro package
	/// and may be a different version from MAME, so it is discovered rather than
	/// assumed to sit next to the binary.
	/// </summary>
	public class MameChdMan
	{
		private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);

		private readonly MameInstall _Install;

		public MameChdMan(MameInstall install)
		{
			_Install = install ?? throw new ArgumentNullException(nameof(install));
		}

		public bool IsAvailable => _Install.ChdmanPrefix != null && _Install.ChdmanPrefix.Count > 0;

		public string Command => _Install.ChdmanCommand;

		private void EnsureAvailable()
		{
			if (IsAvailable == false)
				throw new ApplicationException(
					"chdman was not found. Install the MAME tools package (for example 'mame-tools' on Debian/Ubuntu) " +
					"or point --mame-path at a MAME installation that includes chdman, so disk images can be hashed.");
		}

		public string Hash(string filename)
		{
			Dictionary<string, string> info = Info(filename);

			string sha1 = "";
			if (info.ContainsKey("SHA1") == true)
				sha1 = info["SHA1"];

			if (sha1.Length != 40)
				throw new ApplicationException($"CHD Info SHA1 not found in output: {filename}");

			if (Verify(filename) == false)
				throw new ApplicationException($"CHD Verify SHA1 bad file: {filename}");

			return sha1;
		}

		public bool Verify(string filename)
		{
			return VerifyOutput(VerifyRaw(filename));
		}

		/// <summary>
		/// Returns the combined output; a failed verify is data, not an exception,
		/// because callers use it to decide whether a file is good.
		/// </summary>
		public string VerifyRaw(string filename)
		{
			EnsureAvailable();
			return Run("verify", "-i", filename).Combined;
		}

		public bool VerifyOutput(string output)
		{
			if (output == null)
				return false;

			return output.Contains("Raw SHA1 verification successful!", StringComparison.Ordinal) == true &&
				output.Contains("Overall SHA1 verification successful!", StringComparison.Ordinal) == true;
		}

		public Dictionary<string, string> Info(string filename)
		{
			EnsureAvailable();

			ProcessResult result = Run("info", "-i", filename);
			if (result.ExitCode != 0)
				throw new ApplicationException($"chdman info failed for '{filename}' (exit {result.ExitCode}): {Trim(result.Combined)}");

			return ParseResult(result.StandardOutput);
		}

		public static Dictionary<string, string> ParseResult(string text)
		{
			Dictionary<string, string> result = new Dictionary<string, string>();

			if (text == null)
				return result;

			using (StringReader reader = new StringReader(text))
			{
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					int index = line.IndexOf(':');

					if (index == -1)
						continue;

					string key = line.Substring(0, index).Trim();
					string value = line.Substring(index + 1).Trim();

					if (result.ContainsKey(key) == true)
					{
						int count = 2;
						while (result.ContainsKey(key + count) == true)
						{
							++count;
						}
						key += count;
					}

					result.Add(key, value);
				}
			}

			return result;
		}

		private ProcessResult Run(params string[] arguments)
		{
			ProcessResult result = MameProcess.Run(_Install.ChdmanCommandLine(arguments), null, Timeout);

			if (result.TimedOut == true)
				throw new ApplicationException(
					$"chdman {string.Join(' ', arguments)} timed out after {Timeout.TotalMinutes:0} minutes.");

			return result;
		}

		private static string Trim(string text)
		{
			if (string.IsNullOrWhiteSpace(text) == true)
				return "";

			text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
			return text.Length <= 400 ? text : text.Substring(0, 400) + "…";
		}
	}
}
