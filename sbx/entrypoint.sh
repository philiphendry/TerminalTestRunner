#!/usr/bin/env bash
# entrypoint.sh
# --------------
# Runs once per sandbox start. Starts SQL Server, then the API, admin, and
# voting dev servers in the background (installing dependencies first if
# needed), then hands off to the sandbox's CMD.
#
# No -e: a background service failing to start must not take the others
# down with it, or stop this script from exec'ing into CMD.
set -uo pipefail

LOG_DIR=/var/log/sandbox
sudo mkdir -p "${LOG_DIR}"
sudo chmod -R 0777 "${LOG_DIR}"

# Mirror all entrypoint output to a log file so tooling outside the container
# (e.g. build-and-run scripts) can poll for the final "Ready." line instead of
# racing a fixed sleep against sandbox boot time.
exec > >(tee -a "${LOG_DIR}/entrypoint.log") 2>&1

# -----------------------------------------------------------------------------
# TLS trust store — verify sbx's credential-proxy CA actually got installed
# -----------------------------------------------------------------------------
# sbx installs a per-session "Docker Sandboxes Proxy CA" into
# /usr/local/share/ca-certificates/ at login and runs update-ca-certificates
# to fold it into /etc/ssl/certs/ca-certificates.crt, which NODE_EXTRA_CA_CERTS
# also points at. This step has been observed to race/fail on boot, leaving
# ca-certificates.crt empty (0 bytes) - which silently breaks every non-Node
# HTTPS client (curl, OpenSSL, .NET/PowerShell) while claude itself keeps
# working, since Node bundles its own root CA set. Detect and retry here so a
# broken sandbox says so immediately, instead of failing minutes later deep
# inside an unrelated dotnet restore or NuGet call with an opaque
# "self-signed certificate in certificate chain" error.
echo "[entrypoint] Verifying CA trust store..."
CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt
ca_ok=false
for i in {1..10}; do
  if [[ -s "${CA_BUNDLE}" ]] && curl -fsS -o /dev/null --max-time 5 https://www.google.com 2>/dev/null; then
    ca_ok=true
    break
  fi
  sudo update-ca-certificates >/dev/null 2>&1
  sleep 2
done
if [[ "${ca_ok}" == true ]]; then
  echo "[entrypoint] CA trust store OK ($(wc -c < "${CA_BUNDLE}") bytes)."
else
  echo "[entrypoint] WARNING: CA trust store still broken after retries (${CA_BUNDLE} is $(wc -c < "${CA_BUNDLE}" 2>/dev/null || echo '?') bytes)."
  echo "[entrypoint] WARNING: general HTTPS (curl/OpenSSL/.NET/PowerShell) will fail with cert errors until this sandbox is restarted. claude/Node are unaffected."
fi

# The sbx proxy specifically needs its own CA trusted for the "forward" path
# (hosts with a registered secret, e.g. custom NuGet feed PATs) - check it
# separately since it can be empty even when the rest of the CA bundle is fine.
PROXY_CA=/usr/local/share/ca-certificates/proxy-ca.crt
if [[ -f "${PROXY_CA}" && ! -s "${PROXY_CA}" ]]; then
  echo "[entrypoint] WARNING: ${PROXY_CA} is empty - sbx's credential-proxy CA was not installed this boot."
  echo "[entrypoint] WARNING: requests to hosts behind a custom secret (e.g. pkgs.dev.azure.com) will fail with 'self-signed certificate in certificate chain' until this sandbox is restarted."
fi

# -----------------------------------------------------------------------------
# Custom secrets — warn early if an expected one is missing, wire up NuGet
# -----------------------------------------------------------------------------
# dotnet/NuGet has no built-in way to read NUGET_FEED_PAT itself - it only
# authenticates against a source listed in packageSourceCredentials (or via a
# credential-provider plugin). Without this, `dotnet restore` fails with the
# generic NU1301 "unable to load the service index", indistinguishable from a
# network/CA problem even though credentials are the actual missing piece.
# ClearTextPassword here is the placeholder value, not the real PAT - sbx's
# proxy substitutes the real secret only on egress to pkgs.dev.azure.com.
if [[ -z "${NUGET_FEED_PAT:-}" ]]; then
  echo "[entrypoint] WARNING: NUGET_FEED_PAT not set - it must be configured via 'sbx secret set-custom' for this sandbox before dotnet restore against the private Azure Artifacts feed will work."
else
  echo "[entrypoint] Writing NuGet credential config for the private feed..."
  mkdir -p "${HOME}/.nuget/NuGet"
  cat > "${HOME}/.nuget/NuGet/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSourceCredentials>
    <mi-voice-feed>
      <add key="Username" value="docker" />
      <add key="ClearTextPassword" value="${NUGET_FEED_PAT}" />
    </mi-voice-feed>
  </packageSourceCredentials>
</configuration>
EOF
fi

# -----------------------------------------------------------------------------
# SQL Server
# -----------------------------------------------------------------------------
echo "[entrypoint] Starting SQL Server..."
sudo chown -R mssql:mssql /var/opt/mssql
sudo -E -u mssql nohup /opt/mssql/bin/sqlservr > "${LOG_DIR}/mssql.log" 2>&1 &
echo "[entrypoint] SQL Server PID $!, log: ${LOG_DIR}/mssql.log"

for i in {1..60}; do
  if /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" -C -Q "SELECT 1" >/dev/null 2>&1; then
    echo "[entrypoint] SQL Server is ready."
    break
  fi
  sleep 1
done

# -----------------------------------------------------------------------------
# Hand off to CMD (normally `claude --dangerously-skip-permissions`)
# -----------------------------------------------------------------------------
echo "[entrypoint] Ready. Logs in ${LOG_DIR}/."
exec "$@"
