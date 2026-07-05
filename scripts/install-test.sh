#!/usr/bin/env bash
# Clean-machine global-tool install test (brief M3 / AC1). Packs ttr, installs it from a LOCAL feed into an
# isolated tool-path (only the .NET 10 SDK is assumed present — no repo build outputs on PATH), then runs the
# INSTALLED tool against a copy of the fixture matrix and asserts discovery AND a run work from the installed
# location — re-proving POC-1 "Strategy A" (SDK-resolved vstest.console) from a global-tool install path, and
# the exit codes (1 on the mixed matrix's deliberate failures, 0 on the all-pass project).
#
# Usage: install-test.sh [<nupkg-dir>]   (packs into a temp feed if no dir given)
# Requirements: tmux, dotnet (SDK 10), and a built fixtures/matrix (this script builds it if needed).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
command -v tmux >/dev/null || { echo "tmux is required"; exit 1; }

FEED="${1:-}"
if [ -z "$FEED" ]; then
  FEED="$(mktemp -d)"
  echo "==> packing ttr into $FEED"
  dotnet pack src/ttr.Cli -c Release -o "$FEED" --nologo -v:quiet >/dev/null || { echo "pack failed"; exit 1; }
fi

TOOLDIR="$(mktemp -d)"
LOG="$(mktemp)"
FAIL=0
cleanup() {
  tmux kill-session -t ttr-install 2>/dev/null || true
  rm -rf "$TOOLDIR" "$LOG" 2>/dev/null || true
}
trap cleanup EXIT

echo "==> installing ttr from the local feed (isolated tool-path, no repo build outputs on PATH)"
# --prerelease installs the highest version in the feed including release-candidate (suffixed) builds.
dotnet tool install --tool-path "$TOOLDIR" ttr --add-source "$FEED" --prerelease 2>&1 | tail -2
TTR="$TOOLDIR/ttr"
[ -x "$TTR" ] || { echo "install failed: $TTR not found"; exit 1; }
echo "==> installed: $("$TTR" --version)"

echo "==> building the fixture matrix"
for p in XunitV2.Tests XunitV3.Tests NUnitClassic.Tests NUnit4Broken.Tests MSTestSdk.Tests TUnit.Tests; do
  dotnet build "$ROOT/fixtures/matrix/$p" -c Debug -v:quiet --nologo -consoleLoggerParameters:'ErrorsOnly;NoSummary' >/dev/null
done
dotnet build "$ROOT/fixtures/matrix/MultiTfm.Tests" -c Debug --framework net10.0 -v:quiet --nologo >/dev/null

RCDIR="$(mktemp -d)"
# Run the INSTALLED tool from a neutral CWD (/tmp), so nothing but the SDK + the installed tool is in play.
run_case() {
  local name="$1" target="$2" wait_run="$3" sess="it_$1"
  tmux kill-session -t "$sess" 2>/dev/null || true
  tmux new-session -d -s "$sess" -x 120 -y 45
  tmux send-keys -t "$sess" "cd /tmp && '$TTR' '$target' --log '$LOG'; echo RC=\$? > '$RCDIR/$name'" Enter
  sleep 18
  tmux send-keys -t "$sess" "R"; sleep "$wait_run"
  tmux send-keys -t "$sess" "q"; sleep 4
  tmux kill-session -t "$sess" 2>/dev/null || true
}
expect() {
  local got; got="$(sed -n 's/^RC=//p' "$RCDIR/$1" 2>/dev/null || echo '?')"
  if [ "$got" = "$2" ]; then echo "  $1: exit $got (expected $2) OK"; else echo "  $1: exit $got (expected $2) FAIL"; FAIL=1; fi
}

echo "==> running the INSTALLED tool against the fixture matrix"
: > "$LOG"
run_case failures "$ROOT/fixtures/matrix/XunitV2.Tests/XunitV2.Tests.csproj" 10
expect failures 1
run_case allpass "$ROOT/fixtures/matrix/MultiTfm.Tests/MultiTfm.Tests.csproj" 10
expect allpass 0

echo "==> adapter trace from the installed tool (proves SDK-resolved vstest.console — Strategy A):"
grep -E "\[ADAPTER\]|\[SESSION\]" "$LOG" | head -8 || true
if ! grep -q "vstest.console" "$LOG"; then echo "  FAIL: no SDK-resolved vstest.console in the log"; FAIL=1; fi

rm -rf "$RCDIR"
if [ "$FAIL" = "0" ]; then echo "install test PASSED"; else echo "install test FAILED"; exit 1; fi
