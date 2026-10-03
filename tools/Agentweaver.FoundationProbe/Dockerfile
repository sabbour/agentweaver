# Build context MUST be the repository root:
# docker build --file tools/Agentweaver.FoundationProbe/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src

ARG PROBE_SOURCE_SHA
ARG PROBE_SOURCE_TREE
ARG PROBE_INFRASTRUCTURE_HASH

COPY Directory.Build.props ./
COPY packages/ packages/
COPY tools/Agentweaver.FoundationProbe/ tools/Agentweaver.FoundationProbe/
RUN dotnet restore tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj --locked-mode
RUN dotnet publish tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj \
    --configuration Release --no-restore \
    -p:ProbeSourceSha=${PROBE_SOURCE_SHA} \
    -p:ProbeSourceTree=${PROBE_SOURCE_TREE} \
    -p:ProbeInfrastructureHash=${PROBE_INFRASTRUCTURE_HASH} \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS final
WORKDIR /app
RUN groupadd --gid 10001 foundationprobe && \
    useradd --uid 10001 --gid foundationprobe --no-create-home --shell /usr/sbin/nologin foundationprobe
USER 10001:10001

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Agentweaver.FoundationProbe.dll"]
