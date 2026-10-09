FROM mcr.microsoft.com/dotnet/aspnet@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4

ARG IMAGE_TAG=dev
ARG GIT_SHA=unknown

LABEL org.opencontainers.image.version="${IMAGE_TAG}" \
      org.opencontainers.image.revision="${GIT_SHA}"

WORKDIR /app
COPY publish/ ./
COPY wwwroot/ ./wwwroot/

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Agentweaver.Web.Host.dll"]
