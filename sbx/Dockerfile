FROM docker/sandbox-templates:claude-code

USER root

ENV DEBIAN_FRONTEND=noninteractive \
    TZ=Etc/UTC \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    TESTINGPLATFORM_TELEMETRY_OPTOUT=1

# ---- Microsoft apt repo (shared between .NET and SQL Server) ------------
# Microsoft's prod.list expects the key at /etc/apt/trusted.gpg.d/microsoft.gpg
RUN curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
      | gpg --dearmor -o /etc/apt/trusted.gpg.d/microsoft.gpg \
    && curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/prod.list \
      -o /etc/apt/sources.list.d/microsoft-prod.list \
    && curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/mssql-server-2022.list \
      -o /etc/apt/sources.list.d/mssql-server-2022.list

# ---- .NET 8 SDK ----------------------------------------------------------
RUN apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-8.0 \
    && rm -rf /var/lib/apt/lists/*

# ---- .NET 10 SDK ---------------------------------------------------------
RUN apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

# ---- PowerShell (pwsh) ----------------------------------------------------
# Lets `sbx exec <sandbox> -- pwsh` give an interactive PowerShell prompt
# inside the sandbox, instead of bash.
RUN apt-get update \
    && apt-get install -y --no-install-recommends powershell \
    && rm -rf /var/lib/apt/lists/*

# ---- tmux -----------------------------------------------------------------
RUN apt-get update \
    && apt-get install -y --no-install-recommends tmux \
    && rm -rf /var/lib/apt/lists/*

# ---- SQL Server 2022 (Developer edition by default) + tools -------------
# We install the binaries here, but DO NOT run mssql-conf setup — that needs
# a running init system and an actual SQL Server process, which Docker build
# steps don't provide. The entrypoint script does first-run setup at container
# start, when systemd-style supervision is available.
ARG MSSQL_PID=Developer
ARG MSSQL_SA_PASSWORD=Sandbox_Str0ng!
ENV MSSQL_PID=${MSSQL_PID} \
    MSSQL_SA_PASSWORD=${MSSQL_SA_PASSWORD} \
    ACCEPT_EULA=Y
RUN apt-get update \
    && apt-get install -y --no-install-recommends mssql-server mssql-tools18 \
    && rm -rf /var/lib/apt/lists/*

# mssql-server's packaging targets Ubuntu 22.04 (libldap/liblber 2.5), but this
# base image ships a newer libldap (2.6+) whose SONAME AND exported symbol
# version node changed (OPENLDAP_2.200 vs OPENLDAP_2.5), so sqlservr fails at
# startup first with "liblber-2.5.so.0: cannot open shared object file" and,
# even after symlinking the SONAME, with "version `OPENLDAP_2.5' not found".
# A symlink to the newer lib is therefore not ABI-compatible; we fetch the
# actual Ubuntu 22.04 (jammy) libldap-2.5-0 package, which ships the exact
# liblber-2.5.so.0 / libldap-2.5.so.0 sqlservr was built against, and install
# just its two shared libraries alongside the newer ones (verified: sqlservr
# starts and reaches "Recovery is complete" with these in place).
RUN cd /tmp \
    && curl -fsSL -o libldap-2.5-0.deb \
       http://archive.ubuntu.com/ubuntu/pool/main/o/openldap/libldap-2.5-0_2.5.20+dfsg-0ubuntu0.22.04.1_amd64.deb \
    && dpkg-deb -x libldap-2.5-0.deb /tmp/ldapextract \
    && cp -P /tmp/ldapextract/usr/lib/x86_64-linux-gnu/liblber-2.5.so.0* /usr/lib/x86_64-linux-gnu/ \
    && cp -P /tmp/ldapextract/usr/lib/x86_64-linux-gnu/libldap-2.5.so.0* /usr/lib/x86_64-linux-gnu/ \
    && ldconfig \
    && rm -rf /tmp/libldap-2.5-0.deb /tmp/ldapextract

ENV PATH="${PATH}:/opt/mssql-tools18/bin:/opt/mssql/bin"

# SQL Server runs as the mssql user; give it writable data/log dirs owned by mssql.
RUN mkdir -p /var/opt/mssql \
    && chown -R mssql:mssql /var/opt/mssql

# Logs for the auto-started API/admin/voting processes, written by entrypoint.sh
# as the base image's agent user.
RUN mkdir -p /var/log/sandbox \
    && chown -R agent:agent /var/log/sandbox

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

USER agent
WORKDIR /workspace

ENTRYPOINT ["tini", "--", "/usr/local/bin/entrypoint.sh"]
CMD ["claude", "--dangerously-skip-permissions"]
