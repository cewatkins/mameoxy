using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace dome_bt
{
	public class Tools
	{
		private static readonly object _LogLock = new object();

		//
		// Plain line oriented logging. Nothing here may depend on stdout being a
		// terminal: under systemd stdout is a pipe (or /dev/null) and anything using
		// Console.WindowWidth, Console.SetCursorPosition or Console.ReadKey throws.
		//
		public static void Log(string message)
		{
			lock (_LogLock)
			{
				try
				{
					Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{message}");
				}
				catch (IOException)
				{
					// stdout went away (systemd restart, closed pipe), keep running.
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}

		public static void Heading(string line)
		{
			Log($"== {line} ==");
		}

		public static void ReportError(Exception e, string title)
		{
			Log($"!!! {title}: {e.Message}");
			Log(e.ToString());
			Log("Report issues here https://github.com/sam-ludlow/mame-ao/issues");
		}

		public static string FetchCached(string url)
		{
			string filename = Path.Combine(Globals.DirectoryCache, Tools.ValidFileName(Tools.UrlFileName(url)));

			string result = null;

			if (File.Exists(filename) == false || (DateTime.Now - File.GetLastWriteTime(filename) > TimeSpan.FromHours(3)))
			{
				try
				{
					Log($"Downloading {url} ...");
					result = Fetch(url);
					Log($"...done {url}");
				}
				catch (TaskCanceledException e)
				{
					Log($"ERROR Fetch client timeout: {url} {e.Message}");
				}
				catch (HttpRequestException e)
				{
					Log($"ERROR Fetch request: {url} {e.Message} {e.InnerException?.Message}");
				}
				catch (Exception e)
				{
					Log($"ERROR Fetch: {url} {e.Message} {e.InnerException?.Message}");
				}

				if (result != null)
					File.WriteAllText(filename, result, Encoding.UTF8);
			}

			if (result == null && File.Exists(filename) == true)
				result = File.ReadAllText(filename, Encoding.UTF8);

			return result;
		}

		private static string UrlFileName(string url)
		{
			int index = url.IndexOf("://");
			return index == -1 ? url : url.Substring(index + 3);
		}

		public static string Fetch(string url)
		{
			try
			{
				using (HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
				{
					HttpResponseMessage responseMessage = Globals.HttpClient.SendAsync(requestMessage).GetAwaiter().GetResult();

					responseMessage.EnsureSuccessStatusCode();

					return responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				}
			}
			catch (AggregateException e)
			{
				throw e.InnerException ?? e;
			}
		}

		public static void CompressSingleFile(string filename, string zipFilename)
		{
			using (var fileStream = new FileStream(zipFilename, FileMode.Create))
				using (var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Create))
					zipArchive.CreateEntryFromFile(filename, Path.GetFileName(filename));
		}

		private static readonly List<char> _InvalidFileNameChars = new List<char>(Path.GetInvalidFileNameChars());
		public static string ValidFileName(string name)
		{
			return ValidName(name, _InvalidFileNameChars, "_");
		}
		public static string ValidName(string name, List<char> invalidChars, string replaceBadWith)
		{
			StringBuilder sb = new StringBuilder();

			foreach (char c in name)
			{
				if (invalidChars.Contains(c) == true)
					sb.Append(replaceBadWith);
				else
					sb.Append(c);
			}

			return sb.ToString();
		}

		private static string[] _SystemOfUnits =
		{
			"Bytes",
			"Kilobytes (KB)",
			"Megabytes (MB)",
			"Gigabytes (GB)",
			"Terabytes (TB)",
			"Petabytes (PB)",
			"Exabytes (EB)"
		};
		public static string DataSizeText(long sizeBytes)
		{
			return DataSizeText((ulong)sizeBytes);
		}
		public static string DataSizeText(ulong sizeBytes)
		{
			for (int index = 0; index < _SystemOfUnits.Length; ++index)
			{
				ulong nextUnit = (ulong)Math.Pow(2, (index + 1) * 10);

				if (sizeBytes < nextUnit || nextUnit == 0 || index == (_SystemOfUnits.Length - 1))
				{
					ulong unit = (ulong)Math.Pow(2, index * 10);
					decimal result = (decimal)sizeBytes / (decimal)unit;
					int decimalPlaces = 0;
					if (result <= 9.9M)
						decimalPlaces = 1;
					result = Math.Round(result, decimalPlaces);
					return result.ToString() + " " + _SystemOfUnits[index];
				}
			}

			throw new ApplicationException($"Failed to find Data Size {sizeBytes}");
		}

		public static string TimeTookText(TimeSpan span)
		{
			StringBuilder text = new StringBuilder();

			if (((int)span.TotalDays) > 0)
			{
				text.Append((int)span.TotalDays);
				text.Append("d");
				if (span.Hours > 0)
				{
					text.Append(" ");
					text.Append(span.Hours);
					text.Append("h");
				}
				if (span.Minutes > 0)
				{
					text.Append(" ");
					text.Append(span.Minutes);
					text.Append("m");
				}

				return text.ToString();
			}

			if (((int)span.TotalHours) > 0)
			{
				text.Append(span.Hours);
				text.Append("h");
				if (span.Minutes > 0)
				{
					text.Append(" ");
					text.Append(span.Minutes);
					text.Append("m");
				}

				return text.ToString();
			}

			if (((int)span.TotalMinutes) > 0)
			{
				text.Append(span.Minutes);
				text.Append("m");
				if (span.Seconds > 0)
				{
					text.Append(" ");
					text.Append(span.Seconds);
					text.Append("s");
				}

				return text.ToString();
			}

			if (((int)span.TotalSeconds) > 0)
			{
				text.Append(span.Seconds);
				text.Append("s");
				if (span.Milliseconds > 0)
				{
					text.Append(" ");
					text.Append(span.Milliseconds);
					text.Append("ms");
				}

				return text.ToString();
			}

			text.Append(span.Milliseconds);
			text.Append("ms");

			return text.ToString();
		}
	}
}
