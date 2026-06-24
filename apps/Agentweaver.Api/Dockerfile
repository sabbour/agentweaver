# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore
COPY ["apps/Agentweaver.Api/Agentweaver.Api.csproj", "apps/Agentweaver.Api/"]
COPY ["packages/", "packages/"]

# Copy all source files needed for the build
COPY apps/Agentweaver.Api/ apps/Agentweaver.Api/
COPY packages/ packages/

RUN dotnet restore "apps/Agentweaver.Api/Agentweaver.Api.csproj"
RUN dotnet publish "apps/Agentweaver.Api/Agentweaver.Api.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install libgit2 dependencies for LibGit2Sharp
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgit2-1.7 \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user (UID 1000)
RUN groupadd --gid 1000 appgroup \
    && useradd --uid 1000 --gid appgroup --shell /bin/bash --create-home appuser

# Create data directory and set ownership
RUN mkdir -p /data && chown appuser:appgroup /data

COPY --from=build --chown=appuser:appgroup /app/publish .

USER 1000

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.Api.dll"]
