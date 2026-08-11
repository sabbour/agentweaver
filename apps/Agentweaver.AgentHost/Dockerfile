# Agentweaver.AgentHost — sandbox pod entrypoint (spec-018 P1.5).
# Hosts the A2ATurnBridgeAgent over A2A (MapA2AHttpJson). One pod per run; the worker's
# RemoteAgentProxy connects to this pod's A2A endpoint. Build context = repo root.
#
#   docker build -f apps/Agentweaver.AgentHost/Dockerfile -t <reg>/agentweaver-agent-host:<tag> .
#   (or, preferred on Windows) az acr build -r agentweaverregistry \
#       -f apps/Agentweaver.AgentHost/Dockerfile -t agentweaver-agent-host:<tag> .
#
# SECURITY: This image is rebuilt weekly by .github/workflows/agent-host-maintenance.yml to
# pick up OS-level patches. Trivy scans for HIGH/CRITICAL CVEs gate every build.
# Pinned tool versions below are bumped automatically by Dependabot (see .github/dependabot.yml).

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj", "apps/Agentweaver.AgentHost/"]
COPY ["packages/", "packages/"]
COPY Directory.Build.props .

# Bumped 1.0.67 -> 1.0.71-3 (latest published, 2026-07-15) to pick up whatever
# Go stdlib / golang.org/x/net patch level GitHub compiled the CLI binary with.
# The `copilot` binary is a prebuilt, third-party compiled artifact — we cannot
# patch its embedded Go stdlib/x-net ourselves; bumping to the newest release is
# the only lever available. If Trivy still reports "Package: stdlib" /
# "Package: golang.org/x/net" CVEs against this binary after the bump, that is a
# residual, accepted-risk finding pending an upstream copilot-cli release built
# against a newer Go toolchain (see apps/Agentweaver.AgentHost/.trivyignore).
ARG COPILOT_CLI_VERSION=1.0.71-3
# SHA-1 shasum published by the npm registry for this exact tarball (from
# `npm view @github/copilot-linux-x64@${COPILOT_CLI_VERSION} dist.shasum`).
# Pinning + verifying this closes the "mutable remote tooling" supply-chain
# gap: without it, a compromised/replaced tarball at this URL would be piped
# straight into `tar` and run as part of the image with no integrity check.
ARG COPILOT_CLI_SHA1=82ec5a02af1864e177da19bab4ff50c4840917f5
RUN mkdir -p /copilot-bin && \
    curl -fsSL -o /tmp/copilot.tgz "https://registry.npmjs.org/@github/copilot-linux-x64/-/copilot-linux-x64-${COPILOT_CLI_VERSION}.tgz" && \
    echo "${COPILOT_CLI_SHA1}  /tmp/copilot.tgz" | sha1sum -c - && \
    tar -xzf /tmp/copilot.tgz --strip-components=1 -C /copilot-bin && \
    rm -f /tmp/copilot.tgz && \
    chmod +x /copilot-bin/copilot

COPY apps/Agentweaver.AgentHost/ apps/Agentweaver.AgentHost/
COPY packages/ packages/

RUN dotnet restore "apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj" \
    --runtime linux-x64
RUN dotnet publish "apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj" \
    --configuration Release \
    --no-restore \
    --runtime linux-x64 \
    --self-contained false \
    --output /app/publish

RUN mkdir -p /app/publish/runtimes/linux-x64/native && \
    cp /copilot-bin/copilot /app/publish/runtimes/linux-x64/native/copilot

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG IMAGE_TAG=dev
ARG GIT_SHA=unknown
# Pinned tool versions — bumped by Dependabot / weekly maintenance workflow.
# Downloads below are verified against independently-recorded sha256/sha1
# checksums instead of trusting the mutable download URL alone (no
# curl|bash execution of unpinned upstream scripts).
ARG NODE_VERSION=24.19.0
ARG NODE_SHA256=f625d97cd707df4ff96254916fbc5ff014f09c09effe5a1e0ca8f6d41a8789d4
ARG YQ_VERSION=4.53.3
ARG YQ_SHA256=fa52a4e758c63d38299163fbdd1edfb4c4963247918bf9c1c5d31d84789eded4
ARG NPM_VERSION=12.0.2
ARG NODEMON_VERSION=3.1.14
ARG TYPESCRIPT_VERSION=7.0.2
ARG TS_NODE_VERSION=10.9.2
ARG PNPM_VERSION=11.13.1

LABEL org.opencontainers.image.version="${IMAGE_TAG}"
LABEL org.opencontainers.image.revision="${GIT_SHA}"
# Surface build provenance to the running process (LABELs are image metadata only and
# are not readable via Environment.GetEnvironmentVariable at runtime).
ENV IMAGE_TAG=${IMAGE_TAG}
ENV GIT_SHA=${GIT_SHA}

# ── Core dev toolchain ────────────────────────────────────────────────────────
# The lxc-native-linux sandbox bind-mounts host paths, so every tool installed
# here is available inside Copilot CLI agent runs too.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    apt-transport-https \
    gnupg \
    # HTTP / download
    curl \
    wget \
    # VCS
    git \
    git-lfs \
    # Build
    build-essential \
    cmake \
    make \
    libgit2-1.7 \
    # Shell utilities
    sudo \
    less \
    tree \
    file \
    lsof \
    jq \
    unzip \
    zip \
    openssh-client \
    # Network debug
    iputils-ping \
    netcat-openbsd \
    # Process / system
    procps \
    psmisc \
    htop \
    tmux \
    # Sandboxed command execution. Kata AgentHost startup probes bwrap and fails closed if the
    # hardened security context cannot create its per-run mount namespace (#476).
    bubblewrap \
    # Text editors
    vim \
    nano \
    # Search
    ripgrep \
    # Databases
    sqlite3 \
    postgresql-client \
    # Python
    python3 \
    python3-pip \
    python3-venv \
    && git lfs install --system \
    && rm -rf /var/lib/apt/lists/*

# ── GitHub CLI ────────────────────────────────────────────────────────────────
# `apt-get install gh` (no version pin) previously let GHA's Docker layer cache
# silently keep serving whatever `gh` build got baked into the cache the first
# time this layer ran — `apt-get update` re-runs but BuildKit reuses the whole
# RUN's cached *result* verbatim as long as the instruction text is unchanged,
# so the actually-latest `gh` package was never re-fetched. ARG GH_CLI_CACHE_BUST
# is interpolated into the RUN command so bumping it (Dependabot / maintenance
# workflow) forces a fresh apt-get update+install, closing gh's embedded
# Go stdlib/grpc-go CVEs (e.g. GHSA-hrxh-6v49-42gf) as new gh releases ship.
ARG GH_CLI_CACHE_BUST=2026-07-23
RUN echo "cache-bust: ${GH_CLI_CACHE_BUST}" \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

# ── yq (YAML processor) ───────────────────────────────────────────────────────
RUN curl -fsSL "https://github.com/mikefarah/yq/releases/download/v${YQ_VERSION}/yq_linux_amd64" \
        -o /usr/local/bin/yq \
    && echo "${YQ_SHA256}  /usr/local/bin/yq" | sha256sum -c - \
    && chmod +x /usr/local/bin/yq

# ── Node.js + npm globals ─────────────────────────────────────────────────────
# Downloaded as a checksum-verified tarball from nodejs.org instead of piping
# NodeSource's mutable setup_<major>.x script into bash — that script is
# fetched and executed as root with no version pin or content verification.
# npm is self-updated to latest *before* the global tool installs: the
# bundled npm in this Node release has transitive deps (node-gyp -> tar /
# undici) carrying CVEs with upstream fixes (e.g. CVE-2026-59873/59874 in
# tar, CVE-2026-12151 in undici); newer npm pulls in patched versions,
# closing those out of the Trivy HIGH/CRITICAL gate without touching the
# scan config itself. npm global packages are pinned to exact versions
# instead of resolving whatever "latest" happens to publish at build time.
RUN curl -fsSL -o /tmp/node.tar.gz "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.gz" \
    && echo "${NODE_SHA256}  /tmp/node.tar.gz" | sha256sum -c - \
    && tar -xzf /tmp/node.tar.gz -C /usr/local --strip-components=1 \
    && rm -f /tmp/node.tar.gz \
    && npm install -g npm@${NPM_VERSION} \
    && npm install --prefix /usr/local/lib/node_modules/npm --workspaces=false --omit=dev --no-save --no-package-lock \
        brace-expansion@5.0.9 \
        ip-address@10.3.1 \
    && npm install -g \
        nodemon@${NODEMON_VERSION} \
        typescript@${TYPESCRIPT_VERSION} \
        ts-node@${TS_NODE_VERSION} \
        pnpm@${PNPM_VERSION}

# ── .NET 10 SDK — agents need to build .NET projects ──────────────────────────
RUN curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
        -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm -f /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

# ── Non-root user ─────────────────────────────────────────────────────────────
RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

RUN echo "appuser ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/appuser \
    && chmod 0440 /etc/sudoers.d/appuser

RUN mkdir -p /workspace /app/config /app/logs && chown -R 1000:1000 /workspace /app

# ── Sandbox tool manifest ─────────────────────────────────────────────────────
# Baked at build time so agents can discover available tools without probing.
# Injected into every agent's system prompt by AgentHostStartupService.
RUN mkdir -p /etc/agentweaver && python3 - <<'EOF'
import json, subprocess, shutil, sys

def ver(cmd):
    try:
        out = subprocess.check_output(cmd, shell=True, stderr=subprocess.DEVNULL, text=True).strip()
        return out.splitlines()[0] if out else None
    except Exception:
        return None

tools = {
    "git":              ver("git --version"),
    "git-lfs":          ver("git-lfs --version"),
    "gh":               ver("gh --version"),
    "node":             ver("node --version"),
    "npm":              ver("npm --version"),
    "pnpm":             ver("pnpm --version"),
    "typescript":       ver("tsc --version"),
    "ts-node":          ver("ts-node --version"),
    "nodemon":          ver("nodemon --version"),
    "python3":          ver("python3 --version"),
    "pip3":             ver("pip3 --version"),
    "dotnet":           ver("dotnet --version"),
    "curl":             ver("curl --version"),
    "wget":             ver("wget --version"),
    "gh":               ver("gh --version"),
    "jq":               ver("jq --version"),
    "yq":               ver("yq --version"),
    "rg":               ver("rg --version"),
    "cmake":            ver("cmake --version"),
    "make":             ver("make --version"),
    "sqlite3":          ver("sqlite3 --version"),
    "psql":             ver("psql --version"),
    "tmux":             ver("tmux -V"),
    "vim":              ver("vim --version"),
    "nano":             ver("nano --version"),
    "tree":             ver("tree --version"),
    "ssh":              ver("ssh -V"),
    "openssl":          ver("openssl version"),
}

available = {k: v for k, v in tools.items() if v}
manifest = {
    "schema": "agentweaver/sandbox-manifest/v1",
    "tools": available,
    "note": "Read /etc/agentweaver/sandbox-manifest.json to discover available tools and versions."
}
with open("/etc/agentweaver/sandbox-manifest.json", "w") as f:
    json.dump(manifest, f, indent=2)
print(f"Manifest written: {len(available)} tools", file=sys.stderr)
EOF

COPY --from=build --chown=1000:1000 /app/publish .

USER 1000

# Allow git operations on workspace volumes owned by other users (Azure File shares mount as root).
RUN mkdir -p ~/.config/git && printf '[safe]\n\tdirectory = *\n' > ~/.config/git/config

# A2A port (spec-018: AgentHost:Port default 8088). PoC plain-http path binds here when
# AgentHost:RequireMtls=false; mTLS path uses Kestrel:Endpoints from the mounted appsettings.k8s.json.
EXPOSE 8088

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.AgentHost.dll"]
