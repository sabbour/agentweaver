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

# Dev tools are installed in the agent-host image because the nested lxc-native-linux sandbox
# bind-mounts host paths, making these tools available inside Copilot CLI runs as well.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    wget \
    git \
    sudo \
    build-essential \
    libgit2-1.7 \
    python3 \
    python3-pip \
    python3-venv \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
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
