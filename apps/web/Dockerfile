# Frontend assets are built before `az acr build` runs, so the private Azure
# Artifacts npm credential never enters the Docker build context, layer
# filesystem, `RUN` command metadata, or build logs.
# Stage 1: Build the ASP.NET Core static file host
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY apps/Agentweaver.Web/Agentweaver.Web.csproj apps/Agentweaver.Web/
RUN dotnet restore apps/Agentweaver.Web/Agentweaver.Web.csproj
COPY apps/Agentweaver.Web/ apps/Agentweaver.Web/
RUN dotnet publish apps/Agentweaver.Web/Agentweaver.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Stage 2: Runtime — ASP.NET Core serving the React SPA
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG IMAGE_TAG=dev
ARG GIT_SHA=unknown
LABEL org.opencontainers.image.version="${IMAGE_TAG}"
LABEL org.opencontainers.image.revision="${GIT_SHA}"

RUN getent group 1000 || groupadd --gid 1000 appgroup; \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser

COPY --from=dotnet-build --chown=1000:1000 /app/publish .
COPY --chown=1000:1000 apps/web/dist ./wwwroot
COPY --chown=1000:1000 apps/web/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod 0755 /app/docker-entrypoint.sh

USER 1000

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# API_URL is the API ORIGIN only (no `/api` suffix). Empty string means "same origin as the
# served frontend" — the deployed gateway forwards `/api/*` (XHR) and `/auth/*` (redirects)
# to the API on the same host, so the client resolves relative `/api/...` and `/auth/...` URLs.
ENV AGENTWEAVER_API_URL=""

ENTRYPOINT ["/bin/sh", "/app/docker-entrypoint.sh"]
