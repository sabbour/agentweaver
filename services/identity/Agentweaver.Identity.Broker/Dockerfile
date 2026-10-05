# Build context MUST be the repository root (not this directory), e.g.:
#   docker build --platform linux/amd64 -f services/identity/Agentweaver.Identity.Broker/Dockerfile .
# This is required because the project relies on root-level build configuration
# (Directory.Build.props) outside this service's folder and its own packages.lock.json.

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src

# The image pins its own .NET 10 SDK independently of the host's global.json.
# Both build paths use the same project lock file and root build properties.
COPY Directory.Build.props ./
COPY services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj services/identity/Agentweaver.Identity.Broker/
COPY services/identity/Agentweaver.Identity.Broker/packages.lock.json services/identity/Agentweaver.Identity.Broker/
COPY packages/Agentweaver.Abstractions/Agentweaver.Abstractions.csproj packages/Agentweaver.Abstractions/
COPY packages/Agentweaver.Abstractions/packages.lock.json packages/Agentweaver.Abstractions/
COPY packages/Agentweaver.Identity/Agentweaver.Identity.csproj packages/Agentweaver.Identity/
COPY packages/Agentweaver.Identity/packages.lock.json packages/Agentweaver.Identity/
COPY packages/Agentweaver.Secrets.AzureKeyVault/Agentweaver.Secrets.AzureKeyVault.csproj packages/Agentweaver.Secrets.AzureKeyVault/
COPY packages/Agentweaver.Secrets.AzureKeyVault/packages.lock.json packages/Agentweaver.Secrets.AzureKeyVault/
RUN dotnet restore services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj --locked-mode

COPY services/identity/Agentweaver.Identity.Broker/ services/identity/Agentweaver.Identity.Broker/
COPY packages/Agentweaver.Abstractions/ packages/Agentweaver.Abstractions/
COPY packages/Agentweaver.Identity/ packages/Agentweaver.Identity/
COPY packages/Agentweaver.Secrets.AzureKeyVault/ packages/Agentweaver.Secrets.AzureKeyVault/
COPY infra/bicep/postgres-identity-runtime-grants.sql infra/bicep/
RUN dotnet build services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj \
    --configuration Release --no-restore
RUN dotnet publish services/identity/Agentweaver.Identity.Broker/Agentweaver.Identity.Broker.csproj \
    --configuration Release --no-build --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS final
WORKDIR /app

# Run as a non-root, unprivileged user. The aspnet runtime image does not include
# curl/wget, so there is deliberately no shell-based HEALTHCHECK instruction here —
# the hosting orchestrator (e.g. Kubernetes) must probe the exposed /health/live and
# /health/ready endpoints directly over HTTPS, not via an in-container shell command.
RUN groupadd --gid 10001 identitybroker && \
    useradd --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin identitybroker
USER 10001:10001

COPY --from=build /app/publish .

EXPOSE 8443
ENTRYPOINT ["dotnet", "Agentweaver.Identity.Broker.dll"]
