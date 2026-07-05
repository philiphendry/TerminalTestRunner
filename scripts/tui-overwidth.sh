#!/usr/bin/env bash
# Machine-checks the CLAUDE.md invariant 5 guarantee at multiple terminal sizes: every rendered line fits
# its pane (the renderer cell-aware-truncates with `…` before writing, so nothing overflows). Drives
# `ttr --fake big` (10k nodes) with the detail pane + durations gutter open so both panes are exercised.
# A fresh tmux session is created per size (resize-window is unreliable on a detached session), and each
# captured line's DISPLAY WIDTH (Unicode code points — the fake `big` glyphs are all width-1, so code
# points == cells) is checked against the pane's actual width. Byte length would over-count multi-byte
# glyphs (✓ ✗ … │), so widths are measured with Python, not `${#}`/awk.
# Usage: tui-overwidth.sh <path-to-ttr.dll>
set -uo pipefail

TTR="${1:?usage: tui-overwidth.sh <ttr.dll>}"
FAIL=0

for size in "100 30" "60 20" "160 50" "45 12" "80 24"; do
  # shellcheck disable=SC2086
  set -- $size; W=$1; H=$2
  SESS="ttr_ow_${W}x${H}"
  tmux kill-session -t "$SESS" 2>/dev/null || true
  tmux new-session -d -s "$SESS" -x "$W" -y "$H"
  tmux send-keys -t "$SESS" "dotnet '$TTR' --fake big" Enter
  sleep 6
  tmux send-keys -t "$SESS" "s"; sleep 1     # detail pane
  tmux send-keys -t "$SESS" "t"; sleep 1     # durations gutter
  actual=$(tmux display-message -t "$SESS" -p '#{pane_width}')
  over=$(tmux capture-pane -t "$SESS" -p | python3 -c '
import sys
W = int(sys.argv[1]); bad = 0
for line in sys.stdin:
    if len(line.rstrip("\n").rstrip()) > W:
        print("  OVER-WIDTH: width %d > pane %d: [%s]" % (len(line.rstrip()), W, line.rstrip()))
        bad += 1
print("__COUNT__%d" % bad)
' "$actual")
  echo "size ${W}x${H} (actual pane width ${actual}):"
  echo "$over" | grep -v '^__COUNT__' || true
  [ "$(echo "$over" | sed -n 's/^__COUNT__//p')" != "0" ] && FAIL=1
  tmux send-keys -t "$SESS" "q"; sleep 1
  tmux kill-session -t "$SESS" 2>/dev/null || true
done

if [ "$FAIL" = "0" ]; then echo "over-width check PASSED (no line exceeded its pane at any size)"; else exit 1; fi
