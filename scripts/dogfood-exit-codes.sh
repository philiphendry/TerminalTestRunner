#!/usr/bin/env bash
# Dogfooding + exit-code correctness (brief M5/M6, AC5). Drives real ttr runs through tmux and asserts
# the process exit code: 0 when nothing failed, 1 when a test failed, 130 on Ctrl+C mid-run. From this
# phase on, a regression that breaks these breaks ttr's own CI.
# Usage: dogfood-exit-codes.sh <path-to-ttr.dll> <fixtures/matrix dir>
set -uo pipefail

TTR="${1:?usage: dogfood-exit-codes.sh <ttr.dll> <matrix-dir>}"
MATRIX="${2:?usage: dogfood-exit-codes.sh <ttr.dll> <matrix-dir>}"
RCDIR="$(mktemp -d)"
FAIL=0

# Launch ttr on a target, wait for discovery, press R, wait, then either quit ('q') or interrupt (C-c).
# Writes the process exit code to $RCDIR/<name>.
run_case() {
  local name="$1" target="$2" after="$3" wait_run="$4"
  local sess="df_${name}"
  tmux kill-session -t "$sess" 2>/dev/null || true
  tmux new-session -d -s "$sess" -x 120 -y 45
  tmux send-keys -t "$sess" "dotnet '$TTR' --no-build '$target'; echo RC=\$? > '$RCDIR/$name'" Enter
  sleep 16                       # discovery (VSTest console + MTP hosts) settles
  tmux send-keys -t "$sess" "R"
  if [ "$after" = "C-c" ]; then
    sleep 1                      # interrupt mid-run
    tmux send-keys -t "$sess" C-c
  else
    sleep "$wait_run"            # let the run finish
    tmux send-keys -t "$sess" "q"
  fi
  sleep 4
  tmux kill-session -t "$sess" 2>/dev/null || true
}

expect() {
  local name="$1" want="$2"
  local got; got="$(sed -n 's/^RC=//p' "$RCDIR/$name" 2>/dev/null || echo '?')"
  if [ "$got" = "$want" ]; then
    echo "  $name: exit $got (expected $want) OK"
  else
    echo "  $name: exit $got (expected $want) FAIL"; FAIL=1
  fi
}

echo "exit-code checks:"
# All-pass target (MultiTfm net10.0 tests all pass; net8.0 is an evaluate-only warning, not a failure).
run_case allpass "$MATRIX/MultiTfm.Tests/MultiTfm.Tests.csproj" q 8
expect allpass 0
# Mixed matrix has deliberate failures → exit 1.
run_case failures "$MATRIX/Matrix.slnx" q 18
expect failures 1
# Ctrl+C mid-run → 130, terminal restored.
run_case interrupt "$MATRIX/XunitV3.Tests/XunitV3.Tests.csproj" C-c 0
expect interrupt 130

rm -rf "$RCDIR"
if [ "$FAIL" = "0" ]; then echo "exit-code checks PASSED"; else exit 1; fi
