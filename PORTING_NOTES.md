# Porting `mame-ao` to Linux — discovery notes

Status: **Phase 1 implemented and running; see §12 for the record and §13 for what is still unproven.**
Author: porting session, 2026-10-28.
These notes are the running engineering log for the port. Section 9 is the
recommendation; section 10 lists the decisions I need from you.

---

## 1. Sources and provenance

Cloned read-only into `/tmp/opencode/po` for analysis; the port itself lives in
this repository.

| Repo | Commit | Date | Size | Licence |
|---|---|---|---|---|
| `sam-ludlow/mame-ao` | `aea6e491a05049243802c17e3294bfee375395e7` | 2026-10-23 | 6.0 MB, 39 `.cs`, 21 430 LOC | MIT |
| `sam-ludlow/dome-bt` | `37949850ae055727d6b2e6c26452e589a8115fb3` | 2026-10-18 | 424 KB, 7 `.cs`, ~3.5 kLOC | MIT |
| `sam-ludlow/mame-ao-react` | `09165533996e3b2972b06790a3dfb11dadecd9ac` | 2025-04-06 | 1.3 MB | see repo |

`mame-ao` assembly version at clone time: **1.185**.

The upstream README claims: Windows 64-bit, .NET Framework 4.8, 2 GB RAM, 2 GB
disk "absolute minimum", CPU with x86-64-v2 (MAME > 0.273), and that the app is
deliberately **32-bit to keep RAM usage down**. The last two claims are Windows
artefacts (they describe the MAME binaries it downloads and the AnyCPU/x86 build)
and are dropped by this port.

---

## 2. Project layout, framework and dependencies

### 2.1 Shape

Single project, `mame-ao.csproj`, old-style MSBuild, `TargetFrameworkVersion
v4.8`, `OutputType Exe`, `PlatformTarget AnyCPU` plus explicit `x64`
configurations, `Prefer32Bit` behaviour from the AnyCPU/x86 history. One
solution file. `Properties/AssemblyInfo.cs` carries the version used by CI.

`source/` (39 files, LOC):

| Area | Files |
|---|---|
| Entry / lifecycle | `Program.cs` (48), `MameAO.cs` (960), `Operations.cs` (554) |
| Core emulator abstraction | `Cores.cs` (964), `CoreMame.cs` (324), `CoreHbMame.cs` (394), `CoreFbNeo.cs` (1385), `CorePinballVisual.cs` (806) |
| Dat-file cores | `CoreTosec.cs` (411), `CoreNoIntro.cs` (391), `CoreRedump.cs` (368) |
| MAME data | `Mame.cs` (293), `Database.cs` (542), `ReadXML.cs` (170), `HashStore.cs` (240), `Place.cs` (620), `Artwork.cs` (248), `Samples.cs` (135), `MameChdMan.cs` (141) |
| Fetch operations | `OperationsMameish.cs` (1603), `OperationsDatish.cs` (808), `ArchiveOrgItem.cs` (359), `BitTorrent.cs` (494) |
| UI / HTTP | `WebServer.cs` (1309), `UI.html` (62 KB), `_styles.css` (written by `.style`) |
| Reporting | `Reports.cs` (2334), `Snap.cs` (869), `Export.cs` (629), `Genre.cs` (320), `Cheats.cs` (386), `Favorites.cs` (259) |
| Plumbing | `Tools.cs` (1305), `Settings.cs` (100), `Import.cs` (263), `SelfUpdate.cs` (136), `GitHubRepo.cs` (87), `PhoneHome.cs` (442), `Upload.cs` (636), `Test.cs` (97) |

### 2.2 NuGet / system dependencies (complete)

`packages.config` has exactly three entries:

| Package | Version | Purpose | Linux viability |
|---|---|---|---|
| `Newtonsoft.Json` | 13.0.4 | all JSON (archive.org, GitHub, UI API) | fine; consider `System.Text.Json` or keep Newtonsoft |
| `System.Data.SQLite.Core` | 1.0.119.0 | SQLite ADO.NET | **replace** with `Microsoft.Data.Sqlite` (managed `SQLitePCLRaw`, has `linux-arm64`/`linux-x64` native assets) |
| `Stub.System.Data.SQLite.Core.NetFramework` | 1.0.119.0 | build-time stub | not needed |

Assembly references in the csproj: `System`, `System.Core`, `System.Data`,
`System.Data.DataSetExtensions`, `System.Drawing`, `System.IO.Compression`,
`System.IO.Compression.FileSystem`, `System.Web`, `System.Xml`,
`System.Xml.Linq`, `System.Net.Http`, `Microsoft.CSharp`, plus the two SQLite
refs. Discussion of each in §3.

### 2.3 Build/CI today

`.github/workflows/build-release.yml` runs `windows-latest`, reads the version
from `Properties/AssemblyInfo.cs`, validates the tag does not exist, then
`nuget restore` + MSBuild and attaches a release asset. There is no test job and
no test project at all — a Linux port must add both.

---

## 3. Windows-specific dependency inventory

Method: full reads of the ten largest files plus repo-wide greps for every
category below; all `file:line` references are verified. Counts are
grep-verified *sites*, not lines.

### 3.0 What is **absent** (so nobody goes looking)

* No `System.Windows.Forms` at all — no dialogs, no message boxes.
* **No registry access** (`Microsoft.Win32`, `Registry*`, `HKLM`, `HKCU`: 0 hits).
* **No `Environment.SpecialFolder`** — the data root is the `directory=` CLI
  argument, defaulting to `Environment.CurrentDirectory` (`Program.cs:25-28`).
* **No firewall handling** (`netsh`, `advfirewall`, `INetFwPolicy2`: 0 hits).
  The only firewall reference is prose in `README.md:42` and `UI.html:408`.
* **No console-colour P/Invoke**, no ANSI escapes, no `Console.ForegroundColor`.
* No COM interop in C# (`Setup64.exe` is run as a process, not `ComImport`).
* Only **two** `DllImport`s in the whole codebase (`Tools.cs:29-33`).

### 3.1 P/Invoke (complete: 2 functions)

| Site | Function | Purpose |
|---|---|---|
| `Tools.cs:29-30` | `kernel32!GetConsoleWindow` | HWND used as "is there a real console" flag |
| `Tools.cs:32-33` | `user32!SetForegroundWindow` | bring console to front (`Tools.cs:138-142`) |

Consumed at `MameAO.cs:166`, gating `Console.WindowWidth` reads
(`Tools.cs:178-183`). Trivially replaceable with `Console.IsOutputRedirected`
and a no-op.

### 3.2 Console APIs

| Site | Use | Linux |
|---|---|---|
| `Tools.cs:183` | `Console.WindowWidth` for heading padding | throws/0 when not a TTY → clamp to 80 |
| `MameAO.cs:173` | `Console.Title = …` | no-op |
| `Tools.cs:202` | `Console.ReadKey()` on fatal | throws when stdin is redirected → replace with `ReadLine` guarded by `IsInputRedirected` |

### 3.3 `System.Drawing` (GDI+) — the real UI dependency

| Site | Use |
|---|---|
| `Snap.cs:17-30` | `Size`, `ImageCodecInfo.GetImageDecoders`, JPEG encoder GUID |
| `Snap.cs:312-321` | `Bitmap.LockBits` + `Marshal.Copy` — pixel hash for the snap index |
| `Snap.cs:349-360` | `EncoderParameter(Encoder.Quality, …)` JPEG save |
| `Snap.cs:363-418` | `Graphics.FromImage` thumbnail renderer |
| `Tools.cs:884-905` | `Image.FromFile` / `Bitmap.GetPixel` for `.svg` |
| `PhoneHome.cs:316-320` | snap resize before upload |

Verified on this box: `System.Drawing.Bitmap` does **not** compile on
`net10.0` without `System.Drawing.Common` (CS1069), and that package is
Windows-only. → replace with **SkiaSharp** (MIT, native assets for
`linux-arm64`/`linux-x64`, covers decode/resize/JPEG-quality/pixel access).

### 3.4 `Process.Start` / `ProcessStartInfo` — every external program

Windows binaries (all must be replaced or gated):

| Site | Program | Purpose |
|---|---|---|
| `Tools.cs:261` | `notepad.exe` | dump report text |
| `Tools.cs:751` | `cmd.exe` | run generated `link.bat` (`mklink`) |
| `Tools.cs:794-830` | `access-linker.exe` | build MS Access `.accdb` |
| `Tools.cs:967, 1004` | `C:\Program Files\7-Zip\7z.exe` | `.7z` pack/unpack |
| `MameChdMan.cs:15, 103` | `chdman.exe` | CHD SHA1/verify/info |
| `Mame.cs:13-20` | downloaded `mame*.exe` | 7-Zip **self-extractor**, `-y` |
| `Mame.cs:43, 93` | `mame.exe` | `-listxml`, `-listsoftware`, run |
| `CoreMame.cs:88` | `*b_x64.exe` / `*b_64bit.exe` | Windows-only release asset |
| `CoreHbMame.cs:116-197`, `CoreFbNeo.cs:139-720` | `hbmame.exe`, `fbneo64.exe` | other cores |
| `CorePinballVisual.cs:81-564` | `VPinballX64.exe`, `PinMAME.exe`, `Setup64.exe` | pinball core |
| `BitTorrent.cs:55-232` | `dome-bt.exe` | torrent daemon |
| `SelfUpdate.cs:50, 99, 115` | `mame-ao.exe` | self-replace |
| `Test.cs:77`, `MameAO.cs:820-918` | `{core}.exe` | run/test |

Shell-opens needing `xdg-open`: `MameAO.cs:353` (UI at startup, **must not
fail headless**), `:571` `.ui`, `:579` `.readme`, `:603` `.what`,
`Reports.cs:352, 354`. All are plain `Process.Start(url|file)`.

### 3.5 Symlinks: `mklink` through `cmd.exe`

`Tools.LinkFiles` (`Tools.cs:723-767`) writes a `link.bat` containing one
`mklink "<link>" "<target>"` per pair, prefixes `chcp 65001`, pipes the batch
into `cmd.exe`, and **never checks the exit code**. Only `%` is escaped
(`:737-739`); `&`, `^`, `!` and quotes are not. No `/H` (hardlink) and no `/D`
(dir link) is ever used — file symlinks only.

Detection is a probe, not a setting: `MameAO.cs:198-217` creates
`_LINK_TEST.txt`, links it, and sets `Globals.LinkingEnabled = File.Exists(...)`;
failure prints "You can save a lot of disk space by enabling symbolic links"
(`:214-215`). Placement then links or copies (`Place.cs:607-618`). On Linux the
probe silently fails → **everything is copied** (correct but wasteful), and the
warning is misleading because no privilege is needed.

The README's `secpol` / Developer-Mode instructions (`README.md:116-122`) are
Windows-only; Linux needs `File.CreateSymbolicLink`, which requires nothing.

### 3.6 Hard-coded separators, prefixes and paths

| Site | Detail | Impact |
|---|---|---|
| `HashStore.cs:167-179` | `path.Append(@"\");` builds the real store path `<store>/<aa>\<sha1>` | **critical** — on Linux this is a single filename containing `\`; store writes land in the CWD and lookups diverge |
| `Export.cs:576` | `directory = @"\\?\" + directory` | creates a literal `\\?\…` directory on Linux |
| `Tools.cs:743, 749` | `tempDir.Path + @"\link.bat"`, `chcp 65001` | Windows-only block |
| `MameAO.cs:918` | `-play "tables\{machine}"` | pinball only |
| `Snap.cs:634, 821` | `\\splcal-app\e$\ao-data\snap` | dev-only UNC literals |
| `CoreNoIntro.cs:58` | `C:\tmp\No-Intro` | core init reads a fixed path |
| `CoreFbNeo.cs:789-794`, `CorePinballVisual.cs:764-765` | `C:\GIT\…`, `E:\STORE_ROM` | dev scratch |
| `Export.cs:20, 70`, `Upload.cs:488`, `CoreFbNeo.cs:877`, `Reports.cs:568, 1978`, `PhoneHome.cs:292`, `Operations*.cs:394, 659, 1159, 1311, 1458` | `\` as a **data** separator in composite keys (`"list\name"`) | not a path; must stay consistent, not "fixed" |

`Path.Combine` is used for most real paths; the exceptions above are the ones
that matter.

### 3.7 Case-sensitivity assumptions

| Site | Risk |
|---|---|
| `Cores.cs:229` | `HashSet<string>{".accdb",".json",".sqlite",".xml"}` with default comparer — `.SQLite` would be skipped on Linux |
| `Snap.cs:201, 789` | key = `GetFileNameWithoutExtension(...).ToLower()` — two files differing only by case collide silently |
| `WebServer.cs:144` | route lowercased to emulate Windows matching (fine, but keep it) |
| `Tools.cs:440` | `Path.GetInvalidFileNameChars()` differs per OS → filenames legal on one platform are illegal on the other (relevant to `Export`'s software-name sanitising) |
| everywhere | ROM/software names from MAME XML are lowercase, so zip lookups are consistent **if** the store and placement keys stay lowercase; the port must normalise explicitly rather than rely on the FS |

### 3.8 CRLF

Only two, both wire/archive formats rather than shell assumptions:
`Upload.cs:506` (`\r\n` in an archive.org manifest) and `Export.cs:23`
(`\n` in the zip manifest). No `\r\n` splitting logic anywhere. Safe.

### 3.9 Databases

* **SQLite** via `System.Data.SQLite` 1.0.119 with native `SQLite.Interop.dll`
  for win-x86/x64 (`packages.config:4-5`, `mame-ao.csproj:63,131`). SQL used is
  SQLite-native: `INTEGER PRIMARY KEY` (not `AUTOINCREMENT`), `LIMIT/OFFSET`
  (not `TOP`), `COLLATE NOCASE` indexes, `sqlite_master`, `INSERT OR REPLACE`.
  → `Microsoft.Data.Sqlite` is a near drop-in **except**
  `SQLiteDataAdapter` (`Database.cs:301-311`), which does not exist there;
  replace with `DbDataAdapter`-free `DataTable.Load(IDataReader)`.
* **SQL Server** in 7 files, only for optional admin/telemetry operations:
  `CREATE DATABASE`, `NVARCHAR`, `sqlite_master`→`sys.*`, `SqlBulkCopy`,
  `SET IDENTITY_INSERT`, `TOP`, `SYSDATETIME()`, `CONVERT(...,120)`,
  `CREATE FULLTEXT INDEX`, and `Integrated Security=True` in the documented
  connection string (`README.md:369`). Gate out of the Linux build.
* **MS Access** only via `access-linker.exe` (`Tools.cs:769-832`) — gate out.
* **DataSets in memory** are pervasive but portable (`DataRow.Field<T>` works
  on net10.0, verified).

### 3.10 HTTP stack

| Site | Note |
|---|---|
| `WebServer.cs:117` | `if (HttpListener.IsSupported == false) { … return; }` — historically the Linux killer |
| `WebServer.cs:123-131` | prefixes `http://127.0.0.1:12380/` **and** `http://[::1]:12380/` |
| `MameAO.cs:339-342` | `catch (HttpListenerException)` → "already running" is the *only* single-instance guard |
| `Tools.cs:684`, `Upload.cs:525, 607` | `HttpWebRequest`/`WebRequest` |
| `ArchiveOrgItem.cs:35` | `ServicePointManager … Expect100Continue = false` (Framework-era tuning) |

**Measured on this box (.NET 10, Linux):**

```
HttpListener.IsSupported = True
http://127.0.0.1:12399/   -> OK
http://localhost:12399/   -> OK
http://[::1]:12399/       -> HttpListenerException: Invalid port in prefix
```

So the explorer's assumption ("IsSupported is false on Linux") is **wrong for
.NET 10**, but the port still cannot start the UI unchanged: the **IPv6
prefix throws** and the `try` block adds both, so `StartListener` fails. Fix:
bind `http://127.0.0.1:<port>/` by default, honour `--listen` by binding
`http://*:<port>/`, and drop the `[::1]` prefix. This keeps all 1309 lines of
existing route code and avoids a Kestrel rewrite.

### 3.11 Other non-portabilities

| Site | Issue |
|---|---|
| `MameAO.cs:61` | `TimeZoneInfo.Local.StandardName` → IANA names on Linux, changes telemetry identity |
| `Tools.cs:795` | `Assembly.GetEntryAssembly().Location` — empty under single-file publish; use `AppContext.BaseDirectory` |
| `Tools.cs:811`, `BitTorrent.cs:108` | `AssemblyName.GetAssemblyName(exe)` reads **PE metadata** off a Windows `.exe` — throws on native/foreign binaries |
| `WebServer.cs:102` | `File.ReadAllText("UI.html")` is CWD-relative → must become `AppContext.BaseDirectory` |
| `Program.cs:25` | data root = CWD by default (deliberate: makes `UI.html` resolve) |
| `SelfUpdate.cs:99` | deletes the running `.exe` (legal on Windows, `ETXTBSY` on Linux) |
| `CoreNoIntro.cs:85, 110` | **moves** the user's zip out of the import dir — data-loss pattern to fix while porting |
| `Tools.cs:1279` | temp dir derived from `Path.GetTempFileName() + ".dir"` |

### 3.12 Hardest spots, ranked

1. `HashStore.cs:167-179` — one line, blast radius = the whole asset pipeline.
2. `Tools.cs:723-772` + `MameAO.cs:198-217` — symlink creation + probe.
3. `System.Data.SQLite` → `Microsoft.Data.Sqlite` (`SQLiteDataAdapter` gap).
4. `System.Drawing` → SkiaSharp (pixel-level rewrite).
5. `WebServer` IPv6 prefix + shell-opens + single instance.
6. Windows emulator/helper binaries and the SFX unpack path.
7. `.exe` naming in 7 files + `chdman` eager construction.
8. MSSQL/Access surfaces wired into `ICore` (`Cores.cs:20-40`) — interface
   surgery before any exclusion.

---

## 4. How MAME is located, versioned and launched

### 4.1 Discovery is GitHub-only — the binary is never asked its version

`CoreMame.Get()` (`CoreMame.cs:70-106`):

1. URL = `https://api.github.com/repos/mamedev/mame/releases/latest`, or
   `.../releases/tags/mame{version}` when pinned (`:72-74`).
2. Version = `tag_name.Substring(4)`, so tag `mame0287` → **`0287`** (`:78-79`).
3. If `<root>/mame/<version>/mame.exe` exists → return immediately (`:84-85`).
   **No verification that the binary runs or matches the directory name.**
4. Asset selection: name `EndsWith("b_x64.exe")` **or** `EndsWith("b_64bit.exe")`,
   exactly one must match or it throws (`:87-91`).
5. Download to the version directory, then run it with `-y` to unpack
   (`:101-103` → `Mame.cs:12-32`). It is a 7-Zip **self-extracting EXE**; no
   external `7z.exe` is involved for MAME.

`LatestLocalVersion` is the lexicographic max of subdirectory names that
contain `mame.exe` (`CoreMame.cs:132-152`). Version pinning comes from
`_config.txt` key `MameVersion` (`MameAO.cs:290`), from `.core mame 0287`
(`:797-803`), or ad-hoc `.0287 <machine>` to run an old version against its own
saved state (`:812-827`).

**On a version bump nothing is migrated**: a new directory appears with new
XML/SQLite and an empty `roms/`; assets are re-placed from the shared hash
store on first use (`README.md:448-450`). Old version dirs are never deleted
(this is the documented way to keep old save states).

There is **no `mame -version` probe anywhere in the codebase** (0 hits), so
"database built against version X" is really "directory named X", and a user
who replaces the binary in place gets a silently mismatched database.

### 4.2 Launch

```csharp
if (Settings["Cheats"] == "Yes")     arguments += " -cheat";           // MameAO.cs:883-884
if (Settings["SnapNative"] == "Yes") arguments += " -snapview native"; // :886-887
if (Settings["Window"] == "Yes")     arguments += " -window";          // :889-890
Mame.RunMame(Path.Combine(core.Directory, $"{core.Name}.exe"),
             $"{machine} {software} {arguments}");                     // :894
```

`Mame.RunMame` (`Mame.cs:79-138`) appends `Globals.MameArguments` (free-form
text from `_config.txt`), sets `WorkingDirectory` = the version directory,
`UseShellExecute = false`, redirects stdout/stderr, and reads the exit code.

* **No quoting.** The command line is a space-joined string with a possible
  double space when there is no software name. A `machine` containing shell
  metacharacters is harmless while `UseShellExecute=false`, but arguments
  containing spaces cannot be expressed.
* `software` is not the raw software name: `Cores.GetRequiredMedia`
  (`Cores.cs:589-726`) maps `part/@interface` to the machine's
  `device/instance/@briefname` and returns an option such as `-cart et` or
  `-flop1 foo` (`MameAO.cs:880-881`).
* Argument sources, in priority order: the last `-`-prefixed token on the AO
  command line, the three settings above, then `MameArguments`.

**Flags used anywhere in the codebase** (complete): `-y` (SFX unpack),
`-listxml`, `-listsoftware`, `-cheat`, `-snapview native`, `-window`,
`-verifyroms`, plus free-form `MameArguments`; FBNeo-only `-w`/`-listinfo`;
VPinball-only `-play`/`-extractvbs`; `chdman info|verify` on CHDs.

**Never used**: `-rompath`, `-hashpath`, `-artpath`, `-samplepath`,
`-homepath`, `-inipath`, `-snapname`, `-video`, `-keyboardprovider`,
`-getsoftlist`, `-createconfig`, `-showconfig`. There are zero occurrences of
`-video d3d` or `-keyboardprovider dinput`, i.e. the app never hard-codes a
Windows video backend — good news for the port.

### 4.3 No `mame.ini` is ever generated or read

Grep for `mame.ini` in `source/*.cs`: **zero hits**. MAME runs on defaults
resolved against the CWD (= version directory), which is what produces the
`roms/ samples/ artwork/ hash/ cheat/ snap/ sta/ nvram/ cfg/` layout of §5.5.
The only INI the app writes is FBNeo's `config/fbneo64.ini` (`CoreFbNeo.cs:171`).

### 4.4 Windows-only blockers in this path

| Step | Portable? | Detail |
|---|---|---|
| Release download | **no** | asset filter only matches `*b_x64.exe` / `*b_64bit.exe` (`CoreMame.cs:87-91`) |
| Unpack | **no** | runs a Windows 7z SFX with `-y` (`Mame.cs:12-32`) |
| `.exe` naming | **no** | hard-coded in 7 files (`CoreMame.cs:84,114,140`; `MameAO.cs:820,894`; `Mame.cs:147,174,239`; `Test.cs:77`; `OperationsMameish.cs:37`) |
| `chdman` | **naming** | expected as `<version>/chdman.exe`; constructed eagerly at startup (`MameAO.cs:296`), so its absence aborts initialisation (`MameChdMan.cs:15-18`) |
| `-listxml`/`-listsoftware`/launch flags | **yes** | same flags exist in SDL MAME |
| DB build, XML→SQLite | **yes** | pure managed |
| Snapshots, `.svg` | **yes** | MAME writes `snap/`; `.svg` is a managed PNG→SVG converter (`Tools.cs:869-919`), no potrace needed |

---

## 5. How the MAME database and asset layout are produced

### 5.1 Binary → XML → JSON → SQLite

`Cores.EnableCore` (`Cores.cs:59-205`) is the whole pipeline:

```
core.Get()            download + unpack the MAME build            CoreMame.cs:70-106
core.Xml()            mame -listxml     > <ver>/_machine.xml      Cores.cs:106-127
                      mame -listsoftware > <ver>/_software.xml
                      ReadXML.CombineHashSoftwareLists(...)        ReadXML.cs:133-168
core.SQLiteAo()       XML -> DataSet -> SQLite (ao_info gate)     CoreMame.cs:183-219
core.AllSHA1()        union of rom.sha1 + disk.sha1 -> Globals    CoreMame.cs:233-246
```

* Extraction writes to `<file>.tmp` (UTF-8) then renames; an output shorter
  than 48 bytes is replaced with an empty `<softwarelists/>` (`Mame.cs:34-78`).
* **`hash/` merge**: after `-listsoftware`, MAME's own output is scanned for
  already-emitted `<softwarelist name=…>`, then every `hash/*.xml` in the
  version directory is appended if its list was not emitted (`ReadXML.cs:133-168`).
  This matters because `-listsoftware` only emits lists it can *find*, i.e.
  relative to CWD (§8.4). On Linux this step is where a wrong CWD shows up as
  a truncated software DB rather than an error.
* **No MAME source is required.** The only `github.com/mamedev/mame` URLs are
  hyperlink templates built for reports from `machine/@sourcefile`
  (`OperationsMameish.cs:684-706`) — decoration, never fetched.

### 5.2 Schema

Schema is data-driven: every XML element becomes a table, every attribute or
scalar child a TEXT column, plus an integer `<table>_id` PK and a
`<parent>_id` FK; `condition` children are renamed to `<parent>_condition` and
dropped by the whitelist (`ReadXML.cs:64-131`, whitelists at `:13-40`).

Machine DB `_machine.sqlite`: `mame`, `machine`, `rom`, `disk`, `sample`,
`softwarelist`, `driver`, `input`, `control`, `display`, `feature`, `device`,
`device_ref`, `instance`, plus AO-added columns on `machine`
(`ao_rom_count`, `ao_disk_count`, `ao_softwarelist_count`, `ao_driver_status`,
`ao_input_coins`, `ao_type`, `ao_status`, `ao_year`, `ao_players`,
one boolean per control/display type — `Cores.cs:304-324`) and the single
metadata row `ao_info(ao_info_id=1, assembly_version)` (`Cores.cs:261-271`).

Software DB `_software.sqlite`: `softwarelists`, `softwarelist`, `software`,
`part`, `dataarea`, `rom`, `diskarea`, `disk`, `sharedfeat`, plus AO
`softwarelist_name`/`ao_year` on `software` (`Cores.cs:396-409`).

Indexes: `idx_machine_name`, `idx_machine_description`,
`idx_machine_year_description`, `idx_software_*` (`Database.cs:93-111`,
`Cores.cs:156-183`).

All column types are TEXT except ids; there is no boolean/numeric typing, so
SQLite's dynamic typing means almost all SQL in `Reports.cs` is portable. The
one Windows-flavoured thing is the **`||` concatenation** used in the README's
pinball example (`README.md:92`), which is SQLite-native anyway.

### 5.3 Rebuild gates (three independent ones, no timestamps)

1. **Binary**: version directory contains `mame.exe` → skip download
   (`CoreMame.cs:84-85`). A MAME bump = a directory that does not exist.
2. **XML**: extract only if `_machine.xml` / `_software.xml` is absent
   (`Cores.cs:113-126`).
3. **SQLite**: create only if absent, **plus** an app-version rebuild:
   `if (GetAoMetaDataAssemblyVersion(...) != Globals.AssemblyVersion) → MakeSQLite(overwrite:true)`
   (`CoreMame.cs:194-198`, `Cores.cs:273-288`), which deletes both `.sqlite`
   files and rebuilds from the **existing XML** (`Cores.cs:137-141`).

So: a MAME bump re-extracts XML and rebuilds; a mame-ao bump rebuilds from
cached XML. This is the exact behaviour acceptance test 5 has to reproduce, and
it needs one extra gate on Linux: the **`mame -version` string** must be
recorded in `ao_info` and compared, because a native binary can be replaced in
place without changing the directory name.

### 5.4 HashStore

* Two stores: `_STORE` (ROMs) and `_STORE_DISK` (CHDs), overridable via
  `StorePathRom`/`StorePathDisk` (`MameAO.cs:302-312`).
* Key = SHA1 lowercase hex. ROMs: raw file SHA1 (`Tools.SHA1HexFile`); CHDs:
  the SHA1 **inside the CHD header**, obtained via `chdman info`, plus a full
  `chdman verify` (`MameChdMan.cs:21-62`). So a CHD's store key is not the
  SHA1 of the file — a native `chdman` is mandatory for CHD support.
* Layout: content-addressed, **loose, uncompressed, no extension**:
  `_STORE/<first2>/<sha1>`, built with **literal backslashes**
  (`HashStore.cs:167-179`), which is the single hardest blocker for Linux
  writes (`\` is a legal filename character there, so the "directory" collapses).
* Existence = membership of an in-memory `HashSet<string>` of filenames,
  scanned once at startup (`HashStore.cs:47-56`), updated on `Add` (`:71-97`).
* Dedupe on add; `move:true` for CHDs and temp downloads so nothing is copied
  twice (`Place.cs:473, 559`).
* `.valid rom|disk` re-hashes everything and compares to the filename
  (`HashStore.cs:180-237`).
* `.import` walks a directory (recursing into `.zip`), hashes each file, and
  ingests **only** when the SHA1 is in `Globals.AllSHA1`; otherwise reports
  `Unknown` (`Import.cs:69-88`).

### 5.5 Placement and the on-disk layout

`Place.PlaceAssets` (`Place.cs:27-157`) does acquire-then-place for machine
ROMs, machine disks, software ROMs/disks (including `sharedfeat` dependencies),
then samples, artwork and cheats.

* Machine set = the machine plus recursive `romof` parents and every
  `device_ref` bios/device (`Cores.cs:475-498`, `Place.cs:161-177`).
* Acquisition source is either archive.org (`mame-merged`, `mame-sl`, CHD items
  `MAME_0.225_CHDs_merged` / `mame-software-list-chds-2`, `mame-support`) or
  the local DOME-BT API (`Place.cs:195-207, 249-259, 322-347, 387-408`).
* Placement is **loose files, never zip rewriting**: for each entry, if the
  destination file is absent and the store has the SHA1, copy or symlink the
  loose file into `roms/<machine>/<rom name>` (`.chd` appended for disks)
  (`Place.cs:579-618`).
* Symlink vs copy is a single startup decision: `Globals.LinkingEnabled`,
  probed by creating `_LINK_TEST.txt` through `Tools.LinkFiles`
  (`MameAO.cs:201-217`), which shells out to `cmd.exe` + `mklink`
  (`Tools.cs:723-760`). On Linux this probe fails invisibly → everything is
  copied.

Resulting layout (all relative to the process CWD, which is `<root>` for the
app and `<root>/mame/<version>` for MAME):

```
<root>/mame/<version>/
        mame.exe chdman.exe hash/*.xml
        _machine.xml _software.xml _machine.json _software.json
        _machine.sqlite _software.sqlite
        roms/<machine>/<rom files and *.chd>
        roms/<softwarelist>/<software>/<files and *.chd>
        samples/<sampleof>/*.wav   artwork/<machine>/...   cheat/
        snap/<machine>/*.png   sta/<machine>/*.sta   nvram/ cfg/
<root>/_STORE/<aa>/<sha1>        <root>/_STORE_DISK/<aa>/<sha1>
<root>/_TEMP/ _TEMP/CACHE/ _REPORTS/ _SNAP/ _BT/
<root>/_config.txt _Settings.txt
```

There is no `chds/` and no `software/` directory; MAME finds both through the
default `rompath` (`roms`) because CHDs sit next to their ROMs and software
items are nested under `roms/<list>/<software>/`.

### 5.6 Snapshots and `.svg`

* Snapshots are **MAME's own F12**; the app never passes `-snapname` and never
  screen-grabs. It only adds `-snapview native` when `SnapNative=Yes`
  (`MameAO.cs:886-887`).
* After a run, `Snap.CollectSnaps` picks the largest PNG under
  `snap/<machine>/`, moves it to `_SNAP/<core>/<machine>/`, and optionally
  phone-homes it (`Snap.cs:31-72`, `MameAO.cs:388-398`).
* `.snap <dir>` is a bulk sweep of all version dirs (`Mame.cs:226-287`).
* `.svg` is a **pure managed** PNG→SVG converter (one `<rect>` per pixel,
  `Tools.cs:869-919`). No ImageMagick and no potrace required — the brief's
  assumption is wrong here.

### 5.7 What actually breaks on Linux (this path only)

1. `HashStore.GetFileName` literal `\` → store writes fail (`HashStore.cs:167-179`).
2. `mklink` probe → silent fallback to copying (`Tools.cs:723-760`).
3. `chdman.exe` constructed eagerly at startup → app dies without a CHD tool
   (`MameAO.cs:296`, `MameChdMan.cs:15-18`).
4. `mame.exe` name in 7 files.
5. Release asset filter + SFX unpack (`CoreMame.cs:87-103`).

Everything else in this section — XML parsing, schema, indexes, placement
logic, snaphots, `.svg`, argument assembly — is portable as-is.

---

---

## 6. Exact remote endpoints and auth

### 6.1 archive.org — download side uses a **browser session cookie**, not S3 keys

| Purpose | Endpoint |
|---|---|
| Item metadata | `https://archive.org/metadata/{item}` |
| Whole file | `https://archive.org/download/{item}/{file}` |
| **Member of a zip** (archive.org's zip-member hack) | `https://archive.org/download/{item}/{file}.zip/{member}` — used as `…@LIST@%2f@SOFTWARE@.zip` (`Place.cs:330`) |
| Zip member listing (HTML scrape, for sizes) | `https://archive.org/download/{item}/{file}/` (`ArchiveOrgItem.cs:278-334`) |
| Login | `GET https://archive.org/services/csrf-token`, `POST https://archive.org/services/account/login/` (`:95, 120`) |
| Human page | `https://archive.org/details/{item}` |
| Search (dev tool) | `https://archive.org/advancedsearch.php?...&output=json` |

Auth details, all of which the port must change:

* Credentials are prompted for on the console with `Console.ReadLine()` — the
  **password is echoed** (`ArchiveOrgItem.cs:60-64`).
* The resulting cookie jar is persisted as a plain-text `Cookie:` header string
  at `{root}/_TEMP/CACHE/archive.org-auth-cookie.txt`
  (`ArchiveOrgItem.cs:24, 82, 136-140`), mode 0644 by default, and re-login is
  forced when the file is older than **90 days** (`:40`).
* The cookie is attached to **any** `https://archive.org/` URL by prefix match
  (`Tools.cs:642-643, 688-689`).
* **No environment variables anywhere** — `Environment.GetEnvironmentVariable`
  has 0 hits in both repos. Everything is `_config.txt`.
* Upload side is separate and *does* use S3 keys: `{root}/_api-auth.txt`
  containing `LOW <access>:<secret>`, read in a static constructor with no
  trim, so a trailing newline breaks the header (`Upload.cs:27-32, 532, 610`).

Download mechanics (`Tools.Download`, `Tools.cs:670-721`):

* `HttpWebRequest`, 6-hour timeout, 64 KiB buffer, writes **directly to the
  final filename** with `FileMode.Create`.
* **No `.part` file, no Range/resume, no retry, no checksum.** A dropped
  connection leaves a truncated file that looks complete and is re-fetched from
  zero next time. (CHDs are the exception: the expected SHA1 is recomputed and
  compared, but a mismatch is a warning, not a failure — `Place.cs:462-467`.)
* Zip payloads are extracted to a temp dir and every extracted file is SHA1'd;
  only hashes present in `Globals.AllSHA1` are ingested (`Place.cs:507-564`).
* Every text URL is cached for **3 hours** under `_TEMP/CACHE/<url>`
  (`Tools.cs:593-634`) and failures are **swallowed**, returning stale cache or
  `null` — this is load-bearing behaviour for offline starts.

### 6.2 GitHub — unauthenticated, no rate-limit handling

* `https://api.github.com` + `https://raw.githubusercontent.com/{user}/{repo}`
  (`GitHubRepo.cs:17, 33`).
* `GitHubRepo.Initialize` makes **three** API calls per repo (repo, `releases`,
  `releases/latest`) and keeps only the newest release's assets
  (`GitHubRepo.cs:38-68`). Startup registers five repos (`MameAO.cs:223-229`)
  plus direct calls for mamedev/mame, FBNeo, vpinball — roughly **15
  unauthenticated calls per start** against a 60/h/IP limit.
* **No token, no `Authorization`, no `X-RateLimit-*` handling, no backoff.**
  On 403 the cached-fetch layer returns `null`/stale, so the update check can
  silently report a bogus version (`MameAO.cs:318-326`).
* Asset selection is `EndsWith` matching, and for `.up` it is not by name at
  all: `repo.Assets[repo.Assets.First().Key]` (`SelfUpdate.cs:29`) — whichever
  asset GitHub lists first.

### 6.3 `.up` self-update (Windows-only mechanics)

Download `mame-ao-{tag}.zip` into the root, extract flat into
`{root}/_TEMP/UPDATE`, spawn `{UPDATE}/mame-ao.exe update=<pid>
directory="{root}"` with `UseShellExecute=true`, sleep forever, then the child
kills the parent (`Process.GetProcessById(pid).Kill()`), deletes
`{root}/mame-ao.exe` **while it is the running image** (legal on Windows,
`ETXTBSY` on Linux), copies top-level files only, restarts, exits
(`SelfUpdate.cs:29-133`).

Linux replacement: download the arch-specific tarball, verify its published
SHA-256, unpack into a staging dir, `rename(2)` the new binary over the old
path (allowed while running), then `execve` the new binary with a restart
handshake — no PID-kill dance needed. The `UI.html`/`_styles.css` assets must
also be replaced, since the current zip ships `UI.html`.

---

## 7. dome-bt: spawn mechanism and protocol

### 7.1 Process lifecycle

* Binary: `{root}/_BT/dome-bt.exe` (`BitTorrent.cs:55`), replaced from the
  GitHub asset literally named `dome-bt-{tag}.zip` (`:150-154`). Version is
  read from the downloaded assembly's metadata (`:106-110`).
* Started with `UseShellExecute = true` and **no arguments**, no redirection
  (`:228-240`); the working directory provides all configuration.
* Readiness: poll `GET /api/info` every 2 s, 12 tries, then throw; then poll
  every 4 s until `ready_seconds` appears (`:190-221`).
* Stop: `GET /api/stop`, then `Process.GetProcessById(pid).Kill()` (`:244-262`).
* Core enable: rewrite `_BT/_config.txt` key `cores` (tab-separated) and
  restart (`:329-354`).
* Client base URL `http://localhost:12381`, override `BitTorrentUrl`
  (`BitTorrent.cs:32`).

### 7.2 Wire protocol — HTTP/1.1 + JSON on loopback, no auth, CORS `*`

One-directional: **mame-ao polls dome-bt**; dome-bt never calls back.
Dispatch is reflection over `/api/<name>` → `_api_<name>` (`dome-bt/WebServer.cs:49-72`).

| Endpoint | Contract |
|---|---|
| `GET /api/info` | version, pid, `port_number`, `cores`, `priorities`, `torrent_states`, counters, `ready_seconds`, per-torrent `{name, hash, core, type, version, magnet, file_count, state, bytes_*, peers_*}` |
| `GET /api/files?hash=&priority=` | bare array `{path, priority, length, piece_count, percent_complete}` |
| `GET /api/file?core=&machine=` / `?list=&software=` / `&disk=` | `{path, percent_complete, length, priority, filename, piece_start_index, piece_count, torrent_hash}`; raises that file to `Priority.Highest`; `filename` is an **absolute path inside `_BT/_DOWNLOADS`** |
| `GET /api/start_torrent|stop_torrent|reload_torrent?hash=` | `{message:"OK"}` |
| `GET /api/stop` | `{message:"OK"}` |
| `GET /` | static API doc page |

mame-ao polls `/api/file` every **5 s** until `percent_complete == 100`
(`BitTorrent.cs:407-492`); if progress does not move for `BitTorrentRestartMinutes`
(default 5) it escalates torrent stop/start → reload → full process restart,
unbounded (`:452-483`). It copies the absolute `filename` from dome-bt's
directory into its own temp, then into the hash store (`Place.cs:205-207,
257-259, 507-530`).

### 7.3 What dome-bt actually is

* Engine: **MonoTorrent 3.0.3-beta-0049, a locally patched fork** referenced by
  `HintPath` to `..\monotorrent\…\net472` (`dome-bt.csproj:57-65`). The patch is
  essential: default file priority is `DoNotDownload`, so multi-terabyte
  torrents are advertised but only requested files transfer
  (`dome-bt/README.md:16-33`). Stock MonoTorrent would pull whole sets.
* Torrent source: `https://data.spludlow.co.uk/api/torrents/{core}` and
  `…/{core}.peek`, body **AES-CBC encrypted JSON** with a hard-coded key
  (`dome-bt/BitTorrent.cs:145-162`); each item's `torrent` field is a base64 zip
  containing the `.torrent`. Cached under `_BT/_CACHE/metadata/{hash}.torrent`.
  `PleasureDome.cs` is authoring-only (scrapes magnet pages, emits the JSON) and
  is not used at runtime.
* Peer port: random `49152-65535` on first run, persisted as `port-number` in
  `_config.txt` (`:53-61`); the HTTP port is fixed 12381.
* Files land in `_BT/_DOWNLOADS/<torrent name>/…`; dome-bt never touches
  `_STORE` or `roms/`.
* Windows-isms are presentation/process only: `Console.Title`,
  `Console.WindowWidth/Height`, `SetCursorPosition`, `ReadKey`, a 5-second
  repaint loop, `UseShellExecute`. `HttpListener` on loopback works on Linux.

**Port difficulty: low, ~1-2 days.** The protocol and JSON are portable. The
only real work is replacing the forked MonoTorrent with the `MonoTorrent` 3.x
NuGet package and reproducing the `Priority.Normal → DoNotDownload` default
explicitly, plus removing the full-screen console UI so it survives
`stdout` not being a TTY (systemd).

**Risk to flag:** both the port and any wrapper depend on
`data.spludlow.co.uk` (encrypted torrent payloads, hard-coded key) as the
*magnet source*. If that service is unreachable, the BitTorrent path has no
torrents at all. archive.org remains the fallback, and is the documented
default when BitTorrent is off.

---

## 8. What the installed Linux MAME actually provides (measured, this box)

### 8.1 Host

```
Ubuntu 26.04.1 LTS (Resolute Raccoon), kernel 7.0.0-34-generic
x86_64, 8 vCPU, 7 GiB RAM, 14 GB free on / (95% used)
.NET SDK 10.0.113 (only SDK), runtimes Microsoft.NETCore.App 10.0.13 and
Microsoft.AspNetCore.App 10.0.13
```

**This is not the aarch64 GB10 box described in the brief.** See §10 Q1.

### 8.2 MAME

| Fact | Value |
|---|---|
| `which mame` | `/usr/games/mame` → symlink to `/home/oo/src/mameme/mame/mame` |
| `mame -version` | `0.288 (mame0288-847-g84e1e905e45-dirty)` |
| Provenance | **custom source build** from a git checkout; the distro package is shadowed by the symlink |
| distro packages present | `mame 0.285+dfsg1-1`, `mame-data 0.285+dfsg1-1`, `mame-tools 0.285+dfsg1-1`, `mame-extra 0.250-1build1` |
| distro `hash/` | `/usr/share/games/mame/hash` — **761** XML files (0.285 data) |
| effective `homepath` | `/home/oo/src/mame` (from `/etc/mame/mame.ini` and `~/.mame/mame.ini`) |
| effective `hashpath` | `hash` (relative) → `/home/oo/src/mame/hash` — **783** XML files (0.288 data) |
| `inipath` | `/home/oo/src/mame;$HOME/.mame;.;ini` |
| binary/data version match | **no** — binary 0.288, distro data 0.285; the 0.288 hash set comes from the source tree |

Flatpak is installed; the Flathub app `org.mamedev.MAME` is available at
**0.289, `x86_64` only** (no aarch64 ref), and is **not installed** here.
`hbmame` is not installed anywhere.

### 8.3 Capabilities verified

| Probe | Result |
|---|---|
| `mame -listxml` (all) | **306 MB**, **50 377** machines, **12 s** wall (27 s CPU). Contains `rom` (name/size/crc/sha1/region/offset) and `disk` (name/sha1/region/index) elements — everything the DB build needs |
| `mame -listxml pacman` | works, single machine |
| `mame -listsoftware` | **"No software lists found for this system"** when CWD is `$HOME`; **765** lists when CWD is `/home/oo/src/mame`; works from any CWD with `-hashpath /home/oo/src/mame/hash` |
| `mame -getsoftlist nes` | same CWD sensitivity; works with absolute `-hashpath` |
| software-list XML | `/home/oo/src/mame/hash/nes.xml` — `<softwarelist name="nes">`, `<software>`/`<part>`/`<rom>`/`<disk>` with `sha1`/`crc`/`offset`/`loadflag`; `nes.xml` has 0 `<disk>` entries (CHD lists are separate files) |
| headless launch | `mame apexc -video none -sound none -seconds_to_run 1 -skip_gameinfo` → **exit 0, no output**; `1292apvs` likewise |
| ROM-free machines | **193** machines have zero `<rom>` elements (e.g. `apexc`, `1292apvs`, `a2600`) — enough to prove a real MAME launch with **no copyrighted content** |
| machines needing media | `a2600`, `arb` refuse to boot without a mounted cartridge — pick ROM-free, slot-free machines for smoke tests |
| ROM sets on box | none found under `~/.mame/roms` (tests must either import local files or download with user creds) |

### 8.4 Critical finding: relative MAME paths resolve against the **CWD**

MAME does **not** resolve a relative `hashpath`/`rompath` against `homepath`;
it resolves against the process working directory. Measured:

* from `$HOME` → `mame -listsoftware` reports "No software lists found";
* from `/home/oo/src/mame` → 765 lists;
* from any CWD with `-hashpath /home/oo/src/mame/hash` → works.

This is the hidden contract the Windows port depends on: it never passes any
path flag, it only sets `ProcessStartInfo.WorkingDirectory` to the version
directory (`Mame.cs:91-95`), so `roms/`, `hash/`, `samples/`, `snap/`, `sta/`
all resolve relative to that directory. The same trick does work on Linux MAME
— but it silently degrades to "no software lists" the moment the CWD is wrong
(e.g. a service unit with `WorkingDirectory` unset, or a Flatpak wrapper).
The port will therefore keep the version directory as CWD **and** pass
explicit absolute `-rompath`/`-hashpath`/`-samplepath`/`-artpath`/`-homepath`
values in a generated `mame.ini`, so a wrong CWD cannot corrupt the database
build.

### 8.5 Toolchain / external tools present

```
dotnet 10.0.113   go  cargo  git  qemu-aarch64  docker  flatpak  xdg-open
aria2c  /usr/bin/convert (ImageMagick)  7z  unzip
MISSING: potrace, rqbit, transmission-daemon, hbmame, org.mamedev.MAME flatpak
```

Cross-publishing works: `dotnet publish -r linux-arm64 --self-contained
-p:PublishSingleFile=true` produced a 78 MB `ELF 64-bit LSB pie executable,
ARM aarch64`. **It cannot be executed here**: there is no
`/lib/ld-linux-aarch64.so.1` and no arm64 sysroot, so `qemu-aarch64` binfmt
(which is registered) has nothing to load. Running arm64 artefacts locally needs
`dpkg --add-architecture arm64 && apt install libc6:arm64` (root) or a container
with `--platform linux/arm64`.

---

## 9. Stack recommendation

### 9.1 Decision: port to **.NET 10 (`net10.0`)**, shared core + Linux host

Keep the existing logic. The code is not "Windows-entangled" in the way that
forces a rewrite: there is no registry, no WinForms, no COM, two P/Invokes,
and the overwhelming majority of the 21 kLOC (XML→DataSet→SQLite, hash store,
placement, reports, HTTP routes, snapshot indexing) is plain managed code.
The Windows-ness is concentrated in ~14 files and ~10 specific mechanisms
enumerated in §3.12.

Why `net10.0` and not `net8.0` as the brief suggests: this box has **only**
SDK 10.0.113 and runtime 10.0.13. Targeting net8.0 would mean pulling the
net8 targeting/runtime packs and keeping a second runtime installed for no
benefit. `net10.0` is LTS, matches the rubric's "net8.0 or later", and
cross-publishes to both required RIDs (verified, §8.5). If you later need
net8 compatibility, multi-targeting is a one-line change.

### 9.2 Why not Go or Rust

| Criterion | .NET 10 (recommended) | Go rewrite | Rust rewrite |
|---|---|---|---|
| Reuse of existing logic | ~85 % kept; surgery on ~14 files | ~0 %; 21 kLOC re-derived | ~0 % |
| SQL/DataSet/report layer (2 334 lines in `Reports.cs`) | kept verbatim | full rewrite, easy to drift | full rewrite, easy to drift |
| arm64 + x64 | verified cross-publish, self-contained | excellent | excellent |
| Effort (MAME scope, no pinball) | **7–10 days** | **4–6 weeks** | **6–8 weeks** |
| Risk of behavioural drift | low | high (report SQL, hash-store semantics, DB schema) | high |
| Single static binary | 78–85 MB self-contained (verified) | ~15 MB | ~8 MB |
| Needs runtime on target | no (self-contained) | no | no |

Go/Rust would only win on binary size and cold-start (~50 ms), neither of
which matters for a localhost control panel. They lose on the one thing that
matters: keeping the database/schema/placement semantics identical so existing
asset stores, imports and reports remain valid.

The rubric's escape hatches do not trigger:
* NativeAOT/trimming are **not required** — we publish self-contained,
  single-file, non-trimmed. (`PublishTrimmed` would break `DataSet`,
  reflection-based HTTP dispatch, and Newtonsoft `dynamic`; AOT is not worth
  it for a 78 MB console app.)
* .NET on this box works for both arches (§8.5).

### 9.3 Target architecture

```
src/
  MameAo.Core/            net10.0   no platform APIs
      Mame/              provider chain, version probe, args, ini generation
      Database/          XML -> DataSet -> SQLite (ReadXML, Database, indexes)
      Store/             HashStore, Import, Export, placement planner
      Assets/            archive.org client, resume/retry/checksum, samples, artwork
      Bt/                dome-bt client (HTTP) — unchanged contract
      Reports/           report SQL + HTML (port of Reports.cs)
      Platform/          ILinking, IProcessRunner, IUrlOpener, IImageCodec,
                         ICredentialStore, IClock, IConsoleUi
  MameAo.Cli/             net10.0   console + HttpListener host, dot-commands,
                                    systemd-friendly, UI.html embedded
  MameAo.Platform.Linux/  symlinks, xdg-open, /proc, XDG dirs, chdman/mame discovery
  MameAo.Platform.Windows/net10.0-windows  optional, see §9.8
  MameAo.Bt/              net10.0   dome-bt port (same HTTP API on 12381)
  MameAo.Tests/           xUnit: paths, hash store, placement, resume, args, provider
```

Rules that make the split real:

* `Core` references **no** `System.Drawing`, `System.Data.SqlClient`,
  `Microsoft.Win32.*`, or `Process.Start`.
* Every OS touch goes through one of the five interfaces above; a grep in CI
  fails the build if `Process.Start`, `DirectorySeparatorChar` literals,
  `.exe`, or `\\\\` appear under `MameAo.Core/`.
* `UI.html` and `_styles.css` are **embedded resources**, resolved via
  `AppContext.BaseDirectory` (fixes `WebServer.cs:102`), and still served at
  `/`, `/styles.css`, `/images/*`.

### 9.4 Dependency replacements

| Today | Replacement | Why |
|---|---|---|
| `System.Data.SQLite` + `SQLite.Interop.dll` | `Microsoft.Data.Sqlite` 10.0.x | managed + `SQLitePCLRaw` native assets for both RIDs; only gap is `SQLiteDataAdapter` (`Database.cs:301-311`) → `DataTable.Load(IDataReader)` |
| `System.Drawing` | `SkiaSharp` (+`SkiaSharp.NativeAssets.Linux.NoDependencies`) | MIT; decode/resize/JPEG-quality/pixel access all supported; pure-managed `ImageSharp` is the fallback but has a commercial licence |
| `System.Web.HttpUtility` | `WebUtility.UrlEncode` / `Uri.EscapeDataString` | drop the reference |
| `HttpListener` | **keep** (fix IPv6 prefix only) | verified working on Linux/.NET 10; avoids a 1 309-line Kestrel rewrite |
| `HttpWebRequest` download | `HttpClient` + `Range` resume + `SHA1` verify + retries | brief's Phase 3 requirement |
| forked MonoTorrent (`HintPath`) | `MonoTorrent` 3.x NuGet + explicit `DoNotDownload` defaults | removes the private fork |
| `System.Data.SqlClient`, `access-linker.exe` | removed from Linux build | optional admin features |
| Newtonsoft.Json 13.0.4 | **keep** | 100+ `JObject`/`dynamic` uses; churn has no payoff |

Known wart to track: `Microsoft.Data.Sqlite` 10.0.0 still pulls
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which NuGet flags as **NU1903 / high**
(GHSA-2m69-gcr7-jv3q). Options: pin a fixed `SQLitePCLRaw.bundle_e_sqlite3`
when released, or switch to `SQLitePCLRaw.provider.sqlite3` and use the
system libsqlite3 (both RIDs have it). Decide at implementation time, pin
explicitly, and document — do not ship a silent advisory.

### 9.5 MAME on Linux: provider chain

Resolution order (first hit wins, overridable by `.core`/config/CLI):

1. `--mame-path` / config key `MamePath` / `$MAME`.
2. `PATH` lookup of `mame`; `hbmame` for the HBMAME core.
3. Flatpak: `flatpak info org.mamedev.MAME` → launch through
   `flatpak run --command=mame org.mamedev.MAME …` (wrapper, not a path).
   Note: Flathub publishes **x86_64 only** — on arm64 this provider is
   unavailable by design.
4. Optional build-from-source into the data dir (`make -j`), **off by default**.

Then:

* **Version probe** `mame -version` → `0.288 (mame0288-…)`; parse the leading
  semver and store *both* the version and the raw string in `ao_info` so a
  binary swapped in place is detected (§5.3 gate 4).
* **Hash/software lists**: use the installed binary's own `hash/` (query
  `mame -showconfig` for `hashpath`, resolve against the binary's CWD
  behaviour, then pass it **absolute**). Fall back to
  `https://github.com/mamedev/mame/tree/mame{ver}/hash` only if absent.
  No MAME source required. This is what removes the "download a 300 MB
  Windows exe to get `hash/*.xml`" step.
* **Database**: `mame -listxml` and `mame -listsoftware` with absolute
  `-hashpath` (≈12 s / 306 MB on this box), then the existing XML→SQLite path.
* **Mismatch handling**: if `mame -version` ≠ the version recorded for the
  existing DB, rebuild into `<data>/mame/<ver>/` and log
  `MAME 0.288 (installed) / database 0.285 (rebuilt)` — never crash.
* **HBMAME**: no native Linux build exists (upstream is a Windows Qt binary;
  the `arm64` zips on `sdlmame.lngn.net` are Windows-ARM64, same family as the
  progettosnaps "Arm64 = Windows 10 ARMv8.2" binaries). → core is registered
  but **disabled with a clear message**, per the brief. Verify by downloading
  one zip and running `file(1)` during Phase 2; if it turns out to be ELF,
  lift the restriction.

### 9.6 What is kept, gated, dropped

| Area | Decision |
|---|---|
| MAME core, DB build, hash store, placement, `.import`, `.fetch`, `.test`, `.export`, `.report`, `.list`, `.snap`, `.svg`, `.style`, `.r`, `.ui`, `.dbm/.dbs`, `.valid`, `.softname*`, `.software`, favorites, genres, settings | **port and keep** |
| archive.org downloads (ROM/CHD/software/samples/artwork) | keep, rewritten on `HttpClient` with resume/retry/SHA1 |
| BitTorrent | keep via ported `dome-bt` (§7.3), behind `dome-bt`/`internal` config |
| HBMAME core | registered, **disabled** on Linux with a reason |
| FBNeo core | **dropped** on Linux (Windows-only binary) — no Linux FBNeo integration in scope |
| Visual Pinball core | **dropped** (COM registration, DirectX, `.vbs`, Windows binaries) |
| MS Access export, `.accdb*` | **dropped** (`access-linker.exe`) |
| MS SQL Server export/telemetry admin | **dropped** from the Linux build (kept behind `#if WINDOWS` if Windows parity is wanted) |
| Phone-home upload | kept but **off by default**, documented; it depends on the author's SQL Server |
| Self-update `.up` | reimplemented for Linux tarball + atomic rename + re-exec |

### 9.7 Effort and risk (MAME scope, Linux primary)

| Work package | Days | Risk |
|---|---:|---|
| Repo scaffold, core/platform split, CI skeleton, path + hash-store fixes | 1.5 | low |
| MAME provider chain, version probe, DB rebuild gates, `mame.ini` generation | 1 | medium (Flatpak/cwd quirks) |
| SQLite provider swap + `SQLiteDataAdapter` gap + MSSQL excision | 0.5 | medium |
| Downloads: resume, retry, checksums, credential store, archive.org client | 1 | medium |
| Web host fix, UI serving, dot-command port, `xdg-open` | 1 | low |
| Snap/images on SkiaSharp, `.svg` | 0.5 | low |
| dome-bt port (MonoTorrent 3.x, headless) | 1.5 | **medium-high** (fork behaviour, author's torrent service) |
| `.import`, `.export`, reports port | 1 | low |
| Packaging (tar.gz, systemd user unit), CI both arches, tests | 1 | medium (no local arm64 execution) |
| Acceptance runs, `LINUX.md`, `PORTING_NOTES.md` final | 1 | low |
| **Total** | **~10** | |

Biggest risks, honestly:

1. **arm64 cannot be executed on this box** (§8.5) — arm64 correctness would be
   CI-only unless arm64 libc is installed or a container/QEMU image is used.
2. **dome-bt's torrent source is the author's encrypted API** with a hard-coded
   key (`data.spludlow.co.uk`); if it is unreachable, BitTorrent has no
   torrents and archive.org is the only source.
3. **Report layer volume** (2 334 lines) is tedious but mechanical.
4. **No ROMs on this box and no archive.org credentials** — acceptance tests
   3/7 cannot be completed until you supply them (§10).

### 9.8 Windows

Recommended: one codebase, `MameAo.Platform.Windows` targeting
`net10.0-windows` implementing the same interfaces (Windows MAME download,
`mklink`, `notepad`, shell-open), so the Windows build keeps working on .NET 10
rather than net48. That costs ~2 extra days and a `windows-latest` CI job, and
I **cannot verify it on this machine** beyond compiling.

Cheaper alternative: freeze upstream `mame-ao` v1.185 as the Windows product
(untouched, net48) and maintain the Linux port separately. That is honest but
the two will diverge immediately.

Dropping Windows entirely saves ~2 days and one job; nothing in the Linux
design depends on it.

---

## 10. Decisions needed before Phase 1

**Q1 — Stack.** Approve .NET 10 / shared core / Linux host (§9.1)? Or override
to Go/Rust (~4-8 weeks, §9.2)?

**Q2 — Target hardware.** The brief says the main box is an aarch64 GB10 with
128 GB unified memory; **this box is x86_64, 8 vCPU, 7 GiB RAM, 14 GB free
disk**. Should I (a) build and verify on x86_64 here and make arm64 a
cross-built, CI-verified artifact, or (b) is the GB10 reachable (different
SSH target) for real arm64 runs?

**Q3 — Test prerequisites.** There are no archive.org credentials in the
environment (no `IA_*`/`ARCHIVE*` vars, no `_api-auth.txt`) and no ROMs on the
box. Options: (a) you provide `IA_S3_ACCESS`/`IA_S3_SECRET` (or a cookie) and
free ≥3 GB, so acceptance tests 3 and 7 run for real; (b) I verify with
ROM-free machines (`apexc`, `1292apvs` — confirmed to run headless) plus a
local `.import` fixture, and the download paths stay unit-tested only.
Disk note: 14 GB free (95 % used) is workable for a MAME-only test but not for
CHD/software-list full sets.

**Q4 — Windows parity.** Keep Windows working via `net10.0-windows` (§9.8,
+2 days, CI-only verification), freeze the original v1.185 binary as the
Windows product, or drop Windows?

Two smaller calls I will otherwise take by default (say if you disagree):
`.up` self-update downloads a tarball and atomically replaces the binary
(no PID-kill dance), and I will tighten the web API with a same-origin check
plus a `--token` option, because today any web page can drive `/api/command`.

---

## 11. Phase plan against the acceptance criteria

| Phase | Deliverable | Acceptance |
|---|---|---|
| 1 | core/platform split, XDG dirs, `ln -s` default, `--listen`, `xdg-open`, credential store (env + 0600 file) | build + unit tests |
| 2 | provider chain, `mame -version`, hash/listxml from installed MAME, mismatch messages, HBMAME disabled | tests 1, 2, 5 |
| 3 | `HttpClient` downloads (resume/retry/SHA1), dome-bt port or wrapper, no partial files | tests 6, 7 |
| 4 | `mame.ini` generation with absolute paths, `ProcessStartInfo.ArgumentList`, headless `-video none -sound none` | tests 1, 3, 6 |
| 5 | UI + dot-commands, `.snap`, `.svg`, `.up` for Linux assets | tests 3, 4 |
| 6 | tar.gz both arches, systemd user unit, CI (x64 + arm64) | tests 8 |

Test fixtures: synthetic zips/CHDs built in the test project (no copyrighted
content); ROM-free machines for launch tests; a generated 2-machine XML for DB
tests.


---

## 12. Phase 1 implementation record

.NET 10 (`net10.0`), shared core, Linux host, self-contained single-file
publish. `LINUX.md` is the user-facing document; this section is the engineering
record and the list of deviations.

### 12.1 Projects

| Project | Assembly | Role |
|---|---|---|
| `src/MameAo.Core` | `MameAo.Core` | upstream logic under `Legacy/` plus new `Mame/` (provider, layout, ini, argv) and `Assets/FileDownloader.cs`; also compiled by the Windows CI job |
| `src/MameAo.Cli` | `mame-ao` | Linux host: `--data-dir`/`--config-dir`/`--cache-dir`/`--mame-path`/`--listen`/`--headless`, XDG paths, instance lock, SIGTERM handling, embedded BitTorrent payload |
| `src/MameAo.Platform.Linux` | `MameAo.Platform.Linux` | `UnixFileLinker`, `XdgShellOpener`, `EnvFileCredentialStore`, `LinuxEnvironment`, `LinuxPlatform.Create` |
| `src/MameAo.Bt` | `dome-bt` | dome-bt port (MonoTorrent 3.0.2), published separately and embedded into `mame-ao` at packaging time |
| `tests/MameAo.Tests` | — | xUnit; 86 tests (path layout, locator, ini, argv, hash store, placement, downloader, platform services, listener prefixes) |

### 12.2 MAME discovery and layout

`MameProvider.Get()` resolves in this order: `--mame-path`/`MAME` →
`mame` on `PATH` → Flatpak → (never) a source build. `mame -version` is the only
version source; `mame -showconfig` supplies `homepath`/`hashpath`, from which
`-listxml`/`-listsoftware` are run with absolute `-hashpath` values. A requested
version that does not match the installed binary is an error with the real
version in the message, not a silent second download. HBMAME is registered but
refuses with "no Linux build".

Each MAME version gets `$DATA/mame/<dir>` containing a generated `mame.ini` with
absolute `rompath`/`hashpath`/`cfg_directory`/`nvram_directory`/`state_directory`/
`snapshot_directory` (separator `;` on every platform, as MAME expects), plus
`hash` and `roms` symlinks and `ao_info.txt`. Nothing depends on the process
working directory: MAME is started with an explicit `-inipath` and argv lists
(`ProcessStartInfo.ArgumentList`, never a shell string).

### 12.3 Deviations from the Windows original

1. **Self-update points at this port, not at upstream.** Upstream's tags
   (1.185) and this port's version (0.10) are not comparable, and upstream
   releases are Windows zips, so `.up` would replace the binary with something
   that does not run. It uses `cewatkins/mameoxy` by default
   (`MAME_AO_UPDATE_REPO=owner/repo` follows a fork), expects assets named
   `mame-ao-<version>-linux-<arch>.tar.gz`, compares tags by major.minor
   (`v0.10.0` is version `0.10`), and refreshes `UI.html` next to the
   executable so a new binary never serves an old UI.
2. **The BitTorrent client is embedded, not downloaded.** `pack.sh` publishes
   `dome-bt` for the same RID and embeds it as a resource; the host unpacks it to
   `<cache>/bin/dome-bt` (0755) on first use and exports `MAME_AO_DOME_BT`.
   A missing payload is a warning (archive.org still works), never fatal.
   There is no "install dome-bt from GitHub" path on Linux.
3. **Phone home defaults to `No`.** The original default was `Yes`; a port that
   reports usage data without being asked is not acceptable.
4. **`HttpListener` is IPv4-only on Linux.** The managed listener rejects every
   IPv6 prefix at `Start()` with "Invalid port in prefix". Loopback therefore
   produces a single `http://127.0.0.1:<port>/` prefix, `*` maps to
   `http://*:<port>/`, and an explicit IPv6 `--listen` fails with a message that
   names the default. The dual-stack fallback remains for Windows.
5. **`System.Data.SqlClient` and `System.Drawing.Common` are retained** so the
   shared core compiles unchanged, but MSSQL/Access paths and the Windows-only
   cores throw `PlatformNotSupportedException` with a reason. FBNeo, Visual
   Pinball, MS Access export and MSSQL admin are compiled out of reach, not
   deleted.
6. **`whatsnew.txt` is optional.** It ships with the Windows MAME zip; a native
   install does not have it, so `/api/what` explains that instead of throwing.
7. **Console shell is EOF-safe.** With no stdin (systemd) it says so once and
   sleeps instead of printing the prompt forever.

### 12.4 Downloads

`Assets/FileDownloader` writes `<name>.part` plus a sidecar, resumes with
`Range` only when the server answers 206 with a matching `Content-Range`, retries
408/429/5xx/network/stall with jittered exponential backoff, verifies SHA1 and
size, and renames onto the real name atomically. Nothing partial is ever placed
in the hash store. archive.org uses the browser session cookie as before;
credentials come from `IA_USERNAME`/`IA_PASSWORD`, then `MAMEAO_*`, then a 0600
file, and are never logged.

### 12.5 Packaging

`packaging/pack.sh <rid> <version>` publishes both executables, fails unless the
result is single-file (no neighbouring DLLs/native libraries), and writes
`dist/mame-ao-<version>-<rid>.tar.gz` with `mame-ao`, `UI.html`, `LINUX.md` and
`mame-ao.service` (systemd **user** unit, `--headless`, `KillMode=mixed`).
`.github/workflows/ci.yml` runs `dotnet test` on x64, packages and smoke-tests
both `linux-x64` and `linux-arm64` on native runners, compile-checks the shared
core on Windows, and publishes release assets on `v*` tags.

---

## 13. Verification record

Environment: x86_64, .NET SDK 10.0.113, `/usr/games/mame` 0.288
(`mame0288-847-g84e1e905e45-dirty`), `/usr/bin/chdman` 0.285.

| Check | Result |
|---|---|
| Fresh data directory: linked core dir, `_machine.xml` 320 MB, `_software.xml` 101 MB, both SQLite databases built, SHA1 load, genres | pass (`.run2`) |
| Restart on the same directory | no re-extraction, no rebuild; only SHA1 load + genres |
| Web UI served, `/api/what`, `/api/machines?profile=arcade` (12 220) | pass |
| `.list`, `.snap` (real PNGs moved and reported), `.import` (synthetic zip, reported "Unknown" as designed), `.up` (unconfigured message) | pass |
| Headless launch of `1292apvs` and `apexc` with `-seconds_to_run 5` | both reach "Average speed 100.00%" and exit 0 |
| Single-file release tarball: `--version`, unpack embedded `dome-bt`, `.bt` starts it, API on 12381 | pass |
| `dotnet test` | 86 passed |
| `dotnet build MameAo.slnx` | 0 errors |

Not proven here:

* archive.org fetch-and-place with real credentials (credentials were never
  supplied; `.fetch` would run into 401/403). Unit tests cover the downloader and
  placement, and `.import` exercises the hash store, but the acceptance
  criterion that needs an actual download is still open.
* linux-arm64 execution: no aarch64 loader on this box. The artifact is built
  and smoke-tested by CI on an arm64 runner instead.
* A second real MAME version: the rebuild gate was exercised with a wrapper that
  reports `0.289` (`.run3`), which creates `mame/0289` and rebuilds; the MAME
  binary itself is unchanged.
* `.up` against a published release: the release assets did not exist until the
  first tag was pushed, so the update path was checked against the release
  workflow and the asset naming rather than by replacing a running binary.

### 13.1 Published artefacts

Tagging `v0.10.0` runs the release workflow, which publishes
`mame-ao-0.10.0-linux-x64.tar.gz` and `mame-ao-0.10.0-linux-arm64.tar.gz`.
CI builds both on native runners (x64 and arm64), smoke-tests the packed
binary (`--version`, `--help`), and uploads the tarballs as release assets.
