#!/usr/bin/env bash
# ttr Phase 5 — watch-mode latency harness (brief M7 / AC5, closing POC-6 AC2).
#
# Drives the REAL ttr in watch mode over fixtures/watch/, touches LibA/Calc.cs for N cycles, and reads the
# per-stage timing ttr writes to its --log file to report min/median/p95 for each segment:
#   T0 save → T1 debounce → T2 parallel builds done → T3 re-discovery done → T4 diff done → T5 rerun complete
# Exit criterion: T0→T4 median < 4 s warm (the plan §9 bar). The first (cold) cycle is dropped.
#
# Requirements: tmux, dotnet, and a built ttr + built fixtures/watch (this script builds both if needed).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CYCLES="${1:-6}"          # total cycles (cycle 1 is cold and dropped; need ≥5 warm ⇒ default 6)
SESSION="ttr-latency-$$"
LOG="$(mktemp)"
DLL="$ROOT/src/ttr.Cli/bin/Debug/net10.0/ttr.dll"

command -v tmux >/dev/null || { echo "tmux is required"; exit 1; }

echo "==> building ttr"
dotnet build "$ROOT/src/ttr.Cli/ttr.Cli.csproj" -v:quiet --nologo >/dev/null

# Measure over a copy on a normal (non-mounted) filesystem. On CI this is the checkout FS; in the agent
# sandbox the repo lives on a slow host-mounted FS whose dotnet-build time is a ~7× artifact unrepresentative
# of any real deployment (documented in docs/phase-5-notes.md), so the copy makes the number honest.
WORK="$(mktemp -d)/watch"
mkdir -p "$WORK"
cp -r "$ROOT/fixtures/watch/." "$WORK/"
CALC="$WORK/LibA/Calc.cs"
echo "==> building the watch fixture copy at $WORK"
for p in LibA LibB LibC LibD LibE LibF LibX LibY TestsCore TestsApp TestsX; do
  dotnet build "$WORK/$p/$p.csproj" -v:quiet --nologo \
    -consoleLoggerParameters:'ErrorsOnly;NoSummary' >/dev/null
done

cleanup() {
  tmux kill-session -t "$SESSION" 2>/dev/null || true
  rm -rf "$(dirname "$WORK")" 2>/dev/null || true
  rm -f "$LOG"
}
trap cleanup EXIT

: > "$LOG"
echo "==> starting ttr --watch (timing log: $LOG)"
tmux new-session -d -s "$SESSION" -x 160 -y 48
# TTR_WATCH_LOG is the dedicated per-cycle timing file (kept separate from --log / VSTest trace).
tmux send-keys -t "$SESSION" "cd '$WORK' && TTR_WATCH_LOG='$LOG' dotnet '$DLL' '$WORK/Watch.sln' --watch" Enter

echo "==> waiting for initial discovery to settle"
sleep 40

# T0 timestamps (epoch ns) recorded by THIS script, one per touch.
declare -a T0S=()
for i in $(seq 1 "$CYCLES"); do
  T0S+=("$(date -u +%s.%N)")
  # A real edit: flip a trailing marker comment so the file content genuinely changes each cycle.
  sed -i "s|// lat=.*|// lat=$i|; t; s|return a + b;|return a + b; // lat=$i|" "$CALC"
  # Wait for this cycle's T5 (rerun complete) before the next touch, so cycles don't overlap.
  for _ in $(seq 1 120); do
    sleep 0.5
    [ "$(grep -c "^$i T5_RERUN " "$LOG" 2>/dev/null || echo 0)" -ge 1 ] && break
  done
done

tmux send-keys -t "$SESSION" "q" Enter
sleep 2

echo
echo "==> raw cycle log"
cat "$LOG"
echo

python3 - "$LOG" "${T0S[@]}" <<'PY'
import sys, statistics
log = sys.argv[1]
t0s = [float(x) for x in sys.argv[2:]]

# Parse "<cycle> <STAGE> <iso-utc> ticks=<n>" into {cycle: {stage: epoch_seconds}}
from datetime import datetime, timezone
cycles = {}
for line in open(log):
    parts = line.split()
    if len(parts) < 3: continue
    try: c = int(parts[0])
    except ValueError: continue
    stage = parts[1]
    try:
        dt = datetime.fromisoformat(parts[2])
        if dt.tzinfo is None: dt = dt.replace(tzinfo=timezone.utc)
        cycles.setdefault(c, {})[stage] = dt.timestamp()
    except ValueError:
        pass

STAGES = ["T1_DEBOUNCE","T2_BUILDS","T3_DISCOVER","T4_DIFF","T5_RERUN"]
rows = []
for c in sorted(cycles):
    st = cycles[c]
    if "T1_DEBOUNCE" not in st: continue
    t0 = t0s[c-1] if c-1 < len(t0s) else None
    rows.append((c, t0, st))

if not rows:
    print("no cycles captured — is the fixture built and the log path writable?"); sys.exit(1)

# Drop the first (cold) cycle.
warm = rows[1:] if len(rows) > 1 else rows
print(f"captured {len(rows)} cycles; reporting {len(warm)} warm (cold cycle dropped)\n")

def seg(a, b, st, t0):
    if a == "T0": start = t0
    else: start = st.get(a)
    end = st.get(b)
    if start is None or end is None: return None
    return (end - start) * 1000.0  # ms

segments = [
    ("debounce (T0→T1)", "T0", "T1_DEBOUNCE"),
    ("build    (T1→T2)", "T1_DEBOUNCE", "T2_BUILDS"),
    ("discover (T2→T3)", "T2_BUILDS", "T3_DISCOVER"),
    ("diff     (T3→T4)", "T3_DISCOVER", "T4_DIFF"),
    ("RERUN    (T4→T5)", "T4_DIFF", "T5_RERUN"),
    ("TOTAL    (T0→T4)", "T0", "T4_DIFF"),
]

def pct(vals, p):
    if not vals: return float('nan')
    s = sorted(vals); k = min(len(s)-1, int(round((p/100.0)*(len(s)-1))))
    return s[k]

print(f"{'segment':22} {'min':>8} {'median':>8} {'p95':>8}   (ms)")
print("-"*54)
total_median = None
for name, a, b in segments:
    vals = [v for (_,t0,st) in warm if (v:=seg(a,b,st,t0)) is not None]
    if not vals:
        print(f"{name:22} {'—':>8} {'—':>8} {'—':>8}"); continue
    md = statistics.median(vals)
    if name.startswith("TOTAL"): total_median = md
    print(f"{name:22} {min(vals):8.0f} {md:8.0f} {pct(vals,95):8.0f}")

print()
if total_median is not None:
    bar = 4000.0
    verdict = "PASS" if total_median < bar else "FAIL — STOP (plan §9 says ~3 s should be achievable)"
    print(f"AC5 exit criterion: T0→T4 median = {total_median:.0f} ms  (< {bar:.0f} ms bar) → {verdict}")
    sys.exit(0 if total_median < bar else 2)
PY
