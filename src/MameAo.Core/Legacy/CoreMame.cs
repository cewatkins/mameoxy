using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Spludlow.MameAO
{
	internal class CoreMame : ICore
	{
		string ICore.Name { get => "mame"; }
		string ICore.Version { get => _Version; }
		string ICore.Directory { get => _CoreDirectory; }
		string[] ICore.ConnectionStrings { get => new string[] { _ConnectionStringMachine, _ConnectionStringSoftware }; }

		Dictionary<string, string> ICore.SoftwareListDescriptions { get => _SoftwareListDescriptions; }
		Dictionary<string, string[]> ICore.Filters { get => _Filters; }

		private string _RootDirectory = null;
		private string _CoreDirectory = null;

		private string _Version = null;

		private string _ConnectionStringMachine = null;
		private string _ConnectionStringSoftware = null;

		private Dictionary<string, DataRow[]> _MachineDevicesRefs = null;
		private Dictionary<string, string> _SoftwareListDescriptions = null;
		private Dictionary<string, string[]> _Filters = null;

		private List<DataQueryProfile> _DataQueryProfiles = new List<DataQueryProfile>();

		public CoreMame()
		{
			string[][] types = new string[][] {
				new string[] { "arcade",        "Arcade",       "Arcade Machines" },
				new string[] { "software",      "Software",     "Computers & Consoles with Software" },
				new string[] { "pinball",       "Pinball",      "Pinball Machines" },
				new string[] { "gamble",        "Gamble",       "Gamble Machines" },
				new string[] { "other",         "Other",        "Other Machines" },
				new string[] { "everything",    "Everything",   "Every Machine" },
				new string[] { "favorites",     "Favorites",    "Favorite Machines" },
			};
			foreach (string[] type in types)
			{
				_DataQueryProfiles.Add(new DataQueryProfile()
				{
					Key = type[0],
					Text = type[1],
					Decription = type[2],
				});
			}
		}

		void ICore.Initialize(string directory, string version)
		{
			//	TODO: validate version
			_RootDirectory = directory;
			Directory.CreateDirectory(_RootDirectory);

			if (version != "0")
				_Version = version;
		}

		/// <summary>
		/// Linux: do not download MAME. Find the natively installed one, remember
		/// its version as the core version, and prepare the per-version directory
		/// (ini, links, output directories).
		/// </summary>
		int ICore.Get()
		{
			MameInstall install = ResolveInstall();

			_CoreDirectory = MameCoreLayout.ForVersion(_RootDirectory, _Version).CoreDirectory;

			foreach (string warning in new MameCoreLayout(_CoreDirectory).Prepare(install))
				Console.WriteLine($"!!! {warning}");

			Console.WriteLine($"MAME {install.VersionRaw} ({install.Detail}) -> {_CoreDirectory}");

			return 0;
		}

		void ICore.Xml()
		{
			MameInstall install = ResolveInstall();

			_CoreDirectory = Path.Combine(_RootDirectory, _Version);

			Cores.ExtractXML(install, _CoreDirectory);
		}

		/// <summary>
		/// Resolve the native MAME and make <see cref="_Version"/> agree with it.
		///
		/// The Windows original let GitHub tell it which version to fetch, so many
		/// call sites lazily called LatestLocalVersion(). Here the installed binary
		/// is the single source of truth: a requested version that does not match
		/// is a configuration error with an explicit message, not a silent
		/// download of a second MAME.
		/// </summary>
		private MameInstall ResolveInstall()
		{
			MameInstall install = MameProvider.Get();

			if (string.IsNullOrWhiteSpace(_Version) == true)
				_Version = install.Version;
			else if (_Version != install.Version)
				throw new ApplicationException(
					$"MAME version '{_Version}' was requested but the installed MAME is {install.VersionRaw} ({install.Detail}). " +
					$"Point --mame-path at a {_Version} build, or set MameVersion={install.Version} in the config.");

			return install;
		}

		void ICore.Json()
		{
			if (_Version == null)
				ResolveInstall();
			_CoreDirectory = Path.Combine(_RootDirectory, _Version);

			foreach (string xmlFilename in new string[] { Path.Combine(_CoreDirectory, "_machine.xml"), Path.Combine(_CoreDirectory, "_software.xml") })
			{
				string jsonFilename = xmlFilename.Substring(0, xmlFilename.Length - 4) + ".json";

				if (File.Exists(jsonFilename) == false)
					Tools.XML2JSON(xmlFilename, jsonFilename);
			}
		}

		/// <summary>
		/// The installed MAME decides the version, not a directory listing. Kept
		/// for callers that want the version without an ICore instance.
		/// </summary>
		public static string LatestLocalVersion(string directory)
		{
			MameInstall install = MameProvider.Get();

			if (Directory.Exists(Path.Combine(directory, install.Version)) == true)
				return install.Version;

			List<string> versions = new List<string>();

			foreach (string versionDirectory in Directory.GetDirectories(directory))
			{
				if (Mame.IsCoreDirectory(versionDirectory) == true)
					versions.Add(Path.GetFileName(versionDirectory));
			}

			if (versions.Count == 0)
				throw new ApplicationException(
					$"No prepared MAME directory in '{directory}' for the installed MAME {install.VersionRaw} ({install.Detail}).");

			versions.Sort();

			return versions[versions.Count - 1];
		}

		void ICore.MsAccess()
		{
			throw new PlatformNotSupportedException(
				"MS Access export is not available on Linux: it needs the Microsoft ACE/Jet provider, which has no Linux build. " +
				"Use the SQLite database in the MAME version directory instead.");
		}
		void ICore.Zips()
		{
			if (_Version == null)
				ResolveInstall();
			_CoreDirectory = Path.Combine(_RootDirectory, _Version);

			Cores.Zips(_CoreDirectory);
		}

		void ICore.SQLite()
		{
			if (_Version == null)
				ResolveInstall();

			_CoreDirectory = Path.Combine(_RootDirectory, _Version);

			Tools.ConsoleHeading(1, new string[] { "SQLite MAME", _Version, _CoreDirectory });

			Cores.MakeSQLite(_CoreDirectory, null, null, false, null, null);
		}

		void ICore.SQLiteAo()
		{
			if (_Version == null)
				ResolveInstall();

			_CoreDirectory = Path.Combine(_RootDirectory, _Version);

			InitializeConnections();

			Cores.MakeSQLite(_CoreDirectory, ReadXML.RequiredMachineTables, ReadXML.RequiredSoftwareTables, false, Globals.AssemblyVersion, Cores.AddExtraAoData);

			if (Cores.GetAoMetaDataAssemblyVersion(_ConnectionStringMachine) != Globals.AssemblyVersion)
			{
				Console.WriteLine("SQLite database from previous version re-creating.");
				Cores.MakeSQLite(_CoreDirectory, ReadXML.RequiredMachineTables, ReadXML.RequiredSoftwareTables, true, Globals.AssemblyVersion, Cores.AddExtraAoData);
			}

			//
			// Cache machine device_ref to speed up machine dependancy resolution
			//
			_MachineDevicesRefs = new Dictionary<string, DataRow[]>();

			DataTable device_refTable = Database.ExecuteFill(_ConnectionStringMachine, "SELECT * FROM device_ref");

			foreach (DataRow row in Database.ExecuteFill(_ConnectionStringMachine, "SELECT machine_id, name FROM machine").Rows)
				_MachineDevicesRefs.Add((string)row["name"], device_refTable.Select($"machine_id = {(long)row["machine_id"]}"));

			//
			// Cache softwarelists for description
			//
			_SoftwareListDescriptions = new Dictionary<string, string>();

			foreach (DataRow row in Database.ExecuteFill(_ConnectionStringSoftware, "SELECT [name], [description] FROM [softwarelist] ORDER BY [description]").Rows)
				_SoftwareListDescriptions.Add((string)row["name"], (string)row["description"]);

			_Filters = Cores.GetFilters(_ConnectionStringMachine);
		}

		List<DataQueryProfile> ICore.GetDataQueryProfiles()
		{
			return _DataQueryProfiles;
		}

		private void InitializeConnections()
		{
			_ConnectionStringMachine = Database.MakeSQLiteConnectionString(Path.Combine(_CoreDirectory, "_machine.sqlite"));
			_ConnectionStringSoftware = Database.MakeSQLiteConnectionString(Path.Combine(_CoreDirectory, "_software.sqlite"));
		}


		void ICore.AllSHA1(HashSet<string> hashSet)
		{
			if (_Version == null)
				ResolveInstall();

			_CoreDirectory = Path.Combine(_RootDirectory, _Version);

			InitializeConnections();

			Console.Write($"Load all database SHA1 ...");
			Cores.AllSHA1(hashSet, _ConnectionStringMachine, new string[] { "rom", "disk" });
			Cores.AllSHA1(hashSet, _ConnectionStringSoftware, new string[] { "rom", "disk" });
			Console.WriteLine("...done");
		}

		void ICore.MSSql(string serverConnectionString, string[] databaseNames)
		{
			throw new PlatformNotSupportedException(
				"SQL Server export is not available in the Linux port: System.Data.SqlClient is a Windows-oriented " +
				"provider and the port does not ship it. Use SQLite, or run the export from the Windows build.");
		}

		void ICore.MSSqlPayload(string serverConnectionString, string[] databaseNames)
		{
			throw new PlatformNotSupportedException(
				"SQL Server export is not available in the Linux port: System.Data.SqlClient is not shipped. Use SQLite instead.");
		}




		DataRow ICore.GetMachine(string machine_name) => Cores.GetMachine(_ConnectionStringMachine, machine_name);

		DataRow[] ICore.GetMachineRoms(string machine_name) => Cores.GetMachineRoms(_ConnectionStringMachine, machine_name);

		DataRow[] ICore.GetMachineDisks(DataRow machine) => Cores.GetMachineDisks(_ConnectionStringMachine, machine);

		DataRow[] ICore.GetMachineSamples(DataRow machine) => Cores.GetMachineSamples(_ConnectionStringMachine, machine);

		DataRow[] ICore.GetMachineSoftwareLists(DataRow machine) => Cores.GetMachineSoftwareLists(_ConnectionStringMachine, machine, _SoftwareListDescriptions);

		DataRow ICore.GetSoftwareList(string softwarelist_name) => Cores.GetSoftwareList(_ConnectionStringSoftware, softwarelist_name);

		HashSet<string> ICore.GetReferencedMachines(string machine_name) => Cores.GetReferencedMachines(this, machine_name);

		DataRow[] ICore.GetMachineDeviceRefs(string machine_name) => _MachineDevicesRefs[machine_name];

		DataRow ICore.GetSoftware(DataRow softwarelist, string software_name) => Cores.GetSoftware(_ConnectionStringSoftware, softwarelist, software_name);

		DataRow ICore.GetSoftware(string softwarelist_name, string software_name) => Cores.GetSoftware(_ConnectionStringSoftware, softwarelist_name, software_name);

		DataRow[] ICore.GetSoftwareSharedFeats(DataRow software) => Cores.GetSoftwareSharedFeats(_ConnectionStringSoftware, software);

		DataRow[] ICore.GetSoftwareListsSoftware(DataRow softwarelist) => Cores.GetSoftwareListsSoftware(_ConnectionStringSoftware, softwarelist);

		DataRow[] ICore.GetSoftwareRoms(DataRow software) => Cores.GetSoftwareRoms(_ConnectionStringSoftware, software);

		DataRow[] ICore.GetSoftwareDisks(DataRow software) => Cores.GetSoftwareDisks(_ConnectionStringSoftware, software);

		DataRow[] ICore.GetMachineFeatures(DataRow machine) => Cores.GetMachineFeatures(_ConnectionStringMachine, machine);

		string ICore.GetRequiredMedia(string machine_name, string softwarelist_name, string software_name) =>
			Cores.GetRequiredMedia(_ConnectionStringMachine, _ConnectionStringSoftware, _SoftwareListDescriptions, machine_name, softwarelist_name, software_name);

		DataTable ICore.QueryMachines(string profile, int offset, int limit, string search, string manufacturer, string[] status, string[] display, string[] players, string[] control, bool? mechanical, bool? clone, string order, string sort) =>
			Cores.QueryMachines(_ConnectionStringMachine, profile, offset, limit, search, manufacturer, status, display, players, control, mechanical, clone, order, sort);

		DataTable ICore.QuerySoftware(string softwarelist_name, int offset, int limit, string search, string publisher, string order, string sort, string favorites_machine) =>
			Cores.QuerySoftware(_ConnectionStringSoftware, softwarelist_name, offset, limit, search, publisher, order, sort, favorites_machine);

	}
}
