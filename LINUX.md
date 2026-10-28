# mame-ao on Linux

A native Linux port of [mame-ao](https://github.com/sam-ludlow/mame-ao). No Wine,
no Mono, no Windows runtime: it is a .NET 10 console program that drives the
MAME you already have installed.

Everything works through the same web UI as the original, so the UI is opened
in a browser instead of a WebView window.

```
mame-ao                  # start, print the data directory status, open the UI
mame-ao --headless       # no browser, MAME runs with -video none -sound none
mame-ao --help
```

## Requirements

| | |
|---|---|
| .NET | nothing, the release tarball is self-contained |
| MAME | a MAME binary with `chdman`; 0.2xx is fine, it is probed with `mame -version` |
| Disk | ~1 GB for the machine/software databases plus whatever ROMs you fetch |

MAME is found in this order:

1. `--mame-path /path/to/mame`
2. `$MAME`
3. `mame` on `PATH`
4. the Flatpak installation (`org.mamedev.MAME`, wrapper on `PATH` or in
   `/var/lib/flatpak/exports/bin`)
5. **not** built from source; see [Building MAME](#building-mame) if you want it

`chdman` is looked up next to the MAME binary, then on `PATH`. A version
difference between MAME and chdman is reported but only matters for CHD hashing.

MAME does not have to be the newest release. The version reported by
`mame -version` decides the directory the databases live in, so upgrading MAME
creates a new version directory and rebuilds the databases once.

## Where things go

| Purpose | Default | Override |
|---|---|---|
| Data (databases, ROMs, snapshots) | `$XDG_DATA_HOME/mame-ao` → `~/.local/share/mame-ao` | `--data-dir`, `MAMEAO_DATA_DIR` |
| Config (credentials, lock, settings) | `$XDG_CONFIG_HOME/mame-ao` → `~/.config/mame-ao` | `--config-dir`, `MAMEAO_CONFIG_DIR` |
| Cache (extracted client, downloads) | `$XDG_CACHE_HOME/mame-ao` → `~/.cache/mame-ao` | `--cache-dir`, `MAMEAO_CACHE_DIR` |

Inside the data directory:

```
mame/0.288/            generated mame.ini, hash/ and roms/ symlinks, databases
_STORE/                hash store (ROMs by SHA1, symlinked into place)
_STORE_DISK/           hash store for CHDs
_BT/                   BitTorrent client working directory
_TEMP/, _REPORTS/      scratch and generated reports
_Settings.txt          UI settings (same format as the original)
```

`_Settings.txt` is the original settings file; it is not in the config
directory because the original program keeps it with the data it describes.

## archive.org credentials

Downloads that need an account read `IA_USERNAME` and `IA_PASSWORD`:

```sh
export IA_USERNAME='you@example.com'
export IA_PASSWORD='...'
```

or write them once to the credentials file:

```sh
install -m 0600 /dev/null ~/.config/mame-ao/credentials
printf 'IA_USERNAME=you@example.com\nIA_PASSWORD=...\n' >> ~/.config/mame-ao/credentials
```

Values are never logged or included in error messages. Without credentials the
program still starts and anonymous downloads still work; items that require
login report `401`/`403` from archive.org.

## Running under systemd (user unit)

The tarball contains `mame-ao.service`:

```sh
mkdir -p ~/.local/bin
tar -xzf mame-ao-0.10.0-linux-x64.tar.gz -C ~/.local/bin --strip-components=1 \
    mame-ao-0.10.0-linux-x64/mame-ao
install -m 0644 mame-ao-0.10.0-linux-x64/mame-ao.service ~/.config/systemd/user/mame-ao.service
systemctl --user daemon-reload
systemctl --user enable --now mame-ao
journalctl --user -u mame-ao -f
```

The unit runs `--headless`, so nothing tries to open a browser and MAME is
launched with `-video none -sound none`. The UI is then at
`http://127.0.0.1:12380/` (remote access: `--listen 0.0.0.0:12380`, which is why
the default is loopback only).

## Headless operation

`--headless` (also implied when there is no `DISPLAY`/`WAYLAND_DISPLAY`):

* no browser is started, the URL is printed instead
* MAME gets `-video none -sound none`, so ROM and CHD checks still happen but
  no window is created
* snapshots still work: the UI's `.snap` command writes to the snapshot
  directory under the data directory
* `xdg-open` failures are not fatal

Everything in the UI works headless except watching a game. Launching a game
without a display is possible (`-video none`) but pointless outside of testing.

## BitTorrent

The CHD download path can use BitTorrent instead of archive.org. The client
(`dome-bt`, a port of the original .NET client) is embedded in the `mame-ao`
executable and unpacked to the cache directory on first use, so a release is a
single file.

If the binary cannot be unpacked (read-only cache, exotic architecture), the
program says so and falls back to archive.org. To use a client you built
yourself:

```sh
export MAME_AO_DOME_BT=/path/to/dome-bt
```

The client listens on `127.0.0.1:12381`; `MAMEAO_BT_HTTP_PORT` moves it.

## Self update (`.up`)

`.up` replaces the running executable with the tarball for the current
architecture and re-executes it in place, keeping the PID (so a systemd unit
does not restart).

The upstream releases are Windows builds, so this port does **not** point at
them by default. Set the repository that publishes your Linux tarballs:

```sh
export MAME_AO_UPDATE_REPO=owner/repo       # releases must contain
                                            # mame-ao-linux-x64.tar.gz /
                                            # mame-ao-linux-arm64.tar.gz
```

Without it, `.up` explains that self-update is not configured and does nothing
else. Installing from the tarball with your own package manager is the
supported path.

## Not available on Linux

These were in the Windows program and are refused with a message that says why:

| Feature | Why |
|---|---|
| HBMAME | no Linux build of the binaries the original used |
| FBNeo core | ships as a Windows DLL set |
| Visual Pinball core | Windows only |
| MS Access export (`.accdb`, `.accdbxml`) | needs ACE/Jet, not available |
| SQL Server admin/export | needs the Windows SQL Server tooling |

They are still compiled in (the shared core keeps building for Windows), but the
Linux host throws `PlatformNotSupportedException` with the reason instead of
failing obscurely.

Phone-home statistics exist but are off unless enabled in the settings.

## Building from source

```sh
dotnet build mameoxy.slnx
dotnet test tests/MameAo.Tests/MameAo.Tests.csproj
./packaging/pack.sh linux-x64        # or linux-arm64; needs the matching runtime pack
```

`pack.sh` publishes `dome-bt`, embeds it into `mame-ao`, and writes
`dist/mame-ao-<version>-<rid>.tar.gz` plus a `.sha256`.

### Building MAME

Not needed, and off by default. If you want a matching MAME + chdman:

```sh
git clone --depth 1 https://github.com/mamedev/mame
cd mame && make -j"$(nproc)" SUBTARGET=native
sudo make install        # or copy mame/chdman into ~/.local/bin
```

## Troubleshooting

**`MAME binary not found`** — pass `--mame-path`, or set `MAME`. Flatpak MAME
must expose its wrapper on `PATH`; running `flatpak run org.mamedev.MAME -version`
should work first.

**`Another mame-ao is already using this data directory`** — the lock file is
`~/.config/mame-ao/mame-ao.lock`. A crashed instance releases it on exit; if not,
check `pgrep -a mame-ao`.

**`HttpListener cannot bind an IPv6 address on this platform`** — the managed
listener on Linux only supports IPv4 prefixes. Use `--listen 127.0.0.1:12380`
(the default) or `--listen 0.0.0.0:12380`.

**Downloads resume from nothing** — partial downloads are `<name>.part` with a
`.part.json` sidecar; they are resumed unless the checksum or size disagrees,
in which case they restart. Nothing partial is ever placed into the hash store.

**Version bump rebuilds the databases** — expected: the databases are keyed on
the MAME version directory, and the machine/software XML is re-extracted when
MAME reports a different version. The build takes a few minutes and is skipped
on the next start.
