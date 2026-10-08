FROM nginxinc/nginx-unprivileged@sha256:35d4ff1b94af60250f296e07700038da6ac0ae9f482964b8e6b82c0291f8b2eb

ARG IMAGE_TAG=dev
ARG GIT_SHA=unknown

LABEL org.opencontainers.image.version="${IMAGE_TAG}" \
      org.opencontainers.image.revision="${GIT_SHA}"

COPY --chown=101:101 dist/ /usr/share/nginx/html/
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker-entrypoint.sh /docker-entrypoint-web.sh

USER 101:101
EXPOSE 8080

ENTRYPOINT ["/bin/sh", "/docker-entrypoint-web.sh"]
CMD ["nginx", "-g", "daemon off;"]
