# Build stage
FROM node:22-alpine AS build
WORKDIR /app

# Copy package files and install dependencies
COPY package.json package-lock.json* ./
RUN npm ci

# Copy source and build
COPY . .
RUN npm run build

# Runtime stage — Caddy serving static assets
FROM caddy:alpine AS runtime

# Copy built assets
COPY --from=build /app/dist /srv

# SPA routing: all unmatched paths fall back to index.html.
# Assets (JS/CSS/etc.) get long-lived immutable cache headers.
RUN printf ':80 {\n\
    root * /srv\n\
    encode gzip br\n\
    @assets {\n\
        path *.js *.css *.woff *.woff2 *.ttf *.eot *.ico *.png *.jpg *.jpeg *.gif *.svg\n\
    }\n\
    header @assets Cache-Control "public, max-age=31536000, immutable"\n\
    try_files {path} /index.html\n\
    file_server\n\
}\n' > /etc/caddy/Caddyfile

# Caddy needs write access to /data (OCSP/cert cache) and /config (runtime config).
# Chown them so the process can run as non-root UID 1000.
RUN chown -R 1000:1000 /data /config

USER 1000

EXPOSE 80

CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
