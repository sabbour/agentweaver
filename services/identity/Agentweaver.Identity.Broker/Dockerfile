# Build context MUST be the repository root (not this directory), e.g.:
#   docker build -f services/identity/Agentweaver.Identity.Broker/Dockerfile .
# This is required because the project relies on root-level build configuration
# (Directory.Build.props) outside this service's folder and its own packages.lock.json.

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src

# The image pins its own .NET 10 SDK independently of the host's global.json.
# Both build paths use the same project lock file and root build properties.
COPY Directory.Build.props ./
COPY services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj services/identity/Agentweaver.Identity.Broker/
COPY services/identity/Agentweaver.Identity.Broker/packages.lock.json services/identity/Agentweaver.Identity.Broker/
RUN dotnet restore services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj --locked-mode

COPY services/identity/Agentweaver.Identity.Broker/ services/identity/Agentweaver.Identity.Broker/
RUN dotnet build services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj \
    --configuration Release --no-restore
RUN dotnet publish services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj \
    --configuration Release --no-build --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Run as a non-root, unprivileged user. The aspnet runtime image does not include
# curl/wget, so there is deliberately no shell-based HEALTHCHECK instruction here —
# the hosting orchestrator (e.g. Kubernetes) must probe the exposed /health/live and
# /health/ready endpoints directly (HTTP GET), not via an in-container shell command.
RUN groupadd --system identitybroker && \
    useradd --system --gid identitybroker --no-create-home identitybroker
USER identitybroker

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Agentweaver.Identity.Broker.dll"]
