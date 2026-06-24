# Stage 1: Build React SPA
# Build context must be the repo root so both apps/web/ and apps/Agentweaver.Web/ are accessible.
FROM node:22-alpine AS web-build
WORKDIR /app
COPY apps/web/package.json apps/web/package-lock.json* ./
RUN npm ci
COPY apps/web/ .
RUN npm run build

# Stage 2: Build the ASP.NET Core static file host
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY apps/Agentweaver.Web/Agentweaver.Web.csproj apps/Agentweaver.Web/
RUN dotnet restore apps/Agentweaver.Web/Agentweaver.Web.csproj
COPY apps/Agentweaver.Web/ apps/Agentweaver.Web/
RUN dotnet publish apps/Agentweaver.Web/Agentweaver.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Stage 3: Runtime — ASP.NET Core serving the React SPA
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd --gid 1000 appgroup \
    && useradd --uid 1000 --gid appgroup --shell /bin/bash --create-home appuser

COPY --from=dotnet-build --chown=appuser:appgroup /app/publish .
COPY --from=web-build --chown=appuser:appgroup /app/dist ./wwwroot

USER 1000

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Agentweaver.Web.dll"]
