<#
.SYNOPSIS
  Rebuilds the sbx-test template and (re)creates the claude-src sandbox from it.
#>
param(
    [string]$SandboxName = "claude-src",
    [string]$WorkspacePath = (Get-Location).Path,
    [switch]$SkipPatSecret,
    [switch]$RunClaude
)

$ErrorActionPreference = "Stop"

# sbx rm already stops the sandbox as part of removal; -f only skips the
# confirmation prompt. Only attempt removal if it currently exists - and if
# removal fails, stop here with a clear error instead of silently continuing
# into a much later, more confusing "sandbox already exists" failure from
# `sbx run --template` after a wasted docker build/save/load cycle. sbx has a
# known flaky-runtime bug where delete/stop return 500 Internal Server Error
# for a specific sandbox even though the daemon itself is healthy - if that's
# what's happening, this fails fast and says so.
$existingNames = sbx ls -q 2>$null
if ($existingNames -contains $SandboxName) {
    sbx rm $SandboxName -f
    if ($LASTEXITCODE -ne 0) {
        throw "sbx rm failed with exit code $LASTEXITCODE - sandbox '$SandboxName' may be stuck in a bad runtime state (known sbx flakiness, not specific to this template). Try again shortly, or see progress.md for prior occurrences."
    }
}

# Custom secrets are scoped by sandbox name and survive sbx rm/recreate (unlike
# the network policy rules below), so this only prompts on first creation of
# a sandbox with this name. Must run before `sbx run` so NUGET_FEED_PAT is
# already set when entrypoint.sh does its first dotnet restore. Pass
# -SkipPatSecret to bypass this entirely (e.g. when the secret is already set
# or nuget access isn't needed for this run).
if ($SkipPatSecret) {
    Write-Host "Skipping PAT secret setup (-SkipPatSecret specified)."
} else {
    $existingSecrets = sbx secret ls $SandboxName 2>$null
    if ($existingSecrets -match "No secrets found") {
        Write-Host "A DevOps PAT is required the nuget package access and is required on first creation of the sandbox. Create a new PAT with Packaging (Read) permissions and paste it below."
        sbx secret set-custom $SandboxName --host pkgs.dev.azure.com --env NUGET_FEED_PAT
        if ($LASTEXITCODE -ne 0) {
            throw "sbx secret set-custom failed with exit code $LASTEXITCODE"
        }
    }
}

Push-Location $PSScriptRoot
try {
    docker build -t sbx-test:latest .
    if ($LASTEXITCODE -ne 0) {
        throw "docker build failed with exit code $LASTEXITCODE"
    }

    $tarPath = Join-Path $PSScriptRoot "sbx-test.tar"
    if (Test-Path $tarPath) {
        Remove-Item $tarPath -Force
    }

    docker save sbx-test:latest -o $tarPath
    if ($LASTEXITCODE -ne 0) {
        throw "docker save failed with exit code $LASTEXITCODE"
    }

    sbx template load $tarPath
    if ($LASTEXITCODE -ne 0) {
        throw "sbx template load failed with exit code $LASTEXITCODE"
    }
} finally {
    if (Test-Path $tarPath) {
        Remove-Item $tarPath -Force
    }
    Pop-Location
}

sbx run --template docker.io/library/sbx-test claude --name $SandboxName $WorkspacePath --detached
if ($LASTEXITCODE -ne 0) {
    throw "sbx run failed with exit code $LASTEXITCODE"
}

# Sandbox-scoped network policy rules don't survive sbx rm/recreate (unlike
# secrets), so they're reapplied on every run. Unlike secrets, sbx requires
# the sandbox to already exist before a rule can be scoped to it - applying
# these before `sbx run` fails with 'sandbox "..." not found', so they must
# come after.
$allowedDomains = @(
    "pkgs.dev.azure.com:443"
    "api.nuget.org:443"
    "*.blob.core.windows.net:443"
    "api.anthropic.com:443"
    "platform.claude.com:443"
    "api.twilio.com:443"
    "api.sanity.io:443"
    "www.nuget.org:443"
    "nuget.org:443"
    "aka.ms:443"
    "learn.microsoft.com:443"
    "github.com:443"
    "api.github.com:443"
    "raw.githubusercontent.com:443"
    "codeload.github.com:443"
    "objects.githubusercontent.com:443"
    "spectreconsole.net:443"
    "razorconsole.github.io:443"
    "xunit.net:443"
    "nunit.org:443"
    "docs.nunit.org:443"
    "tunit.dev:443"
    
)
foreach ($domain in $allowedDomains) {
    sbx policy allow network --sandbox $SandboxName $domain
    if ($LASTEXITCODE -ne 0) { throw "sbx policy allow ($domain) failed with exit code $LASTEXITCODE" }
}

Write-Host "Waiting for entrypoint.sh startup checks to finish..."
$ready = $false
$shown = 0
for ($i = 0; $i -lt 120; $i++) {
    # Re-cat the whole log each poll (sbx exec has no tail -f/follow mode) but
    # only print lines not already shown, so entrypoint's echo output streams
    # into this terminal as it happens instead of dumping as one concatenated
    # block after the loop exits.
    $lines = @(sbx exec $SandboxName -- sh -c 'cat /var/log/sandbox/entrypoint.log 2>/dev/null' 2>$null)
    if ($lines.Count -gt $shown) {
        $lines[$shown..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
        $shown = $lines.Count
    }
    if ($lines -match '\[entrypoint\] Ready\.') {
        $ready = $true
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $ready) {
    Write-Host "WARNING: entrypoint didn't report ready within 2 minutes - check 'sbx exec $SandboxName -- cat /var/log/sandbox/entrypoint.log'"
}

if ($RunClaude) {
    sbx exec -it $SandboxName claude --dangerously-skip-permissions
} else {
    sbx exec -it $SandboxName pwsh
}
