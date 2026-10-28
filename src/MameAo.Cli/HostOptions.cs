using System;
using System.Collections.Generic;
using System.IO;

namespace Spludlow.MameAO.Cli
{
	/// <summary>
	/// Command line for the Linux host.
	///
	/// The original Windows program had no options at all: it used the directory
	/// it was started from for everything and opened a browser window. On Linux the
	/// data has to live in an XDG directory, MAME is installed elsewhere, and the
	/// program may well be started by systemd.
	/// </summary>
	internal sealed class HostOptions
	{
		public const string Usage = @"mame-ao - MAME front end

Usage:
  mame-ao [options]
  mame-ao [options] <operation> [key=value ...]

Options:
  --data-dir <dir>     Data directory (default: $XDG_DATA_HOME/mame-ao, then ~/.local/share/mame-ao)
  --config-dir <dir>   Config directory (default: $XDG_CONFIG_HOME/mame-ao, then ~/.config/mame-ao)
  --cache-dir <dir>    Cache directory (default: $XDG_CACHE_HOME/mame-ao, then ~/.cache/mame-ao)
  --mame-path <path>   MAME binary to use. Default: $MAME, then 'mame' on PATH, then Flatpak
  --listen <address>   Web UI address, e.g. 127.0.0.1:12380 or 0.0.0.0:8080
  --headless           No browser, force -video none -sound none when launching MAME
  --version            Print the version and exit
  --help               Print this help and exit

Environment:
  MAMEAO_DATA_DIR, MAMEAO_CONFIG_DIR, MAMEAO_CACHE_DIR
  MAME                                  MAME binary (same as --mame-path)
  IA_USERNAME, IA_PASSWORD              archive.org credentials (or the 0600 credentials file)
  MAMEAO_LISTEN                         same as --listen

The web UI binds 127.0.0.1 by default. Use --listen 0.0.0.0:12380 to expose it.
";

		public string DataDirectory { get; private set; }
		public string ConfigDirectory { get; private set; }
		public string CacheDirectory { get; private set; }
		public string MamePath { get; private set; }
		public string Listen { get; private set; }
		public bool Headless { get; private set; }
		public bool ShowVersion { get; private set; }
		public bool ShowHelp { get; private set; }

		/// <summary>key=value arguments forwarded to Operations.ProcessOperation.</summary>
		public Dictionary<string, string> OperationArguments { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public static HostOptions Parse(string[] args)
		{
			HostOptions options = new HostOptions();

			for (int index = 0; index < args.Length; ++index)
			{
				string argument = args[index];

				if (argument == "--help" || argument == "-h")
				{
					options.ShowHelp = true;
					continue;
				}

				if (argument == "--version" || argument == "-V")
				{
					options.ShowVersion = true;
					continue;
				}

				if (argument == "--headless")
				{
					options.Headless = true;
					continue;
				}

				if (TryOption(argument, "--data-dir", args, ref index, out string value))
				{
					options.DataDirectory = value;
					continue;
				}

				if (TryOption(argument, "--config-dir", args, ref index, out value))
				{
					options.ConfigDirectory = value;
					continue;
				}

				if (TryOption(argument, "--cache-dir", args, ref index, out value))
				{
					options.CacheDirectory = value;
					continue;
				}

				if (TryOption(argument, "--mame-path", args, ref index, out value))
				{
					options.MamePath = value;
					continue;
				}

				if (TryOption(argument, "--listen", args, ref index, out value))
				{
					options.Listen = value;
					continue;
				}

				if (argument.StartsWith("--", StringComparison.Ordinal) == true)
					throw new ArgumentException($"Unknown option: {argument}");

				// Operation form: either "operation=name" or a bare operation name.
				int separator = argument.IndexOf('=');

				if (separator == -1)
				{
					if (options.OperationArguments.ContainsKey("operation") == true)
						throw new ArgumentException($"Unexpected argument: {argument}");

					options.OperationArguments["operation"] = argument;
				}
				else
				{
					string key = argument.Substring(0, separator).Trim().ToLowerInvariant();
					string argumentValue = argument.Substring(separator + 1).Trim();

					if (key.Length == 0)
						throw new ArgumentException($"Bad argument: {argument}");

					options.OperationArguments[key] = argumentValue;
				}
			}

			if (options.OperationArguments.Count == 0)
				options.OperationArguments.Clear();

			return options;
		}

		private static bool TryOption(string argument, string name, string[] args, ref int index, out string value)
		{
			value = null;

			if (argument == name)
			{
				if (index + 1 >= args.Length)
					throw new ArgumentException($"{name} needs a value.");

				value = args[++index];
				return true;
			}

			string prefix = name + "=";

			if (argument.StartsWith(prefix, StringComparison.Ordinal) == true)
			{
				value = argument.Substring(prefix.Length);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Turn whatever the user typed into a canonical http://host:port/ URL.
		/// HttpListener needs an explicit host and a trailing slash.
		/// </summary>
		public static string NormaliseListen(string value)
		{
			const string Default = "http://127.0.0.1:12380/";

			if (string.IsNullOrWhiteSpace(value) == true)
			{
				value = Environment.GetEnvironmentVariable("MAMEAO_LISTEN");

				if (string.IsNullOrWhiteSpace(value) == true)
					return Default;
			}

			string text = value.Trim();

			if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
				throw new ArgumentException("The web UI is HTTP only: HttpListener cannot serve TLS without a certificate. Put a reverse proxy in front of it instead.");

			if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == false)
				text = "http://" + text;

			if (Uri.TryCreate(text, UriKind.Absolute, out Uri uri) == false)
				throw new ArgumentException($"Cannot parse --listen '{value}'. Use host:port, for example 127.0.0.1:12380.");

			string path = uri.AbsolutePath;

			if (path.EndsWith("/", StringComparison.Ordinal) == false)
				path += "/";

			return $"http://{uri.Host}:{uri.Port}{path}";
		}

		/// <summary>Best-effort description used by --version and error reports.</summary>
		public static string DescribeHost()
		{
			string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
			string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

			return $"{runtime}, linux-{architecture}";
		}
	}
}
