# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["apps/Agentweaver.Mcp/Agentweaver.Mcp.csproj", "apps/Agentweaver.Mcp/"]

COPY apps/Agentweaver.Mcp/ apps/Agentweaver.Mcp/

RUN dotnet restore "apps/Agentweaver.Mcp/Agentweaver.Mcp.csproj"
RUN dotnet publish "apps/Agentweaver.Mcp/Agentweaver.Mcp.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

COPY --from=build --chown=1000:1000 /app/publish .

USER 1000

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.Mcp.dll"]
