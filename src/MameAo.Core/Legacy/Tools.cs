using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Spludlow.MameAO.Platform;

namespace Spludlow.MameAO
{
	public class Tools
	{
		[DllImport("kernel32.dll")]
		public static extern IntPtr GetConsoleWindow();

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(IntPtr hWnd);

		private static readonly string[] _SystemOfUnits =
		{
			"Bytes (B)",
			"Kilobytes (KiB)",
			"Megabytes (MiB)",
			"Gigabytes (GiB)",
			"Terabytes (TiB)",
			"Petabytes (PiB)",
			"Exabytes (EiB)"
		};

		private static readonly char[] _HeadingChars = new char[] { ' ', '#', '=', '-' };

		[ThreadStatic]
		private static SHA1Managed _SHA1Managed = null;

		private static readonly uint[] _CRC32_Table;

		static Tools()
		{
			_CRC32_Table = new uint[256];
			for (uint index = 0; index < _CRC32_Table.Length; index++)
			{
				uint crc = index;
				for (int bit = 0; bit < 8; bit++)
					crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;

				_CRC32_Table[index] = crc;
			}

		}

		public static Dictionary<string, string> LoadDictionary(string filename)
		{
			var dictionary = new Dictionary<string, string>();

			if (File.Exists(filename) == true)
			{
				using (StreamReader reader = new StreamReader(filename, Encoding.UTF8))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						line = line.Trim();
						if (line.Length == 0)
							continue;

						int index = line.IndexOf('\t');
						if (index == -1)
							throw new ApplicationException($"Bad line in Dictionary file {filename}\t{line}");

						dictionary.Add(line.Substring(0, index), line.Substring(index + 1));
					}
				}
			}

			return dictionary;
		}

		public static void SaveDictionary(string filename, Dictionary<string, string> dictionary)
		{
			string[] lines = dictionary.Select(pair =>  $"{pair.Key}\t{pair.Value}").ToArray();

			File.WriteAllLines(filename, lines, Encoding.UTF8);
		}

		public static string DataRowValue(DataRow row, string columnName)
		{
			if (row.IsNull(columnName))
				return null;
			return (string)row[columnName];
		}

		public static DataTable DataTableFromView(DataView view, string tableName)
		{
			DataTable table = view.Table.Clone();
			table.TableName = tableName;
			foreach (DataRowView rowView in view)
				table.ImportRow(rowView.Row);
			return table;
		}

		public static void SetDataTableStringLengths(DataTable table)
		{
			foreach (DataColumn column in table.Columns)
			{
				if (column.DataType != typeof(string))
					continue;

				int max = 1;
				foreach (DataRow row in table.Rows)
				{
					if (row.IsNull(column) == false)
					{
						int len = ((string)row[column]).Length;
						if (len > max)
							max = len;
					}
				}
				column.MaxLength = max;
			}
		}

		public static void ConsoleToFront()
		{
			if (OperatingSystem.IsWindows() == false)
				return;

			if (Globals.AO != null && Globals.AO.ConsoleHandle != IntPtr.Zero)
				SetForegroundWindow(Globals.AO.ConsoleHandle);
		}

		/// <summary>
		/// The Win32 console handle is used to size headings and to bring the
		/// window forward. Off Windows, and when there is no terminal, there is
		/// nothing to do.
		/// </summary>
		public static IntPtr GetConsoleWindowSafe()
		{
			if (OperatingSystem.IsWindows() == false)
				return IntPtr.Zero;

			try { return GetConsoleWindow(); }
			catch (Exception) { return IntPtr.Zero; }
		}

		public static void ConsoleRule(int head)
		{
			Console.WriteLine(new String(_HeadingChars[head], GetWindowWidth() - 1));
		}

		public static void ConsoleHeading(int head, string line)
		{
			ConsoleHeading(head, new string[] { line });
		}
		public static void ConsoleHeading(int head, string[] lines)
		{
			ConsoleRule(head);

			char ch = _HeadingChars[head];

			foreach (string line in lines)
			{
				int pad = GetWindowWidth() - 3 - line.Length;
				if (pad < 1)
					pad = 1;
				int odd = pad % 2;
				pad /= 2;

				Console.Write(ch);
				Console.Write(new String(' ', pad));
				Console.Write(line);
				Console.Write(new String(' ', pad + odd));
				Console.Write(ch);
				Console.WriteLine();
			}

			ConsoleRule(head);
		}

		private static int GetWindowWidth()
		{
			if (Globals.AO == null || Globals.AO.ConsoleHandle == IntPtr.Zero)
				return 120;

			try { return Math.Max(Console.WindowWidth, 40); }
			catch (Exception) { return 120; }
		}

		public static void ReportError(Exception e, string title, bool fatal)
		{
			Console.WriteLine();
			Console.WriteLine($"!!! {title}: " + e.Message);
			Console.WriteLine();
			Console.WriteLine(e.ToString());
			Console.WriteLine();
			Console.WriteLine("If you want to submit an error report please copy and paste the text from here.");
			Console.WriteLine("Select All (Ctrl+A) -> Copy (Ctrl+C) -> notepad -> paste (Ctrl+V)");
			Console.WriteLine();
			Console.WriteLine("Report issues here https://github.com/sam-ludlow/mame-ao/issues");

			if (fatal == true)
			{
				Console.WriteLine();
				if (Console.IsInputRedirected == false && Console.IsOutputRedirected == false)
				{
					Console.WriteLine("Press any key to continue, program has crashed and will exit.");
					try { Console.ReadKey(); } catch (Exception) { }
				}
				Environment.Exit(1);
			}
		}

		public static void ConsolePrintMemory()
		{
			Console.WriteLine($"Memory Usage: {Tools.DataSize(Process.GetCurrentProcess().PrivateMemorySize64)}");
		}

		public static string CleanWhiteSpace(string text)
		{
			return Regex.Replace(text, @"\s+", " ").Trim();
		}

		public static void CleanDynamic(dynamic data)
		{
			List<string> deleteList = new List<string>();
			foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(data))
			{
				if (descriptor.GetValue(data) == null)
					deleteList.Add(descriptor.Name);
			}

			foreach (string key in deleteList)
				((JObject)data).Remove(key);
		}

		public static void PopText(DataSet dataSet)
		{
			StringBuilder text = new StringBuilder();

			foreach (DataTable table in dataSet.Tables)
			{
				string hr = new string('-', table.TableName.Length);
				text.AppendLine(hr);
				text.AppendLine(table.TableName);
				text.AppendLine(hr);
				text.AppendLine(TextTable(table));
				text.AppendLine();
			}

			PopText(text.ToString());
		}
		public static void PopText(DataTable table)
		{
			PopText(TextTable(table));
		}
		public static void PopText(IEnumerable<string> lines)
		{
			StringBuilder text = new StringBuilder();
			foreach (string line in lines)
				text.AppendLine(line);
			PopText(text.ToString());
		}
		/// <summary>Read a line from the terminal without echoing it.</summary>
		public static string ReadPassword()
		{
			if (Console.IsInputRedirected == true)
				return Console.ReadLine();

			StringBuilder text = new StringBuilder();

			while (true)
			{
				ConsoleKeyInfo key = Console.ReadKey(intercept: true);

				if (key.Key == ConsoleKey.Enter)
				{
					Console.WriteLine();
					break;
				}

				if (key.Key == ConsoleKey.Backspace)
				{
					if (text.Length > 0)
						text.Length -= 1;
					continue;
				}

				if (char.IsControl(key.KeyChar) == false)
					text.Append(key.KeyChar);
			}

			return text.ToString();
		}

		/// <summary>Open a URL with the desktop handler; print it when there is none.</summary>
		public static void OpenUrl(string url)
		{
			IShellOpener shell = PlatformServices.IsInitialised ? PlatformServices.Current.Shell : null;

			if (shell != null && shell.TryOpen(url) == true)
				return;

			Console.WriteLine($"Open this in a browser: {url}");
		}

		public static void PopText(string text)
		{
			string filename = Path.Combine(Globals.TempDirectory ?? Path.GetTempPath(), $"mame-ao-report-{Guid.NewGuid():N}.txt");
			File.WriteAllText(filename, text, Encoding.UTF8);

			IShellOpener shell = PlatformServices.IsInitialised ? PlatformServices.Current.Shell : null;

			if (shell != null && shell.TryOpen(filename) == true)
				return;

			// Headless: the text goes to the terminal and the file stays behind for
			// whoever wants it.
			Console.WriteLine(text);
			Console.WriteLine($"(!) Could not open a viewer for {filename}");
		}

		private readonly static HashSet<string> yearFixMatch = new HashSet<string>(new string[] {
			"?", "??", "???", "0", "00", "000", "0000"
		});
		public static int ParseFixYear(string year)
		{
			string yearFix = year;

			if (yearFix.Length > 4)
				yearFix = yearFix.Substring(0, 4);

			yearFix = yearFix.ToUpper().Replace("X", "?");

			if (yearFixMatch.Contains(yearFix))
				yearFix = "????";

			if (yearFix == "20??")
				yearFix = "2005";

			if (yearFix[3] == '?')
				yearFix = yearFix.Substring(0, 3) + "5";

			if (yearFix.Contains("?") == true)
				yearFix = "1985";

			int year_fixed;
			if (Int32.TryParse(yearFix, out year_fixed) == false)
				throw new ApplicationException($"Bad year:\t{year}\t{yearFix}");

			return year_fixed;
		}

		public static int ParseFixYearFbNeo(string year)
		{
			string yearFix = year;

			if (yearFix.Contains("+") == true)
				yearFix = yearFix.Substring(0, 4);

			if (yearFix.Contains("-") == true)
			{
				string[] parts = yearFix.Split('-');

				if (parts[1].Length == 2)
					yearFix = parts[0].Substring(0, 2) + parts[1];
				else
					yearFix = parts[1];
			}

			yearFix = Regex.Replace(yearFix, @"\D", "?");

			if (yearFix.Length > 4)
				yearFix = yearFix.Substring(0, 4);

			if (yearFix.Length != 4)
				yearFix = "????";

			return ParseFixYear(yearFix);
		}

		public static long ParseMameSize(DataRow row)
		{
			string size = row.Field<string>("size") ?? "0";
			return size.StartsWith("0x") == true ? Int64.Parse(size.Substring(2), NumberStyles.HexNumber) : Int64.Parse(size);
		}

		public static string TextTable(DataTable table)
		{
			StringBuilder result = new StringBuilder();

			foreach (DataColumn column in table.Columns)
			{
				if (column.Ordinal != 0)
					result.Append('\t');

				result.Append(column.ColumnName);
			}
			result.AppendLine();

			foreach (DataColumn column in table.Columns)
			{
				if (column.Ordinal != 0)
					result.Append('\t');

				result.Append(column.DataType.Name);

				if (table.PrimaryKey.Contains(column) == true)
					result.Append("*");
			}
			result.AppendLine();

			foreach (DataRow row in table.Rows)
			{
				foreach (DataColumn column in table.Columns)
				{
					if (column.Ordinal != 0)
						result.Append('\t');

					object value = row[column];

					if (value != null)
						result.Append(Convert.ToString(value));
				}
				result.AppendLine();
			}

			return result.ToString();
		}

		public static DataTable TextTableReadFile(string filename)
		{
			using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read))
				return TextTableReadStream(stream);
		}
		public static DataTable TextTableReadStream(Stream stream)
		{
			DataTable table = null;

			using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
			{
				string[] columnNames = null;

				string line;
				while ((line = reader.ReadLine()) != null)
				{
					string[] words = line.Split('\t');

					if (table == null)
					{
						if (columnNames == null)
						{
							columnNames = words;
						}
						else
						{
							table = new DataTable();
							List<DataColumn> primaryKeyColumns = new List<DataColumn>();

							for (int index = 0; index < columnNames.Length; ++index)
							{
								string name = columnNames[index];
								string type = words[index];
								bool pk = false;
								if (type.EndsWith("*") == true)
								{
									type = type.Substring(0, type.Length - 1);
									pk = true;
								}
								DataColumn column = new DataColumn(name, Type.GetType($"System.{type}", true));
								table.Columns.Add(column);
								if (pk == true)
									primaryKeyColumns.Add(column);
							}

							if (primaryKeyColumns.Count > 0)
								table.PrimaryKey = primaryKeyColumns.ToArray();
						}
					}
					else
					{
						DataRow row = table.NewRow();

						for (int index = 0; index < table.Columns.Count; ++index)
							row[index] = index < words.Length ? words[index] : "";

						table.Rows.Add(row);
					}
				}
			}

			return table;
		}

		public static string ValidFileName(string name)
		{
			return ValidName(name, _InvalidFileNameChars, "_");
		}
		private static readonly List<char> _InvalidFileNameChars = new List<char>(Path.GetInvalidFileNameChars());

		private static string ValidName(string name, List<char> invalidChars, string replaceBadWith)
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

		public static bool IsASCII(string text)
		{
			foreach (char c in text)
			{
				if (c > 127)
					return false;
			}
			return true;
		}

		public static string SHA1HexFile(string filename)
		{
			using (FileStream stream = File.OpenRead(filename))
				return SHA1Hex(stream);
		}

		public static string SHA1HexText(string text)
		{
			return SHA1HexText(text, Encoding.UTF8);
		}

		public static string SHA1HexText(string text, Encoding encoding)
		{
			using (MemoryStream stream = new MemoryStream())
			{
				using (StreamWriter writer = new StreamWriter(stream, encoding, 4096, true))
					writer.Write(text);

				stream.Position = 0;
				return SHA1Hex(stream);
			}
		}

		public static string SHA1Hex(byte[] data)
		{
			if (_SHA1Managed == null)
				_SHA1Managed = new SHA1Managed();

			byte[] hash = _SHA1Managed.ComputeHash(data);
			StringBuilder hex = new StringBuilder();
			foreach (byte b in hash)
				hex.Append(b.ToString("x2"));
			return hex.ToString();
		}

		public static string SHA1Hex(Stream stream)
		{
			if (_SHA1Managed == null)
				_SHA1Managed = new SHA1Managed();

			byte[] hash = _SHA1Managed.ComputeHash(stream);
			StringBuilder hex = new StringBuilder();
			foreach (byte b in hash)
				hex.Append(b.ToString("x2"));
			return hex.ToString();
		}

		public static string CRC32Hex(Stream stream)
		{
			uint crc = 0xFFFFFFFFu;
			byte[] buffer = new byte[1024 * 1024];

			int count;
			while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
			{
				for (int index = 0; index < count; index++)
					crc = _CRC32_Table[(crc ^ buffer[index]) & 0xFF] ^ (crc >> 8);
			}

			return (~crc).ToString("x8");
		}

		public static string CRC32Hex(byte[] data)
		{
			uint crc = 0xFFFFFFFFu;

			for (int index = 0; index < data.Length; index++)
				crc = _CRC32_Table[(crc ^ data[index]) & 0xFF] ^ (crc >> 8);

			return (~crc).ToString("x8");
		}

		public static void ClearAttributes(string directory)
		{
			foreach (string filename in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
				File.SetAttributes(filename, FileAttributes.Normal);
		}

		public static string PrettyJSON(string json)
		{
			dynamic obj = JsonConvert.DeserializeObject<dynamic>(json);
			return JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
		}

		public static DataTable MakeDataTable(string columnNames, string columnTypes)
		{
			return MakeDataTable("untitled", columnNames, columnTypes);
		}

		public static DataTable MakeDataTable(string tableName, string columnNames, string columnTypes)
		{
			string[] names = columnNames.Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
			string[] types = columnTypes.Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);

			if (names.Length != types.Length)
				throw new ApplicationException("Make Data Table Bad definition.");

			DataTable table = new DataTable(tableName);

			List<int> keyColumnIndexes = new List<int>();

			for (int index = 0; index < names.Length; ++index)
			{
				string name = names[index];
				string typeName = "System." + types[index];

				if (typeName.EndsWith("*") == true)
				{
					typeName = typeName.Substring(0, typeName.Length - 1);
					keyColumnIndexes.Add(index);
				}

				table.Columns.Add(name, Type.GetType(typeName, true));
			}

			if (keyColumnIndexes.Count > 0)
			{
				List<DataColumn> keyColumns = new List<DataColumn>();
				foreach (int index in keyColumnIndexes)
					keyColumns.Add(table.Columns[index]);
				table.PrimaryKey = keyColumns.ToArray();
			}

			return table;
		}

		public static string FetchTextCached(string url)
		{
			string filename = url.Substring(url.IndexOf("://") + 3);
			filename = Path.Combine(Globals.CacheDirectory, Tools.ValidFileName(filename));
		
			string result = null;

			if (File.Exists(filename) == false || (DateTime.Now - File.GetLastWriteTime(filename) > TimeSpan.FromHours(3)))
			{
				try
				{
					Console.Write($"Downloading {url} ...");
					result = Query(url);
					Console.WriteLine("...done");

					string extention = Path.GetExtension(filename).ToLower();

					if (extention == ".json" && (result.StartsWith("{") == true || result.StartsWith("[") == true))
						result = PrettyJSON(result);
				}
				catch (TaskCanceledException e)
				{
					Console.WriteLine($"ERROR Fetch client timeout: {url} {e.Message}");
				}
				catch (HttpRequestException e)
				{
					Console.WriteLine($"ERROR Fetch request: {url} {e.Message} {e.InnerException?.Message}");
				}
				catch (Exception e)
				{
					Console.WriteLine($"ERROR Fetch: {url} {e.Message} {e.InnerException?.Message}");
				}

				if (result != null)
					File.WriteAllText(filename, result, Encoding.UTF8);
			}

			if (result == null && File.Exists(filename) == true)
				result = File.ReadAllText(filename, Encoding.UTF8);

			return result;
		}

		public static string Query(string url)
		{
			try
			{
				using (HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
				{
					if (url.StartsWith("https://archive.org/") == true)
						requestMessage.Headers.Add("Cookie", Globals.AuthCookie);

					Task<HttpResponseMessage> requestTask = Globals.HttpClient.SendAsync(requestMessage);
					requestTask.Wait();
					HttpResponseMessage responseMessage = requestTask.Result;

					Task<string> responseMessageTask = responseMessage.Content.ReadAsStringAsync();
					responseMessageTask.Wait();
					string responseBody = responseMessageTask.Result;

					if (responseMessage.IsSuccessStatusCode == false && responseMessage.StatusCode != HttpStatusCode.NotFound)
					{
						Tools.ConsoleHeading(1, $"HTTP Request Error, StatusCode: {responseMessage.StatusCode}");
						Console.WriteLine(responseBody);
					}
					responseMessage.EnsureSuccessStatusCode();

					return responseBody;
				}
			}
			catch (AggregateException e)
			{
				throw e.InnerException ?? e;
			}
		}


		private static Assets.FileDownloader _Downloader;

		/// <summary>
		/// The downloader is built once: it owns its HttpClient (infinite timeout,
		/// because a 40 GB CHD archive is not a 3 minute request) and a per-destination
		/// lock that makes concurrent requests for the same file safe.
		/// </summary>
		private static Assets.FileDownloader Downloader()
		{
			if (_Downloader != null)
				return _Downloader;

			HttpClient http = new HttpClient(new HttpClientHandler { UseCookies = false });
			http.DefaultRequestHeaders.Add("User-Agent", $"mame-ao/{Globals.AssemblyVersion} (https://github.com/sam-ludlow/mame-ao)");
			http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

			_Downloader = new Assets.FileDownloader(http, Globals.CacheDirectory ?? Path.GetTempPath());

			return _Downloader;
		}

		public static long Download(string url, string filename)
		{
			return Download(url, filename, 0);
		}

		/// <summary>
		/// Download to <paramref name="filename"/>. The downloader writes a .part
		/// file, resumes it on retry, verifies the size, and only renames on success,
		/// so an interrupted transfer never looks like a finished file.
		/// </summary>
		public static long Download(string url, string filename, long expectedSize)
		{
			if (expectedSize > 0)
				lock (Globals.WorkerTaskInfo)
					Globals.WorkerTaskInfo.BytesTotal = expectedSize;

			Dictionary<string, string> headers = null;

			if (url.StartsWith("https://archive.org/", StringComparison.Ordinal) == true && string.IsNullOrEmpty(Globals.AuthCookie) == false)
				headers = new Dictionary<string, string> { { "Cookie", Globals.AuthCookie } };

			long lastDot = 0;

			IProgress<(long current, long total)> progress = new Progress<(long current, long total)>(value => {
				if (expectedSize > 0)
					lock (Globals.WorkerTaskInfo)
						Globals.WorkerTaskInfo.BytesCurrent = value.current;

				if (value.current - lastDot >= Globals.DownloadDotSize)
				{
					Console.Write(".");
					lastDot = value.current;
				}
			});

			Assets.DownloadResult result = Downloader()
				.DownloadAsync(url, filename, headers, expectedSize > 0 ? expectedSize : (long?)null, null, progress)
				.GetAwaiter().GetResult();

			if (expectedSize > 0)
				lock (Globals.WorkerTaskInfo)
					Globals.WorkerTaskInfo.BytesCurrent = result.Bytes;

			return result.Bytes;
		}

		public static void LinkFiles(string[][] linkTargetFilenames)
		{
			if (linkTargetFilenames == null)
				return;

			IFileLinker linker = PlatformServices.IsInitialised ? PlatformServices.Current.Links : null;

			foreach (string[] pair in linkTargetFilenames)
			{
				if (pair == null || pair.Length < 2)
					continue;

				string link = pair[0];
				string target = pair[1];

				bool created = false;

				if (linker != null && linker.IsSupported == true)
				{
					try { created = linker.Create(link, target); }
					catch (Exception) { created = false; }
				}

				if (created == false)
				{
					try
					{
						if (Directory.Exists(target) == true)
							Directory.CreateSymbolicLink(link, target);
						else
							File.CreateSymbolicLink(link, target);
						created = true;
					}
					catch (Exception e)
					{
						Console.WriteLine($"!!! Could not link {link} -> {target}: {e.Message}");
					}
				}
			}
		}

		public static void MsAccessLinkSQLite(string sourceFilename)
		{
			string targetFilename = $"{sourceFilename}.accdb";

			string arguments = $"access-link-new filename=\"{targetFilename}\" odbc=\"{sourceFilename}\"";

			MsAccess(arguments, "Create MS Access databases linked to SQLite");

			ConsoleHeading(2, new string[] { "MS Access linked database created", sourceFilename, "=>", targetFilename });
		}

		public static void MsAccessFromXML(string sourceFilename)
		{
			string targetFilename = $"{sourceFilename}.accdb";

			string arguments = $"xml-insert-new filename=\"{sourceFilename}\"";

			MsAccess(arguments, "Create MS Access databases from MAME XML");

			ConsoleHeading(2, new string[] { "MS Access database created", sourceFilename, "=>", targetFilename });
		}

		public static void MsAccess(string arguments, string description)
		{
			string[] locations = new string[] {
				Path.Combine(Globals.RootDirectory, "access-linker.exe"),
				Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "access-linker.exe"),
			};

			string exeFilename = null;
			foreach (string location in locations)
			{
				if (File.Exists(location) == true)
				{
					exeFilename = location;
					break;
				}
			}

			if (exeFilename == null)
				throw new ApplicationException($"Access Linker not found: \"{String.Join(", ", locations)}\". Install from here: https://github.com/sam-ludlow/access-linker/releases/latest");

			Version version = AssemblyName.GetAssemblyName(exeFilename).Version;
			string localVersion = $"{version.Major}.{version.Minor}";

			ConsoleHeading(1, new string[] { description, exeFilename, localVersion });

			ProcessStartInfo startInfo = new ProcessStartInfo(exeFilename)
			{
				Arguments = arguments,
				UseShellExecute = true,
			};

			using (Process process = new Process())
			{
				process.StartInfo = startInfo;

				process.Start();
				process.WaitForExit();

				if (process.ExitCode != 0)
					throw new ApplicationException("access-linker.exe Bad exit code");
			}
		}

		public static DateTime FromEpochDate(string epoch)
		{
			return FromEpochDate(double.Parse(epoch));
		}
		public static DateTime FromEpochDate(double epoch)
		{
			return EpochDateTime.AddSeconds(epoch);
		}
		private static readonly DateTime EpochDateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		public static string DataSize(long sizeBytes)
		{
			return DataSize((ulong)sizeBytes);
		}
		public static string DataSize(ulong sizeBytes)
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

			throw new ApplicationException("Failed to find Data Size: " + sizeBytes.ToString());
		}

		public static void Bitmap2SVG(string filenameOrDirectory)
		{
			string[] filenames = new string[] { filenameOrDirectory };

			if (Directory.Exists(filenameOrDirectory) == true)
				filenames = Directory.GetFiles(filenameOrDirectory, "*.png");

			foreach (string filename in filenames)
			{
				string targetFilename = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename) + ".svg");

				Bitmap2SVG(filename, targetFilename);
			}

		}
		public static void Bitmap2SVG(string filename, string targetFilename)
		{
			using (StreamWriter writer = new StreamWriter(targetFilename, false, Encoding.UTF8))
			{
				using (Image image = Image.FromFile(filename))
				{
					writer.WriteLine("<svg version=\"1.1\" id=\"mame-ao\" xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xml:space=\"preserve\" " +
						$"x=\"{0}px\" y=\"{0}px\" width=\"{image.Width}px\" height=\"{image.Height}px\">");

					using (Bitmap bitmap = new Bitmap(image))
					{
						for (int y = 0; y < image.Height; ++y)
						{
							for (int x = 0; x < image.Width; ++x)
							{
								Color colour = bitmap.GetPixel(x, y);

								if (colour.A == 0)
									continue;

								string fill = String.Format("{0:x6}", colour.ToArgb() & 0xFFFFFF);

								writer.WriteLine($"<rect x=\"{x}\" y=\"{y}\" width=\"1\" height=\"1\" fill=\"#{fill}\"/>");
							}
						}
					}

					Console.WriteLine($"SVG: {image.Width} X {image.Height} : {targetFilename}");

					writer.WriteLine("</svg>");
				}
			}
		}

		public static void CompressSingleFile(string filename, string zipFilename)
		{
			using (FileStream fileStream = new FileStream(zipFilename, FileMode.Create))
				using (ZipArchive zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Create))
					zipArchive.CreateEntryFromFile(filename, Path.GetFileName(filename));
		}

		public static void ZipFileExtractToDirectory(string filename, string directory)
		{
			using (var zipFile = ZipFile.OpenRead(filename))
			{
				var subDirectories = new HashSet<string>() { directory };
				foreach (var entry in zipFile.Entries)
				{
					if (string.IsNullOrEmpty(entry.Name) == true)
						continue;

					string name = Path.GetDirectoryName(entry.FullName.Trim('/').Replace('/', Path.DirectorySeparatorChar));
					subDirectories.Add(Path.Combine(directory, name));
				}
				foreach (var subDirectory in subDirectories)
					Directory.CreateDirectory(subDirectory);

				foreach (var entry in zipFile.Entries)
				{
					if (string.IsNullOrEmpty(entry.Name) == true)
						continue;

					string name = Path.Combine(directory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));

					int index = name.LastIndexOf(Path.DirectorySeparatorChar);

					name = Path.Combine(name.Substring(0, index), ValidFileName(name.Substring(index + 1)));

					Console.WriteLine(name);

					using (var writeStream = new FileStream(name, FileMode.CreateNew))
					{
						using (var readStream = entry.Open())
						{
							readStream.CopyTo(writeStream);
						}
					}
				}
			}
		}

		public static void Compress7Zip(string sourcePath, string targetFilename)
		{
			string programFilename = @"C:\Program Files\7-Zip\7z.exe";

			if (File.Exists(programFilename) == false)
				throw new ApplicationException($"7-Zip Program required: {programFilename}");

			string arguments = $"a -y \"{targetFilename}\" \"{sourcePath}\"";

			Console.WriteLine(arguments);

			ProcessStartInfo startInfo = new ProcessStartInfo(programFilename)
			{
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
			};

			using (Process process = new Process())
			{
				process.StartInfo = startInfo;

				process.OutputDataReceived += new DataReceivedEventHandler((sender, e) => Console.WriteLine(e.Data));
				process.ErrorDataReceived += new DataReceivedEventHandler((sender, e) => Console.WriteLine(e.Data));

				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();

				if (process.ExitCode != 0)
					throw new ApplicationException($"Z-Zip bad exit code: {process.ExitCode}");
			}
		}

		public static void ExtractToDirectory7Zip(string sourceFilename, string targetDirectory)
		{
			string programFilename = @"C:\Program Files\7-Zip\7z.exe";

			if (File.Exists(programFilename) == false)
				throw new ApplicationException($"7-Zip Program required: {programFilename}");

			string arguments = $"x -y -o\"{targetDirectory}\" \"{sourceFilename}\"";

			ProcessStartInfo startInfo = new ProcessStartInfo(programFilename)
			{
				Arguments = arguments,
				WorkingDirectory = targetDirectory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
			};

			using (Process process = new Process())
			{
				process.StartInfo = startInfo;

				process.OutputDataReceived += new DataReceivedEventHandler((sender, e) => Console.WriteLine(e.Data));
				process.ErrorDataReceived += new DataReceivedEventHandler((sender, e) => Console.WriteLine(e.Data));

				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();

				Console.WriteLine($"Z-Zip exit code: {process.ExitCode}");

				if (process.ExitCode != 0)
					throw new ApplicationException($"Z-Zip Extract bad exit code: {process.ExitCode}");
			}
		}

		public static void UtilConvert7Zip2Zip(string sourceDirectory, string targetDirectory)
		{
			foreach (string filename7Zip in Directory.GetFiles(sourceDirectory, "*.7z"))
			{
				string filenameZip = Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(filename7Zip) + ".zip");

				Console.WriteLine($"{filename7Zip}\t=>\t{filenameZip}");

				try
				{
					using (TempDirectory tempDir = new TempDirectory())
					{
						ExtractToDirectory7Zip(filename7Zip, tempDir.Path);
						File.Delete(filenameZip);
						ZipFile.CreateFromDirectory(tempDir.Path, filenameZip);
					}
				}
				catch (Exception e)
				{
					Console.WriteLine(e.Message);
				}
			}
		}

		public static void UtilDownloadFromWeb(string dataFilename, int columnIndex, string downloadUrlTemplate, string targetDirectory)
		{
			foreach (string line in File.ReadLines(dataFilename))
			{
				string[] parts = line.Split('\t');
				string name = parts[columnIndex];

				string url = downloadUrlTemplate.Replace("@", name);
				string filename = Path.Combine(targetDirectory, Path.GetFileName(url));

				if (File.Exists(filename) == true)
					continue;

				Console.WriteLine($"{url}\t=>\t{filename}");

				Tools.Download(url, filename);
			}

		}

		public static string XML2JSON(XElement element)
		{
			JsonSerializerSettings serializerSettings = new JsonSerializerSettings
			{
				Formatting = Newtonsoft.Json.Formatting.Indented
			};

			using (StringWriter writer = new StringWriter())
			{
				CustomJsonWriter customJsonWriter = new CustomJsonWriter(writer);

				JsonSerializer jsonSerializer = JsonSerializer.Create(serializerSettings);
				jsonSerializer.Serialize(customJsonWriter, element);

				return writer.ToString();
			}
		}
		public static void XML2JSON(string inputXmlFilename, string outputJsonFilename)
		{
			XmlDocument xmlDocument = new XmlDocument();
			xmlDocument.Load(inputXmlFilename);

			JsonSerializerSettings serializerSettings = new JsonSerializerSettings
			{
				Formatting = Newtonsoft.Json.Formatting.Indented
			};

			using (StreamWriter streamWriter = new StreamWriter(outputJsonFilename, false, new UTF8Encoding(false)))
			{
				CustomJsonWriter customJsonWriter = new CustomJsonWriter(streamWriter);

				JsonSerializer jsonSerializer = JsonSerializer.Create(serializerSettings);
				jsonSerializer.Serialize(customJsonWriter, xmlDocument);
			}
		}

		public static void DataFileMoveHeader(DataSet dataSet)
		{
			DataTable headerTable = dataSet.Tables["header"];
			DataTable datafileTable = dataSet.Tables["datafile"];

			if (headerTable == null || headerTable.Rows.Count != 1)
				throw new ApplicationException("Did not find one headerTable row");

			if (datafileTable == null || datafileTable.Rows.Count != 1)
				throw new ApplicationException("Did not find one datafileTable row");

			foreach (DataColumn column in headerTable.Columns)
			{
				if (column.ColumnName.EndsWith("_id") == true)
					continue;

				if (datafileTable.Columns.Contains(column.ColumnName) == false)
					datafileTable.Columns.Add(column.ColumnName, typeof(string));

				datafileTable.Rows[0][column.ColumnName] = headerTable.Rows[0][column.ColumnName];
			}

			dataSet.Tables.Remove("header");
		}

		public static void DataFileMergeDataSet(DataSet sourceDataSet, DataSet targetDataSet)
		{
			foreach (DataTable sourceTable in sourceDataSet.Tables)
			{
				sourceTable.PrimaryKey = new DataColumn[0];

				DataTable targetTable = null;
				if (targetDataSet.Tables.Contains(sourceTable.TableName) == false)
				{
					targetTable = new DataTable(sourceTable.TableName);
					targetDataSet.Tables.Add(targetTable);
				}
				else
				{
					targetTable = targetDataSet.Tables[sourceTable.TableName];
				}

				foreach (DataColumn column in sourceTable.Columns)
				{
					column.Unique = false;

					if (targetTable.Columns.Contains(column.ColumnName) == false)
					{
						DataColumn targetColumn = targetTable.Columns.Add(column.ColumnName, column.DataType);
						targetColumn.Unique = false;
					}
				}
			}

			Dictionary<string, long> addIds = new Dictionary<string, long>();
			foreach (DataTable sourceTable in sourceDataSet.Tables)
				addIds.Add(sourceTable.TableName + "_id", targetDataSet.Tables[sourceTable.TableName].Rows.Count);

			foreach (DataTable sourceTable in sourceDataSet.Tables)
			{
				foreach (DataColumn column in sourceTable.Columns)
				{
					if (column.ColumnName.EndsWith("_id") == false)
						continue;

					foreach (DataRow row in sourceTable.Rows)
						row[column] = (long)row[column] + addIds[column.ColumnName];
				}

				DataTable targetTable = targetDataSet.Tables[sourceTable.TableName];

				foreach (DataRow row in sourceTable.Rows)
					targetTable.ImportRow(row);
			}
		}

	}

	public class CustomJsonWriter : JsonTextWriter
	{
		public CustomJsonWriter(TextWriter writer) : base(writer) { }
		public override void WritePropertyName(string name)
		{
			if (name.StartsWith("@") == true)
				base.WritePropertyName(name.Substring(1));
			else
				base.WritePropertyName(name);
		}
	}

	public class LineArguments
	{
		public string Line;
		public string First = null;

		public LineArguments(string line)
		{
			Line = line.Trim();

			if (line.Length == 0)
				return;

			int index = line.IndexOf(' ');
			if (index == -1)
				First = line;
			else
				First = line.Substring(0, index);
		}

		public string[] Arguments(int expectedCount)
		{
			return Arguments(expectedCount, false);
		}

		public string[] Arguments(int expectedCount, bool lastHyphen)
		{
			if (Line.Length == 0)
				return new string[] { };

			List<string> arguments = new List<string>();

			int currentIndex = 0;
			bool done = false;
			while (done == false)
			{
				int nextIndex = Line.IndexOf(' ', currentIndex + 1);
				if (nextIndex != -1 && (nextIndex + 1) < Line.Length)
					++nextIndex;

				string part;
				if (nextIndex == -1 || (expectedCount > 0 && arguments.Count == (expectedCount - 1)) || (lastHyphen == true && Line[currentIndex] == '-'))
				{
					part = Line.Substring(currentIndex);
					done = true;
				}
				else
				{
					part = Line.Substring(currentIndex, nextIndex - currentIndex);
				}

				part = part.Trim();

				if (part.Length > 0)
					arguments.Add(part);

				currentIndex = nextIndex;
			}

			return arguments.ToArray();
		}
	}

	public class TempDirectory : IDisposable
	{
		private readonly string _LockFilePath;
		private readonly string _Path;

		public TempDirectory()
		{
			_LockFilePath = System.IO.Path.GetTempFileName();
			_Path = _LockFilePath + ".dir";

			Directory.CreateDirectory(this._Path);
		}

		public void Dispose()
		{
			if (Directory.Exists(_Path) == true)
				Directory.Delete(_Path, true);

			if (_LockFilePath != null)
				File.Delete(_LockFilePath);
		}

		public string Path
		{
			get => _Path;
		}

		public override string ToString()
		{
			return _Path;
		}
	}

}
