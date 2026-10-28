using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Spludlow.MameAO
{
	public class HashStore
	{
		private readonly string _StoreDirectory;
		private Dictionary<string, string> _Hashes;

		private readonly HashMethod _HashMethod;

		private readonly object _Lock = new object();

		public delegate string HashMethod(string filename);

		public HashStore(string storeDirectory, HashMethod hashMethod)
		{
			_StoreDirectory = storeDirectory;

			_HashMethod = hashMethod;

			if (Directory.Exists(_StoreDirectory) == false)
				Directory.CreateDirectory(_StoreDirectory);

			Refresh();
		}


		public int Length
		{
			get
			{
				lock (_Lock)
				{
					return _Hashes.Count;
				}
			}
		}

		/// <summary>
		/// SHA1s are lowercase hex and the store is content addressed. Filenames are
		/// normalised so that a store populated on a case-insensitive filesystem (or
		/// by an older build) still resolves on Linux.
		/// </summary>
		private static string Normalise(string sha1)
		{
			if (string.IsNullOrWhiteSpace(sha1))
				throw new ArgumentException("A SHA1 is required.", nameof(sha1));
			if (sha1.Length < 2)
				throw new ArgumentException($"'{sha1}' is not a SHA1.", nameof(sha1));

			return sha1.ToLowerInvariant();
		}

		public void Refresh()
		{
			lock (_Lock)
			{
				_Hashes = new Dictionary<string, string>(StringComparer.Ordinal);

				foreach (string filename in Directory.GetFiles(_StoreDirectory, "*", SearchOption.AllDirectories))
				{
					string key = Path.GetFileName(filename).ToLowerInvariant();
					_Hashes[key] = filename;
				}
			}
		}

		public string Hash(string filename)
		{
			return _HashMethod(filename);
		}

		public bool Add(string filename)
		{
			return Add(filename, false);
		}
		public bool Add(string filename, bool move)
		{
			return Add(filename, move, null);
		}
		public bool Add(string filename, bool move, string sha1)
		{
			string key = Normalise(sha1 ?? _HashMethod(filename));

			bool adding = false;

			lock (_Lock)
			{
				if (_Hashes.ContainsKey(key) == false)
					adding = true;
			}

			if (adding == true)
			{
				string storeFilename = StoreFilename(key, true);
				if (move == false)
					File.Copy(filename, storeFilename, overwrite: false);
				else
					File.Move(filename, storeFilename);

				lock (_Lock)
				{
					_Hashes[key] = storeFilename;
				}
			}

			return adding;
		}

		public bool Delete(string sha1)
		{
			string key = Normalise(sha1);
			bool deleting = false;

			lock (_Lock)
			{
				if (_Hashes.TryGetValue(key, out string storeFilename) == true)
				{
					deleting = true;
					_Hashes.Remove(key);
					File.Delete(storeFilename);
				}
			}

			return deleting;
		}

		public bool Exists(string sha1)
		{
			if (string.IsNullOrWhiteSpace(sha1))
				return false;

			string key = sha1.ToLowerInvariant();

			lock (_Lock)
			{
				return _Hashes.ContainsKey(key);
			}
		}

		public string Filename(string sha1)
		{
			if (string.IsNullOrWhiteSpace(sha1))
				return null;

			string key = sha1.ToLowerInvariant();

			lock (_Lock)
			{
				if (_Hashes.TryGetValue(key, out string filename) == false)
					return null;

				return filename;
			}
		}

		public string[] Hashes()
		{
			lock (_Lock)
			{
				return _Hashes.Keys.ToArray();
			}
		}

		public string[] FileNames()
		{
			lock (_Lock)
			{
				return _Hashes.Values.ToArray();
			}
		}

		private string StoreFilename(string sha1, bool writeMode)
		{
			string filename = GetFileName(_StoreDirectory, sha1);

			if (writeMode == true)
			{
				string directory = Path.GetDirectoryName(filename);
				if (Directory.Exists(directory) == false)
					Directory.CreateDirectory(directory);
			}

			return filename;
		}

		public static string GetFileName(string storeDirectory, string sha1)
		{
			string key = Normalise(sha1);

			return Path.Combine(storeDirectory, key.Substring(0, 2), key);
		}
		public static void ValidateHashStore(HashStore hashStore, string type)
		{
			int progressFrequency = type == "DISK" ? 10 : 1000;

			string[] filenames = hashStore.FileNames().ToArray();

			string title = $"Validate Hash Store: {type} {filenames.Length}";

			Tools.ConsoleHeading(1, title);

			DataTable table = Tools.MakeDataTable(
				"Filename	Problem",
				"String		String"
			);

			DateTime startTime = DateTime.Now;

			long progress = 0;

			var options = new ParallelOptions
			{
				MaxDegreeOfParallelism = 4
			};
			Parallel.ForEach(filenames, options, (filename) =>
			{
				long current = Interlocked.Increment(ref progress);
				if ((current % progressFrequency) == 0 || current == 1)
					Console.WriteLine($"{Math.Round((double)progress / (double)filenames.Length * 100.0, 1)} %\t\t{Math.Round((DateTime.Now - startTime).TotalMinutes, 1)} m");

				try
				{
					string storeHash = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
					string actualHash = hashStore.Hash(filename);

					if (storeHash != actualHash.ToLowerInvariant())
						throw new ApplicationException($"storeHash:{storeHash}, actualHash:{actualHash}");
				}
				catch (Exception ee)
				{
					Console.WriteLine($"{filename}\t{ee.Message}");

					lock (table)
						table.Rows.Add(filename, ee.Message);
				}
			});

			Console.WriteLine($"Took Minutes: {Math.Round((DateTime.Now - startTime).TotalMinutes, 1)}");

			if (table.Rows.Count > 0)
			{
				Console.WriteLine("!!! Bad files found see report.");
				Globals.Reports.SaveHtmlReport(table, title);
			}
			else
			{
				Console.WriteLine("All files are OK.");
			}
		}

	}
}
