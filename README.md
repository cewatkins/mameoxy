# mame-ao — Linux port

A native Linux port of [mame-ao](https://github.com/sam-ludlow/mame-ao), the
MAME front end with a web UI, software-list placement, archive.org downloads
and BitTorrent support.

No Wine, no Mono, no Windows runtime: a .NET 10 program that drives the MAME
you already have installed.

```
mame-ao                  # start, open the UI
mame-ao --headless       # no browser, MAME runs with -video none -sound none
```

- **[LINUX.md](LINUX.md)** — install, configure, credentials, systemd, what is
  not available on Linux.
- **[TESTING.md](TESTING.md)** — how to test it, step by step.
- **[PORTING_NOTES.md](PORTING_NOTES.md)** — the engineering log: what the
  Windows original does, what changed, and how each part was verified.

## Status

| | |
|---|---|
| Platform | Linux, `linux-x64` and `linux-arm64`, self-contained single file |
| Runtime | .NET 10 (bundled in the release tarball) |
| MAME | the one you have installed; probed with `mame -version` |
| UI | the original `UI.html`, served by the program instead of a WebView |

Works: machine/software databases built from the installed MAME, launching
machines, hash-store placement with symlinks, archive.org downloads, the
BitTorrent client, snapshots, `.import`, `.fetch`, dot-commands, the web API.

Not on Linux: HBMAME, FBNeo, Visual Pinball, MS Access export, SQL Server
admin/export. Each refuses with a message that says why.

## Build

```sh
dotnet build MameAo.slnx -c Release
dotnet test tests/MameAo.Tests/MameAo.Tests.csproj -c Release
./packaging/pack.sh linux-x64        # or linux-arm64
```

`pack.sh` writes `dist/mame-ao-<version>-<rid>.tar.gz` with a single
executable, `UI.html`, `LINUX.md` and a systemd user unit.

## Licence

MIT. This is a derivative work of [mame-ao](https://github.com/sam-ludlow/mame-ao)
and [dome-bt](https://github.com/sam-ludlow/dome-bt) by Sam Ludlow; their
notices are preserved in [LICENSE](LICENSE).
