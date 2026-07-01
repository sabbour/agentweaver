# Stage 1: Build React SPA
# Build context must be the repo root so both apps/web/ and apps/Agentweaver.Web/ are accessible.
FROM node:22-alpine AS web-build
WORKDIR /app
COPY apps/web/package.json apps/web/package-lock.json* ./
RUN npm ci
COPY apps/web/ .
RUN unset VITE_API_URL VITE_API_KEY; npm run build

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

ARG IMAGE_TAG=dev
ARG GIT_SHA=unknown
LABEL org.opencontainers.image.version="${IMAGE_TAG}"
LABEL org.opencontainers.image.revision="${GIT_SHA}"

RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

COPY --from=dotnet-build --chown=1000:1000 /app/publish .
COPY --from=web-build --chown=1000:1000 /app/dist ./wwwroot
COPY --chown=1000:1000 apps/web/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod 0755 /app/docker-entrypoint.sh

USER 1000

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV AGENTWEAVER_API_URL=/api

ENTRYPOINT ["/bin/sh", "/app/docker-entrypoint.sh"]
