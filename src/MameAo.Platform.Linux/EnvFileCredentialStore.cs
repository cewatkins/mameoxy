using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Spludlow.MameAO.Platform
{
	/// <summary>
	/// Credentials from the environment first, then an XDG-style credentials
	/// file with 0600 permissions. Values are never logged, echoed in errors or
	/// included in exception messages.
	///
	/// Lookup order for Get(name):
	///   archive.org access/secret keys: IA_ACCESS_KEY / IA_SECRET_KEY, then
	///   MAMEAO_ARCHIVE_ORG_ACCESS_KEY / MAMEAO_ARCHIVE_ORG_SECRET_KEY, then the
	///   other common aliases, then the file below.
	///   Other IA_* names: the raw variable, then MAMEAO_ARCHIVE_ORG_*, then
	///   MAMEAO_&lt;NAME&gt;.
	///   Any other name: MAMEAO_&lt;NAME&gt;, then &lt;NAME&gt; verbatim.
	///   Finally: $XDG_CONFIG_HOME/mame-ao/credentials (or the AppPaths file).
	/// </summary>
	public sealed class EnvFileCredentialStore : ICredentialStore
	{
		private static readonly object _Sync = new object();

		private static readonly string[] AccessKeyAliases =
		{
			"IA_ACCESS_KEY",
			"ARCHIVE_ORG_ACCESS_KEY",
			"ARCHIVEORG_ACCESS_KEY",
			"ARCHIVEORGACCESSKEY",
			"INTERNET_ARCHIVE_ACCESS_KEY",
			"INTERNETARCHIVE_ACCESS_KEY",
			"INTERNETARCHIVEACCESSKEY",
		};

		private static readonly string[] SecretKeyAliases =
		{
			"IA_SECRET_KEY",
			"ARCHIVE_ORG_SECRET_KEY",
			"ARCHIVEORG_SECRET_KEY",
			"ARCHIVEORGSECRETKEY",
			"INTERNET_ARCHIVE_SECRET_KEY",
			"INTERNETARCHIVE_SECRET_KEY",
			"INTERNETARCHIVESECRETKEY",
		};

		private const string AccessKeyPrefixed = "MAMEAO_ARCHIVE_ORG_ACCESS_KEY";
		private const string SecretKeyPrefixed = "MAMEAO_ARCHIVE_ORG_SECRET_KEY";

		private readonly string _StorePath;
		private readonly Func<string, string> _GetEnv;

		public EnvFileCredentialStore(string storePath, Func<string, string> getEnv = null)
		{
			if (string.IsNullOrWhiteSpace(storePath))
				throw new ArgumentException("A credential store path is required.", nameof(storePath));

			_StorePath = Path.GetFullPath(storePath);
			_GetEnv = getEnv ?? Environment.GetEnvironmentVariable;
		}

		public EnvFileCredentialStore(AppPaths paths, Func<string, string> getEnv = null)
			: this(
				(paths ?? throw new ArgumentNullException(nameof(paths))).CredentialsFile,
				getEnv)
		{
		}

		public string StorePath => _StorePath;

		public string Get(string name)
		{
			if (ValidateName(name, throwOnError: false) == false)
				return null;

			foreach (string variable in EnvironmentNames(name))
			{
				string fromEnvironment = _GetEnv(variable);
				if (fromEnvironment != null)
					return fromEnvironment;
			}

			List<KeyValuePair<string, string>> entries = ReadFile(throwOnError: false);

			foreach (string key in FileNames(name))
			{
				foreach (KeyValuePair<string, string> entry in entries)
				{
					if (string.Equals(entry.Key, key, StringComparison.Ordinal))
						return entry.Value;
				}
			}

			return null;
		}

		public bool Has(string name)
		{
			return Get(name) != null;
		}

		public void Set(string name, string value)
		{
			ValidateName(name, throwOnError: true);

			if (value == null)
				throw new ArgumentNullException(nameof(value));
			if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
				throw new ArgumentException("Credential values may not contain line breaks.", nameof(value));

			string directory = Path.GetDirectoryName(_StorePath);
			string temporary = null;

			lock (_Sync)
			{
				try
				{
					if (string.IsNullOrEmpty(directory) == false)
						Directory.CreateDirectory(directory);

					// Tighten rather than fail if the file was created loosely.
					TightenFile(_StorePath);

					List<string> lines = ReadLinesForUpdate();
					lines = ReplaceOrAppend(lines, name, value);

					string leaf = Path.GetFileName(_StorePath);
					temporary = Path.Combine(
						directory ?? ".",
						"." + leaf + "." + Guid.NewGuid().ToString("N") + ".tmp");

					WriteTemporary(temporary, lines);

					File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
					File.Move(temporary, _StorePath, overwrite: true);
					temporary = null;

					// SetUnixFileMode before the move keeps the final file 0600 even
					// when replacing an existing entry; re-assert for exotic umasks.
					TightenFile(_StorePath);
				}
				catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
				{
					throw new IOException("Could not write the credential store.", exception);
				}
				finally
				{
					if (temporary != null)
					{
						try
						{
							File.Delete(temporary);
						}
						catch (Exception)
						{
						}
					}
				}
			}
		}

		private static bool ValidateName(string name, bool throwOnError)
		{
			bool valid = string.IsNullOrWhiteSpace(name) == false
				&& name.IndexOf('=') < 0
				&& name.IndexOf('\n') < 0
				&& name.IndexOf('\r') < 0
				&& name.TrimStart().StartsWith("#", StringComparison.Ordinal) == false;

			if (valid == false && throwOnError)
				throw new ArgumentException("Credential name is not valid.", nameof(name));

			return valid;
		}

		private static string[] AliasesFor(string name, out string prefixed)
		{
			prefixed = null;
			string normalized = NormalizeName(name);

			if (MatchesAlias(AccessKeyAliases, normalized, AccessKeyPrefixed) == true)
			{
				prefixed = AccessKeyPrefixed;
				return AccessKeyAliases;
			}

			if (MatchesAlias(SecretKeyAliases, normalized, SecretKeyPrefixed) == true)
			{
				prefixed = SecretKeyPrefixed;
				return SecretKeyAliases;
			}

			return null;
		}

		private static bool MatchesAlias(string[] aliases, string normalized, string prefixed)
		{
			if (string.Equals(normalized, NormalizeName(prefixed), StringComparison.Ordinal) == true)
				return true;

			foreach (string alias in aliases)
			{
				if (string.Equals(normalized, alias, StringComparison.Ordinal) == true)
					return true;
				if (string.Equals(normalized, "MAMEAO_" + alias, StringComparison.Ordinal) == true)
					return true;
			}

			return false;
		}

		private static List<string> EnvironmentNames(string name)
		{
			List<string> candidates = new List<string>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

			void Add(string value)
			{
				if (string.IsNullOrEmpty(value) == false && seen.Add(value) == true)
					candidates.Add(value);
			}

			string[] aliases = AliasesFor(name, out string prefixed);

			if (aliases != null)
			{
				// Standard internetarchive names win, then the MAME-AO prefixed
				// archive.org spelling, then the remaining common aliases.
				Add(aliases[0]);
				Add(prefixed);
				Add(name);
				Add(name.Trim().ToUpperInvariant());

				foreach (string alias in aliases)
					Add(alias);

				Add(EnvironmentName(name));

				foreach (string alias in aliases)
					Add("MAMEAO_" + alias);
			}
			else if (name.Trim().StartsWith("IA_", StringComparison.OrdinalIgnoreCase) == true)
			{
				Add(name);
				Add(name.Trim().ToUpperInvariant());
				Add("MAMEAO_ARCHIVE_ORG_" + name.Trim().Substring(3).ToUpperInvariant());
				Add(EnvironmentName(name));
			}
			else
			{
				// Existing generic convention: MAMEAO_&lt;NAME&gt; before &lt;NAME&gt;.
				Add(EnvironmentName(name));
				Add(name);
			}

			return candidates;
		}

		private static List<string> FileNames(string name)
		{
			List<string> keys = new List<string>();
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

			void Add(string value)
			{
				if (string.IsNullOrEmpty(value) == false && seen.Add(value) == true)
					keys.Add(value);
			}

			Add(name);

			string[] aliases = AliasesFor(name, out string prefixed);

			if (aliases != null)
			{
				Add(prefixed);
				Add(NormalizeName(name));

				foreach (string alias in aliases)
					Add(alias);

				Add(EnvironmentName(name));

				foreach (string alias in aliases)
					Add("MAMEAO_" + alias);
			}
			else if (name.Trim().StartsWith("IA_", StringComparison.OrdinalIgnoreCase) == true)
			{
				Add(name.Trim().ToUpperInvariant());
				Add("MAMEAO_ARCHIVE_ORG_" + name.Trim().Substring(3).ToUpperInvariant());
				Add(EnvironmentName(name));
			}

			return keys;
		}

		private static string EnvironmentName(string name)
		{
			return "MAMEAO_" + NormalizeName(name);
		}

		private static string NormalizeName(string name)
		{
			if (string.IsNullOrWhiteSpace(name) == true)
				return string.Empty;

			StringBuilder builder = new StringBuilder(name.Length);

			foreach (char character in name.Trim())
			{
				if ((character >= 'a' && character <= 'z')
					|| (character >= 'A' && character <= 'Z')
					|| (character >= '0' && character <= '9'))
				{
					builder.Append(char.ToUpperInvariant(character));
				}
				else
				{
					builder.Append('_');
				}
			}

			return builder.ToString();
		}

		private List<KeyValuePair<string, string>> ReadFile(bool throwOnError)
		{
			List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();

			if (File.Exists(_StorePath) == false)
				return entries;

			string[] lines;
			try
			{
				lines = File.ReadAllLines(_StorePath);
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				if (throwOnError)
					throw new IOException("Could not read the credential store.", exception);
				return entries;
			}

			foreach (string line in lines)
			{
				if (TryParseLine(line, out string key, out string value))
					entries.Add(new KeyValuePair<string, string>(key, value));
			}

			return entries;
		}

		private List<string> ReadLinesForUpdate()
		{
			List<string> lines = new List<string>();

			if (File.Exists(_StorePath) == false)
				return lines;

			try
			{
				lines.AddRange(File.ReadAllLines(_StorePath));
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				throw new IOException("Could not read the credential store.", exception);
			}

			return lines;
		}

		private static bool TryParseLine(string line, out string key, out string value)
		{
			key = null;
			value = null;

			if (line == null)
				return false;

			string trimmed = line.TrimEnd('\r').Trim();
			if (trimmed.Length == 0 || trimmed[0] == '#')
				return false;

			int separator = trimmed.IndexOf('=');
			if (separator <= 0)
				return false;

			key = trimmed.Substring(0, separator).Trim();
			value = trimmed.Substring(separator + 1);

			return key.Length != 0;
		}

		private static List<string> ReplaceOrAppend(List<string> lines, string name, string value)
		{
			string replacement = name + "=" + value;
			bool replaced = false;

			List<string> result = new List<string>(lines.Count + 1);

			foreach (string raw in lines)
			{
				string line = raw == null ? string.Empty : raw.TrimEnd('\r');

				if (TryParseLine(line, out string key, out _)
					&& string.Equals(key, name, StringComparison.Ordinal))
				{
					if (replaced == false)
					{
						result.Add(replacement);
						replaced = true;
					}
					continue;
				}

				result.Add(line);
			}

			if (replaced == false)
				result.Add(replacement);

			return result;
		}

		private static void WriteTemporary(string path, List<string> lines)
		{
			// CreateNew: never reuse a stale temp file. 0600 is applied by the
			// caller before the atomic rename.
			using (FileStream stream = new FileStream(
				path,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.WriteThrough))
			{
				using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
				{
					foreach (string line in lines)
						writer.Write(line + "\n");

					writer.Flush();
					stream.Flush(flushToDisk: true);
				}
			}
		}

		private static void TightenFile(string path)
		{
			try
			{
				if (File.Exists(path))
					File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
			catch (Exception)
			{
				// Non-POSIX filesystem or insufficient privileges: not fatal.
			}
		}
	}
}
