#!/usr/bin/env bash
#
# Headless acceptance checks against a running mame-ao.
#
#   BASE=http://127.0.0.1:12380 ./tests/acceptance-smoke.sh
#
# It only uses the web API, so it works over ssh with no display. Steps that
# need archive.org credentials (fetch + place) are skipped and reported.
#
set -uo pipefail

BASE="${BASE:-http://127.0.0.1:12380}"
MACHINE="${MACHINE:-1292apvs}"
# Headless MAME never exits on its own; -seconds_to_run gives the test an end.
MACHINE_ARGS="${MACHINE_ARGS:--seconds_to_run 5}"
SNAP_DIR="${SNAP_DIR:-}"
pass=0
fail=0

say() { printf '\n== %s\n' "$*"; }
ok() { printf '   ok   %s\n' "$*"; pass=$((pass + 1)); }
bad() { printf '   FAIL %s\n' "$*"; fail=$((fail + 1)); }

get() { curl -sS -m "${2:-600}" "$BASE$1"; }

wait_idle() {
	# The worker runs one command at a time; api/status is the only signal.
	for _ in $(seq 1 "${1:-150}"); do
		local status
		status=$(get /api/status 20) || status=""
		if [[ "$status" == *'"busy": false'* ]]; then
			return 0
		fi
		sleep 2
	done
	return 1
}

command_line() {
	# command_line <shell line> <timeout>; URL-encodes spaces as %20
	local encoded="${1// /%20}"
	shift
	get "/api/command?line=$encoded" "${1:-120}"
}

say "server identity"
body=$(get /api/what 30) || body=""
if [[ -n "$body" && "$body" != *'"status": 500'* ]]; then
	echo "$body" | head -5
	ok "/api/what answered"
else
	bad "/api/what: ${body:0:200}"
fi

say "UI is served"
code=$(curl -sS -m 30 -o /dev/null -w '%{http_code}' "$BASE/")
[[ "$code" == "200" ]] && ok "GET / -> 200" || bad "GET / -> $code"

say "machine list"
body=$(get "/api/machines?profile=arcade&offset=0&count=3" 60) || body=""
if [[ "$body" == *'"total"'* ]]; then
	ok "machine query: $(echo "$body" | grep -m1 '"total"' | tr -d ' ,')"
else
	bad "machine query: ${body:0:200}"
fi

say "Dot command .list"
wait_idle 30 || bad "worker was busy before .list"
body=$(command_line ".list" 120) || body=""
[[ "$body" == *'"message": "OK"'* ]] && ok ".list accepted" || bad ".list: ${body:0:200}"
wait_idle 120 && ok ".list finished" || bad ".list did not finish"

say "Launch ROM-less machine $MACHINE (headless, $MACHINE_ARGS)"
body=$(command_line "$MACHINE $MACHINE_ARGS" 120) || body=""
[[ "$body" == *'"message": "OK"'* ]] && ok "launch queued" || bad "launch: ${body:0:200}"
if wait_idle 150; then
	ok "MAME exited, worker idle"
else
	bad "worker still busy after launch"
fi

if [[ -n "$SNAP_DIR" ]]; then
	say "Collect snapshots"
	body=$(command_line ".snap $SNAP_DIR" 300) || body=""
	[[ "$body" == *'"message": "OK"'* ]] && ok ".snap accepted" || bad ".snap: ${body:0:200}"
	wait_idle 60 || true
fi

if [[ -n "${IA_USERNAME:-}" && -n "${IA_PASSWORD:-}" ]]; then
	say "Fetch and place (credentials present)"
	body=$(command_line ".fetch MR" 600) || body=""
	[[ "$body" == *'"message": "OK"'* ]] && ok ".fetch MR accepted" || bad ".fetch MR: ${body:0:200}"
	wait_idle 900 && ok ".fetch finished" || bad ".fetch did not finish"
else
	say "Fetch and place: SKIPPED (no IA_USERNAME/IA_PASSWORD)"
fi

printf '\n%d passed, %d failed\n' "$pass" "$fail"
[[ "$fail" -eq 0 ]]
