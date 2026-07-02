# Agentweaver.AgentHost — sandbox pod entrypoint (spec-018 P1.5).
# Hosts the A2ATurnBridgeAgent over A2A (MapA2AHttpJson). One pod per run; the worker's
# RemoteAgentProxy connects to this pod's A2A endpoint. Build context = repo root.
#
#   docker build -f apps/Agentweaver.AgentHost/Dockerfile -t <reg>/agentweaver-agent-host:<tag> .
#   (or, preferred on Windows) az acr build -r agentweaverregistry \
#       -f apps/Agentweaver.AgentHost/Dockerfile -t agentweaver-agent-host:<tag> .

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
LABEL org.opencontainers.image.version="${IMAGE_TAG}"
LABEL org.opencontainers.image.revision="${GIT_SHA}"

# Dev tools: the lxc-native-linux sandbox bind-mounts host paths, so everything
# installed here is available inside Copilot CLI agent runs.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    apt-transport-https \
    gnupg \
    curl \
    wget \
    jq \
    unzip \
    zip \
    git \
    git-lfs \
    sudo \
    build-essential \
    cmake \
    make \
    libgit2-1.7 \
    python3 \
    python3-pip \
    python3-venv \
    && git lfs install --system \
    && rm -rf /var/lib/apt/lists/*

# GitHub CLI
RUN curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

# Node.js 24 LTS (matches ACA Copilot sandbox image) via NodeSource
RUN curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g nodemon \
    && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK — agents need to build .NET projects, not just run them
RUN curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
        -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm -f /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

RUN echo "appuser ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/appuser \
    && chmod 0440 /etc/sudoers.d/appuser

RUN mkdir -p /workspace /app/config /app/logs && chown -R 1000:1000 /workspace /app

COPY --from=build --chown=1000:1000 /app/publish .

USER 1000

# Allow git operations on workspace volumes owned by other users (Azure File shares mount as root).
RUN mkdir -p ~/.config/git && printf '[safe]\n\tdirectory = *\n' > ~/.config/git/config

# A2A port (spec-018: AgentHost:Port default 8088). PoC plain-http path binds here when
# AgentHost:RequireMtls=false; mTLS path uses Kestrel:Endpoints from the mounted appsettings.k8s.json.
EXPOSE 8088

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.AgentHost.dll"]
