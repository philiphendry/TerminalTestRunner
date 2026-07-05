#!/usr/bin/env bash
# Record the ttr core-loop demo cast (brief M5): open → run → f (failed-only) → fix → vanish → watch.
# Produces docs/demo.cast (asciicast v2). Upload it with `asciinema upload docs/demo.cast` and paste the
# resulting URL into README.md / the release notes.
#
# Requirements: asciinema, tmux, dotnet (SDK 10). Drives the built ttr against the curated `files` fake
# scenario (deterministic outcomes) so the cast is reproducible and needs no real test project.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
command -v asciinema >/dev/null || { echo "asciinema is required (see docs/ttr-implementation-plan.md Appendix C)"; exit 1; }
command -v tmux >/dev/null || { echo "tmux is required"; exit 1; }

echo "==> building ttr"
dotnet build src/ttr.Cli -c Release -v:quiet --nologo >/dev/null
DLL="$ROOT/src/ttr.Cli/bin/Release/net10.0/ttr.dll"
OUT="${1:-$ROOT/docs/demo.cast}"

# A scripted key sequence driving the flaky scenario (has failures to fix, then watch).
DRIVE=$(cat <<'KEYS'
sleep 3
E            # expand all
sleep 1
s            # detail pane
sleep 2
f            # failed-only filter
sleep 2
R            # rerun all (flaky → some now pass and vanish under the filter)
sleep 4
q
KEYS
)

echo "==> recording to $OUT"
asciinema rec --overwrite --cols 120 --rows 34 \
  --command "bash -c '
    tmux kill-session -t ttr-demo 2>/dev/null || true
    tmux new-session -d -s ttr-demo -x 120 -y 34 \"dotnet $DLL --fake flaky\"
    tmux attach -t ttr-demo &
    ATTACH=\$!
    while IFS= read -r line; do
      case \"\$line\" in
        sleep*) eval \"\$line\" ;;
        \"\"|\\#*) : ;;
        *) tmux send-keys -t ttr-demo \"\${line%%#*}\" ;;
      esac
    done <<KEYSEOF
$DRIVE
KEYSEOF
    wait \$ATTACH 2>/dev/null || true
  '" \
  "$OUT"

echo "==> recorded $OUT"
echo "    upload with:  asciinema upload $OUT   (then link the URL from README.md)"
