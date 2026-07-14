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

ARG COPILOT_CLI_VERSION=1.0.67
RUN mkdir -p /copilot-bin && \
    curl -fsSL "https://registry.npmjs.org/@github/copilot-linux-x64/-/copilot-linux-x64-${COPILOT_CLI_VERSION}.tgz" | \
    tar -xz --strip-components=1 -C /copilot-bin && \
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
# Pinned tool versions — bumped by Dependabot / weekly maintenance workflow
ARG NODE_MAJOR=24
ARG YQ_VERSION=4.44.3

LABEL org.opencontainers.image.version="${IMAGE_TAG}"
LABEL org.opencontainers.image.revision="${GIT_SHA}"

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
    # Sandboxed command execution (RunCommandTool / SandboxExecutorFactory
    # requires bwrap on PATH; without it, execution silently falls back to
    # PassthroughExecutor with no isolation, or fails outright — see #269)
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
RUN curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

# ── yq (YAML processor) ───────────────────────────────────────────────────────
RUN curl -fsSL "https://github.com/mikefarah/yq/releases/download/v${YQ_VERSION}/yq_linux_amd64" \
        -o /usr/local/bin/yq \
    && chmod +x /usr/local/bin/yq

# ── Node.js (NodeSource) + npm globals ────────────────────────────────────────
RUN curl -fsSL "https://deb.nodesource.com/setup_${NODE_MAJOR}.x" | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g nodemon typescript ts-node pnpm \
    && rm -rf /var/lib/apt/lists/*

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

