# syntax=docker/dockerfile:1
# Stage 1: Build React SPA
# Build context must be the repo root so both apps/web/ and apps/Agentweaver.Web/ are accessible.
FROM node:22-alpine AS web-build
WORKDIR /app
COPY apps/web/package.json apps/web/package-lock.json* apps/web/.npmrc ./
# The 1JS FluentAI packages resolve from a private Azure Artifacts feed (see
# apps/web/.npmrc) that requires auth. Pass it as a BuildKit secret (never
# baked into an image layer): docker build --secret id=npmrc,src=<path-to-.npmrc-with-token>
# e.g. `az artifacts universal ...` / `vsts-npm-auth -config apps/web/.npmrc -F` writes a
# user-scoped .npmrc with the token locally; point --secret at that file.
# --legacy-peer-deps: internal @1js/fluentai-family packages carry mutually
# conflicting @fluentui/react-icons peer ranges (e.g. ^2.0.324 vs ^2.0.332) that
# cannot be satisfied by any single pin; the resolved tree builds and runs fine.
RUN --mount=type=secret,id=npmrc,target=/root/.npmrc,required=false \
    if [ -s /root/.npmrc ]; then cat /root/.npmrc >> .npmrc; fi && \
    npm ci --legacy-peer-deps --userconfig ./.npmrc
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
# API_URL is the API ORIGIN only (no `/api` suffix). Empty string means "same origin as the
# served frontend" — the deployed gateway forwards `/api/*` (XHR) and `/auth/*` (redirects)
# to the API on the same host, so the client resolves relative `/api/...` and `/auth/...` URLs.
ENV AGENTWEAVER_API_URL=""

ENTRYPOINT ["/bin/sh", "/app/docker-entrypoint.sh"]
