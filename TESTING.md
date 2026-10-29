# Testing mame-ao on Linux

Two parts: a scripted smoke test that checks the machinery, and a manual pass
through the UI that checks the experience.

---

## 0. Build and run

```sh
cd ~/src/mameoxy
dotnet build MameAo.slnx -c Release
dotnet test tests/MameAo.Tests/MameAo.Tests.csproj -c Release
```

Run it (this is what you want day to day):

```sh
./src/MameAo.Cli/bin/Release/net10.0/mame-ao
```

It opens <http://127.0.0.1:12380/> in a browser. Over ssh, or without a
display:

```sh
./src/MameAo.Cli/bin/Release/net10.0/mame-ao --headless
```

Useful variants:

| Command | Effect |
|---|---|
| `--data-dir DIR` | separate data set (e.g. a fresh one to test first-run) |
| `--mame-path /usr/games/mame` | pin the MAME binary |
| `--listen 127.0.0.1:12380` | change the UI port (default) |
| `--listen 0.0.0.0:12380` | expose the UI to the network (only if you want that) |
| `--headless` | no browser, MAME gets `-video none -sound none` |

Stopping: `Ctrl+C`, or `pkill -f mame-ao`. A second instance on the same data
directory refuses to start ("Another mame-ao is already using this data
directory").

---

## 1. Scripted smoke test

With the program running:

```sh
BASE=http://127.0.0.1:12380 ./tests/acceptance-smoke.sh
```

It checks: the API answers, the UI is served, the machine list is populated,
`.list` runs, a ROM-less machine launches and exits under MAME, `.snap`
collects files, and it tells you whether the archive.org steps were skipped
because there are no credentials. Expected output ends with `8 passed, 0 failed`.

The one thing it cannot check on its own is a real download — it needs
credentials (see below).

---

## 2. First run on a clean data directory

```sh
./src/MameAo.Cli/bin/Release/net10.0/mame-ao --data-dir /tmp/ao-fresh --headless
```

Watch the console. You should see, in order:

1. `Symbolic links are enabled, saving disk space.`
2. `MAME <version> (...) (found on PATH as /usr/games/mame) -> /tmp/ao-fresh/mame/0285`
3. `Extract machine XML ...done` (a few minutes, ~320 MB)
4. `Extract software XML and combine ...done` (~110 MB)
5. `Convert machine XML to SQLite ...done`
6. `Convert software XML to SQLite ...done`
7. `Load all database SHA1 ...done`
8. `Loading Genres ...done`
9. `Loading Hash Store .../\_STORE ...done`
10. `Remote Listener ready for commands`

Then check what it produced:

```sh
ls -l /tmp/ao-fresh/mame/0285/            # mame.ini, _machine.sqlite, _software.sqlite, symlinks
cat /tmp/ao-fresh/mame/0285/mame.ini       # every path must be absolute
ls -l /tmp/ao-fresh/mame/0285/hash        # symlink, not a copy
```

`mame.ini` is generated per MAME version and must contain absolute paths, for
example:

```ini
hash_directory             /tmp/ao-fresh/mame/0285/hash
rompath                    /tmp/ao-fresh/mame/0285/roms
snapshot_directory         /tmp/ao-fresh/mame/0288/snap
cfg_directory              /tmp/ao-fresh/mame/0285/cfg
```

**Restart and confirm nothing is rebuilt.** Stop the program, start it again
with the same `--data-dir`. The second start must **not** print "Extract machine
XML" or "Convert ... to SQLite" — only the SHA1 load and genres. That is the
"idempotent restart" requirement: the databases are keyed on the MAME version
directory, not on time.

**Version bump.** Point it at a different MAME version and the databases are
rebuilt in a new directory:

```sh
./src/MameAo.Cli/bin/Release/net10.0/mame-ao --data-dir /tmp/ao-fresh --mame-path /path/to/other/mame
```

You should see a new `mame/<other version>/` directory being built from scratch.

---

## 3. The UI, by hand

Open <http://127.0.0.1:12380/>.

| What to do | What you should see |
|---|---|
| Machine list loads | rows of machines; the count matches `/api/machines` |
| Click a machine name | the console shows `Remote command recieved` with `<name>@mame` — **not** `<name>@undefined` |
| Machine with ROMs you do not have | a message about downloading assets, not a crash |
| ROM-less machine (e.g. `apexc`) | MAME starts; with `--headless` it exits by itself |
| "Snapshots" / `.snap` | files appear in the target directory |
| Settings page | options save; `PhoneHome` is `No` by default |

If you ever see `<name>@undefined`, `/api/info` failed — check the console for
the API error.

---

## 4. Command line inside the program

Type these at the `MAME 0285>` prompt (or send them as
`curl 'http://127.0.0.1:12380/api/command?line=...'`).

| Command | Purpose |
|---|---|
| `.list` | list every machine MAME knows about |
| `.import DIR` | import ROMs/CHDs from a directory into the hash store |
| `.snap DIR` | collect snapshots MAME wrote into `DIR` (created if missing) |
| `.valid rom` / `.valid disk` | check the hash store for bad files |
| `.fetch MR` / `.fetch *` | download assets (needs archive.org credentials or BitTorrent) |
| `.bt` | start the BitTorrent client |
| `.creds` | enter archive.org credentials interactively |
| `.what` | show what the installed MAME is |
| `.up` | self-update (needs `MAME_AO_UPDATE_REPO`) |
| `apexc` | launch a machine |
| `apexc -seconds_to_run 5` | launch and exit after 5 emulated seconds (headless testing) |

---

## 5. archive.org credentials

Downloads that need an account read `IA_USERNAME` and `IA_PASSWORD`.

```sh
export IA_USERNAME='you@example.com'
export IA_PASSWORD='...'
```

or once, in a file with tight permissions:

```sh
install -m 0600 /dev/null ~/.config/mame-ao/credentials
printf 'IA_USERNAME=you@example.com\nIA_PASSWORD=...\n' >> ~/.config/mame-ao/credentials
```

The values are never printed, logged, or put into error messages. After a
successful sign-in the session cookie is cached in
`~/.local/share/mame-ao/_TEMP/CACHE/archive.org-auth-cookie.txt`.

You can also type them at the `MAME 0285>` prompt with `.creds` — that is
interactive and only works on a terminal.

**Test it end to end:**

```sh
curl 'http://127.0.0.1:12380/api/command?line=.fetch%20MR'
```

Then watch the console: files download into the hash store, and the UI shows
them as available for the machines that need them. A ROM in the store is
symlinked into the MAME ROM directory (no copy, no extra disk).

Without credentials, anonymous downloads still work; anything that needs a
login fails with a 401/403 from archive.org and the UI says so.

---

## 6. BitTorrent

The client is `dome-bt`. In a release tarball it is embedded in `mame-ao` and
unpacked into `~/.cache/mame-ao/bin/dome-bt` on first use. In a plain
`dotnet build` it sits next to the executable
(`src/MameAo.Cli/bin/Release/net10.0/dome-bt`).

```sh
curl 'http://127.0.0.1:12380/api/command?line=.bt'
curl -s http://127.0.0.1:12381/api/info | head    # the client answers
```

If the client is missing, the program says so and keeps using archive.org.
Point it elsewhere with `MAME_AO_DOME_BT=/path/to/dome-bt`.

---

## 7. Failure cases worth checking once

| Situation | How to force it | Expected message |
|---|---|---|
| No MAME | `--mame-path /nonexistent/mame` | `MAME not found at '/nonexistent/mame'.` plus the list of places it looked |
| No credentials | unset `IA_*` | download attempts fail with the archive.org status, program keeps running |
| Offline | `unshare -n` or unplug | `Could not check ... for a new mame-ao release (offline or GitHub rate limited).` |
| IPv6 listen address | `--listen 'http://[::1]:12380'` | `HttpListener cannot bind an IPv6 address on this platform` and the IPv4 default |
| Two instances | start a second one on the same data dir | `Another mame-ao is already using this data directory` |
| Port in use | run two on the same port, different data dirs | `MAME-AO is already running` |
| Disk full | fill the filesystem | the download fails, the partial file is kept (not renamed into place), the error names the path |

---

## 8. Automated tests

```sh
dotnet test tests/MameAo.Tests/MameAo.Tests.csproj -c Release
```

86 tests covering: MAME discovery and version parsing, the per-version core
layout and generated `mame.ini`, argv splitting, the hash stores, asset
placement and symlinking, the resumable downloader (against a local HTTP
server), the XDG paths, the credential store, the file linker, the shell
opener and the listener prefixes.

To run one group:

```sh
dotnet test tests/MameAo.Tests/MameAo.Tests.csproj --filter "FullyQualifiedName~DownloadTests"
```

---

## 9. Running as a service

```sh
install -m 0644 packaging/mame-ao.service ~/.config/systemd/user/mame-ao.service
systemctl --user daemon-reload
systemctl --user enable --now mame-ao
journalctl --user -u mame-ao -f
```

The unit runs `--headless`; the UI is on 12380. `SIGTERM` shuts it down
cleanly.

---

## 10. What cannot be tested here

* **linux-arm64** — no arm64 machine or emulator available; CI builds and
  smoke-tests the arm64 artifact on a native runner.
* **A real MAME version upgrade** — only one MAME build is installed, so the
  "new version" path was exercised with a wrapper that reports a different
  version number.
* **Windows** — the shared core compiles for Windows in CI, but there is no
  Windows host for this port.
