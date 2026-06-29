# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["apps/Agentweaver.Api/Agentweaver.Api.csproj", "apps/Agentweaver.Api/"]
COPY ["apps/Agentweaver.Api.Data/Agentweaver.Api.Data.csproj", "apps/Agentweaver.Api.Data/"]
COPY ["apps/Agentweaver.Api.Migrations.Postgres/Agentweaver.Api.Migrations.Postgres.csproj", "apps/Agentweaver.Api.Migrations.Postgres/"]
COPY ["packages/", "packages/"]
COPY Directory.Build.props .

COPY apps/Agentweaver.Api/ apps/Agentweaver.Api/
COPY apps/Agentweaver.Api.Data/ apps/Agentweaver.Api.Data/
COPY apps/Agentweaver.Api.Migrations.Postgres/ apps/Agentweaver.Api.Migrations.Postgres/
COPY packages/ packages/

# Apply to all MSBuild invocations including EF bundle's /tmp temp project
ENV MSBUILDADDITIONALCOMMANDLINEARGS=/p:ErrorOnDuplicatePublishOutputFiles=false

RUN dotnet restore "apps/Agentweaver.Api/Agentweaver.Api.csproj"
RUN dotnet publish "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

RUN dotnet tool install --global dotnet-ef --version 9.0.7
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
    --project "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --startup-project "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --context MemoryDbContext \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained \
    --verbose \
    --output /app/publish/efbundle

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG KUBECTL_VERSION=v1.33.1
WORKDIR /app

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

USER 1000

# Allow git operations on workspace volumes owned by other users (Azure File shares mount as root)
RUN mkdir -p ~/.config/git && printf '[safe]\n\tdirectory = *\n' > ~/.config/git/config

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.Api.dll"]
