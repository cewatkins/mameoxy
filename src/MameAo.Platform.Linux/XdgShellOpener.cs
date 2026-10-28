using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Spludlow.MameAO.Platform
{
	/// <summary>
	/// Opens URLs and files with xdg-open, but only when a graphical session is
	/// present. Headless hosts get false rather than an exception. When the
	/// target cannot be opened it is printed to stdout so the user can copy it.
	/// Nothing is ever run through a shell and no argument is re-parsed.
	/// </summary>
	public sealed class XdgShellOpener : IShellOpener
	{
		private readonly Func<string, string> _Which;
		private readonly Func<bool> _HasGraphicalSession;

		public XdgShellOpener()
			: this(LinuxEnvironment.Which, LinuxEnvironment.HasGraphicalSession)
		{
		}

		public XdgShellOpener(Func<string, string> which, Func<bool> hasGraphicalSession)
		{
			_Which = which ?? throw new ArgumentNullException(nameof(which));
			_HasGraphicalSession = hasGraphicalSession ?? throw new ArgumentNullException(nameof(hasGraphicalSession));
		}

		public bool TryOpen(string target)
		{
			if (string.IsNullOrWhiteSpace(target))
				return false;

			try
			{
				if (_HasGraphicalSession() == false)
				{
					LogUnopened(target);
					return false;
				}

				string xdgOpen = _Which("xdg-open");
				if (string.IsNullOrWhiteSpace(xdgOpen))
				{
					LogUnopened(target);
					return false;
				}

				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = xdgOpen,
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};
				// ArgumentList, not a command line: the target is never re-parsed
				// by a shell and may contain spaces or shell metacharacters.
				startInfo.ArgumentList.Add(target);

				Process process = Process.Start(startInfo);
				if (process == null)
				{
					LogUnopened(target);
					return false;
				}

				// Drain the pipes so a chatty xdg-open cannot block on a full
				// buffer, and do not wait for it: it may outlive this call.
				_ = DrainAsync(process.StandardOutput);
				_ = DrainAsync(process.StandardError);
				process.StandardInput.Close();

				// xdg-open normally exits immediately. Catch the common "no
				// handler" failure (non-zero exit) without blocking on an opener
				// that stays around for the lifetime of the browser.
				bool exited;
				try
				{
					exited = process.WaitForExit(500);
				}
				catch (Exception)
				{
					return true;
				}

				if (exited == true)
				{
					int exitCode;
					try
					{
						exitCode = process.ExitCode;
					}
					catch (Exception)
					{
						exitCode = 0;
					}

					if (exitCode != 0)
					{
						LogUnopened(target);
						return false;
					}
				}

				return true;
			}
			catch (Exception)
			{
				LogUnopened(target);
				return false;
			}
		}

		private static void LogUnopened(string target)
		{
			if (string.IsNullOrWhiteSpace(target) == true)
				return;

			try
			{
				Console.WriteLine(target);
			}
			catch (Exception)
			{
				// stdout can be closed in a daemon; a failed log is not fatal.
			}
		}

		private static async Task DrainAsync(System.IO.StreamReader reader)
		{
			try
			{
				char[] buffer = new char[1024];
				while (true)
				{
					int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
					if (read <= 0)
						return;
				}
			}
			catch (Exception)
			{
				// The child exited or the pipe was closed; nothing to report.
			}
			finally
			{
				try
				{
					reader.Dispose();
				}
				catch (Exception)
				{
				}
			}
		}
	}
}
