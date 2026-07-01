# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["apps/Agentweaver.Api/Agentweaver.Api.csproj", "apps/Agentweaver.Api/"]
COPY ["apps/Agentweaver.Api.Data/Agentweaver.Api.Data.csproj", "apps/Agentweaver.Api.Data/"]
COPY ["apps/Agentweaver.Api.Migrations.Postgres/Agentweaver.Api.Migrations.Postgres.csproj", "apps/Agentweaver.Api.Migrations.Postgres/"]
COPY ["packages/", "packages/"]
COPY Directory.Build.props .

# Pre-download the Copilot CLI binary once. Directory.Build.props sets CopilotSkipCliDownload=true
# for all projects under /src/, preventing per-project downloads and the resulting NETSDK1152
# duplicate-file error. We manually place the binary in the publish output after dotnet publish.
# The EF bundle temp project at /tmp/ does NOT inherit /src/Directory.Build.props, so it downloads
# exactly one copy on its own — no duplicates from /src/ projects to collide with.
ARG COPILOT_CLI_VERSION=1.0.57
RUN mkdir -p /copilot-bin && \
    curl -fsSL "https://registry.npmjs.org/@github/copilot-linux-x64/-/copilot-linux-x64-${COPILOT_CLI_VERSION}.tgz" | \
    tar -xz --strip-components=1 -C /copilot-bin && \
    chmod +x /copilot-bin/copilot

COPY apps/Agentweaver.Api/ apps/Agentweaver.Api/
COPY apps/Agentweaver.Api.Data/ apps/Agentweaver.Api.Data/
COPY apps/Agentweaver.Api.Migrations.Postgres/ apps/Agentweaver.Api.Migrations.Postgres/
COPY packages/ packages/

RUN dotnet restore "apps/Agentweaver.Api/Agentweaver.Api.csproj"
RUN dotnet publish "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Place the Copilot CLI binary that CopilotSkipCliDownload=true suppressed from the SDK targets.
RUN mkdir -p /app/publish/runtimes/linux-x64/native && \
    cp /copilot-bin/copilot /app/publish/runtimes/linux-x64/native/copilot

RUN dotnet tool install --global dotnet-ef --version 9.0.7
ENV PATH="${PATH}:/root/.dotnet/tools"
# EF bundle temp project at /tmp/ downloads its own single copy (no /src/ duplicates to
# conflict with), so no extra flags needed.
RUN dotnet ef migrations bundle \
    --project "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --startup-project "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --context MemoryDbContext \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained \
    --output /app/publish/efbundle

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG KUBECTL_VERSION=v1.33.1
WORKDIR /app

ARG IMAGE_TAG=dev
ARG GIT_SHA=unknown
LABEL org.opencontainers.image.version="${IMAGE_TAG}"
LABEL org.opencontainers.image.revision="${GIT_SHA}"

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    libgit2-1.7 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSLo /usr/local/bin/kubectl "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl" \
    && echo "$(curl -fsSL https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl.sha256)  /usr/local/bin/kubectl" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/kubectl \
    && kubectl version --client=true

RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

RUN mkdir -p /data /app/logs && chown -R 1000:1000 /data /app

COPY --from=build --chown=1000:1000 /app/publish .
COPY --chown=1000:1000 VERSION ./

USER 1000

# Allow git operations on workspace volumes owned by other users (Azure File shares mount as root)
RUN mkdir -p ~/.config/git && printf '[safe]\n\tdirectory = *\n' > ~/.config/git/config

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.Api.dll"]
