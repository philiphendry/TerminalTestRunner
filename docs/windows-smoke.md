# Owner manual smoke: Windows Terminal + legacy conhost (brief M7 / AC7)

The sandbox and the Linux/macOS CI lanes cannot exercise a real Windows console. This is the manual script
for the **owner** to run on a Windows machine and paste the result into `docs/phase-7-notes.md` (PASS, or file
issues). It verifies the two Windows console paths: a modern VT-capable terminal, and the legacy no-VT conhost
refusal (the M2 capability-detection path).

## Prerequisites (on the Windows host)

- .NET 10 SDK on `PATH` (`dotnet --version` → 10.x).
- A local feed: the `.nupkg` from CI's `pack`/`release` artifact, or `dotnet pack src/ttr.Cli -c Release -o artifacts`.
- Windows Terminal installed, and access to the legacy console host (`conhost.exe`).

## Install from the local feed

```powershell
dotnet tool install -g ttr --add-source <path-to-feed> --prerelease
ttr --version
```

## 1. Windows Terminal (modern, VT-capable) — expect full TUI

Open **Windows Terminal** and run:

```powershell
ttr --fake files
```

Check, then quit with `q`:

- [ ] Full Unicode glyphs render (`✓ ✗ ⊘ ○`, braille spinner, box-drawing borders) — no mojibake.
- [ ] **Resize** the window narrower and wider: no line ever exceeds the pane width; the layout re-flows;
      below ~40×10 the "terminal too small" placeholder shows instead of corruption.
- [ ] Press `s` (detail pane), `b` (dock right/beneath), `f` (failed filter), `t` (durations) — all respond.
- [ ] Navigate to a failing test and press `o` — the **modal** opens over the tree with a line-number gutter,
      the referenced line highlighted; `n`/`p` cycle, `c`/Esc closes and restores the tree.
- [ ] Press `?` — the help overlay renders inside a box and any key closes it.
- [ ] A **Unicode** test name (if you point ttr at a project with one) renders at the correct width.

Then confirm the ASCII fallback in the same modern terminal:

```powershell
$env:TTR_ASCII=1; ttr --fake files; Remove-Item Env:TTR_ASCII
```

- [ ] Glyphs degrade to ASCII (`+ x s o`, `|`-`-`-`+` borders, `|/-\` spinner) and everything stays legible.

## 2. Legacy conhost (no VT) — expect the M2 refusal, NOT garbage

Launch the **legacy console host** and disable VT so the enable attempt fails. Easiest reliable path:

```powershell
# Force the classic console: run under conhost explicitly.
conhost.exe powershell -NoProfile -Command "ttr --fake files; 'exit code:' + $LASTEXITCODE"
```

If your build of conhost still enables VT, disable it for the session first (Registry
`HKCU\Console\VirtualTerminalLevel = 0`, new window), then run `ttr --fake files` in that classic console.

- [ ] ttr prints a **single clear line** ("This terminal does not support ANSI/VT sequences (legacy
      conhost)…") and exits with code **2** — it does **not** spray escape codes / garbage into the console.
- [ ] `TERM=dumb ttr --fake files` (in any shell) likewise prints the dumb-terminal refusal and exits 2.

## Record the result

Paste into `docs/phase-7-notes.md` under AC7, e.g.:

```
AC7 (owner, Windows): PASS — Windows Terminal full TUI + resize/modal/unicode OK; TTR_ASCII fallback OK;
legacy conhost shows the VT-refusal line and exits 2 (no garbage). [date / Windows build / WT version]
```

or file issues for any box that failed and link them here.
