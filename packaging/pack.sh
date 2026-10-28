#!/usr/bin/env bash
#
# Build a release tarball: dist/mame-ao-<version>-<rid>.tar.gz
#
#   ./packaging/pack.sh linux-x64
#   ./packaging/pack.sh linux-arm64 0.10.0
#
# The tarball contains one executable (mame-ao) with the BitTorrent client
# embedded, plus UI.html, LINUX.md and the systemd user unit. No runtime is
# needed on the target: the publish is self-contained and single-file.
#
set -euo pipefail

rid="${1:-}"
version="${2:-0.10.0}"

if [[ "$rid" != "linux-x64" && "$rid" != "linux-arm64" ]]; then
	echo "usage: $0 {linux-x64|linux-arm64} [version]" >&2
	exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"

cleanup() { rm -rf -- "$work"; }
trap cleanup EXIT

publish=(-c Release -r "$rid" --self-contained true
	-p:PublishSingleFile=true
	-p:PublishTrimmed=false
	-p:PublishReadyToRun=false
	-p:IncludeNativeLibrariesForSelfExtract=true
	-p:DebugType=none
	-p:GenerateDocumentationFile=false
	--nologo -v quiet)

echo "==> publishing dome-bt ($rid)"
dotnet publish "$root/src/MameAo.Bt/MameAo.Bt.csproj" "${publish[@]}" -o "$work/bt"

echo "==> publishing mame-ao ($rid) with dome-bt embedded"
dotnet publish "$root/src/MameAo.Cli/MameAo.Cli.csproj" "${publish[@]}" \
	-p:DomeBtBinary="$work/bt/dome-bt" -o "$work/app"

stage="$work/mame-ao-$version-$rid"
mkdir -p "$stage"

install -m 0755 "$work/app/mame-ao" "$stage/mame-ao"
install -m 0644 "$root/src/MameAo.Cli/UI.html" "$stage/UI.html"
install -m 0644 "$root/LINUX.md" "$stage/LINUX.md"
install -m 0644 "$root/packaging/mame-ao.service" "$stage/mame-ao.service"

# Anything else the publish produced is unexpected for a single-file build; a
# stray DLL means the publish silently fell back to a framework-dependent one.
extras=$(find "$work/app" -maxdepth 1 -type f ! -name 'mame-ao' ! -name 'UI.html' | wc -l)
if [[ "$extras" -ne 0 ]]; then
	echo "!! publish produced extra files, not a single-file build:" >&2
	find "$work/app" -maxdepth 1 -type f ! -name 'mame-ao' ! -name 'UI.html' -printf '   %f\n' >&2
	exit 1
fi

mkdir -p "$root/dist"
archive="$root/dist/mame-ao-$version-$rid.tar.gz"
tar -C "$work" -czf "$archive" "mame-ao-$version-$rid"
(cd "$root/dist" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")

echo
echo "==> $archive"
ls -lh "$archive"
cat "$archive.sha256"
