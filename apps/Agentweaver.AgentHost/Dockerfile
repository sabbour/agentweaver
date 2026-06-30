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

COPY apps/Agentweaver.AgentHost/ apps/Agentweaver.AgentHost/
COPY packages/ packages/

RUN dotnet restore "apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj"
RUN dotnet publish "apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj" \
    --configuration Release \
    --no-restore \
    --runtime linux-x64 \
    --self-contained false \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# git/libgit2 for workspace operations; ca-certificates for the Copilot HTTP SDK (CopilotClient).
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    git \
    libgit2-1.7 \
    && rm -rf /var/lib/apt/lists/*

RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

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
