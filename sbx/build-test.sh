#!/usr/bin/env bash
# Deterministic build+test for this sandbox. Some host environments leak an absolute drive path
# into NuGet restore intermittently (parallel restore workers race on config resolution). Workaround:
# a clean serial restore, then lock the nuget-generated files read-only so the build's implicit
# restore can't overwrite them dirty, then build/test with --no-restore. On a clean machine / CI a
# plain `dotnet test` works; this script is only needed here.
set -uo pipefail
cd "$(dirname "$0")/.."

export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
export DiffEngine_Disabled=true Verify_DisableClipboard=true CI=true

dotnet build-server shutdown >/dev/null 2>&1
rm -rf src/*/obj src/*/bin tests/*/obj tests/*/bin

echo "restore…"
dotnet restore ttr.sln --nologo >/dev/null 2>&1
leaked=$(grep -l 'p:.packages\|Program Files' src/*/obj/*.nuget.g.props tests/*/obj/*.nuget.g.props 2>/dev/null | wc -l)
if [ "$leaked" != "0" ]; then echo "restore leaked ($leaked) — retrying serial"; \
  dotnet restore ttr.sln --packages "$PWD/.packages" -p:RestoreDisableParallel=true -m:1 -nodeReuse:false --nologo >/dev/null 2>&1; fi

# Lock the clean restore outputs so the build's implicit restore can't re-dirty them.
chmod a-w src/*/obj/*.nuget.g.* tests/*/obj/*.nuget.g.* src/*/obj/project.assets.json tests/*/obj/project.assets.json 2>/dev/null

echo "build…"
dotnet build ttr.sln --no-restore --nologo -v:quiet "$@" || { echo "BUILD FAILED"; exit 1; }

echo "test…"
dotnet test tests/ttr.Tests/ttr.Tests.csproj --no-build --no-restore --nologo -v:quiet
