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
ARG COPILOT_NPM_REGISTRY_URL=https://registry.npmjs.org
# npm registry dist.shasum for @github/copilot-linux-{x64,arm64}@1.0.57.
ARG COPILOT_CLI_SHA1_AMD64=7938f9f6dfc3248efc1a23e1c76bf98fa4554141
ARG COPILOT_CLI_SHA1_ARM64=3fe206bfe1bd5a32636530e1dd3f3815cea1b336
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
      amd64) runtime_arch=x64; copilot_sha1="${COPILOT_CLI_SHA1_AMD64}" ;; \
      arm64) runtime_arch=arm64; copilot_sha1="${COPILOT_CLI_SHA1_ARM64}" ;; \
      *) echo "Unsupported API image TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    mkdir -p /copilot-bin && \
    printf '%s' "${runtime_arch}" > /copilot-bin/runtime-arch && \
    curl -fsSL -o /tmp/copilot.tgz "${COPILOT_NPM_REGISTRY_URL}/@github/copilot-linux-${runtime_arch}/-/copilot-linux-${runtime_arch}-${COPILOT_CLI_VERSION}.tgz" && \
    echo "${copilot_sha1}  /tmp/copilot.tgz" | sha1sum -c - && \
    tar -xzf /tmp/copilot.tgz --strip-components=1 -C /copilot-bin && \
    rm -f /tmp/copilot.tgz && \
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
RUN runtime_arch="$(cat /copilot-bin/runtime-arch)" && \
    mkdir -p "/app/publish/runtimes/linux-${runtime_arch}/native" && \
    cp /copilot-bin/copilot "/app/publish/runtimes/linux-${runtime_arch}/native/copilot"

RUN dotnet tool install --global dotnet-ef --version 9.0.7
ENV PATH="${PATH}:/root/.dotnet/tools"
# EF bundle temp project at /tmp/ downloads its own single copy (no /src/ duplicates to
# conflict with), so no extra flags needed.
RUN runtime_arch="$(cat /copilot-bin/runtime-arch)" && \
    dotnet ef migrations bundle \
    --project "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --startup-project "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --context MemoryDbContext \
    --configuration Release \
    --runtime "linux-${runtime_arch}" \
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
# Surface build provenance to the running process (LABELs are image metadata only and
# are not readable via Environment.GetEnvironmentVariable at runtime).
ENV IMAGE_TAG=${IMAGE_TAG}
ENV GIT_SHA=${GIT_SHA}

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    git \
    libgit2-1.7 \
    && rm -rf /var/lib/apt/lists/*

# Install the GitHub CLI in the runtime image because GitHub diagnostics and
# integrations invoke `gh` from the API process. The apt repository retains
# only its latest package, so use the immutable release artifact for this pin.
ARG GH_CLI_VERSION=2.98.0
ARG GH_CLI_SHA256_AMD64=f65a3fa2fa0eb2e97c445ee3f5e087a40aae03b64847f45a8f13805e504535d6
ARG GH_CLI_SHA256_ARM64=bbc4ac7964c2a091fd555cd1758d10a7cfcfdc472e405f0b0fb958f05d535cb6
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
      amd64) gh_sha256="${GH_CLI_SHA256_AMD64}" ;; \
      arm64) gh_sha256="${GH_CLI_SHA256_ARM64}" ;; \
      *) echo "Unsupported API image TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && curl -fsSLo /tmp/gh.deb \
        "https://github.com/cli/cli/releases/download/v${GH_CLI_VERSION}/gh_${GH_CLI_VERSION}_linux_${TARGETARCH}.deb" \
    && echo "${gh_sha256}  /tmp/gh.deb" | sha256sum -c - \
    && apt-get update \
    && apt-get install -y --no-install-recommends /tmp/gh.deb \
    && rm -f /tmp/gh.deb \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSLo /usr/local/bin/kubectl "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl" \
    && echo "$(curl -fsSL "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${TARGETARCH}/kubectl.sha256")  /usr/local/bin/kubectl" | sha256sum -c - \
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
