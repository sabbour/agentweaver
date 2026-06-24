# Build stage
FROM node:22-alpine AS build
WORKDIR /app

# Copy package files and install dependencies
COPY package.json package-lock.json* ./
RUN npm ci

# Copy source and build
COPY . .
RUN npm run build

# Runtime stage — minimal nginx serving static assets
FROM nginx:alpine AS runtime

# Create non-root user (UID 1000) and configure nginx to run as it
RUN addgroup --gid 1000 appgroup \
    && adduser --uid 1000 --ingroup appgroup --disabled-password --gecos "" appuser

# Copy built assets
COPY --from=build /app/dist /usr/share/nginx/html

# nginx config: listen on port 80, serve SPA with fallback to index.html
RUN printf 'server {\n\
    listen 80;\n\
    server_name _;\n\
    root /usr/share/nginx/html;\n\
    index index.html;\n\
    location ~* \\.(js|css|png|jpg|jpeg|gif|ico|svg|woff|woff2|ttf|eot)$ {\n\
        expires 1y;\n\
        add_header Cache-Control "public, immutable";\n\
        try_files $uri =404;\n\
    }\n\
    location / {\n\
        try_files $uri $uri/ /index.html;\n\
    }\n\
}\n' > /etc/nginx/conf.d/default.conf

# Allow nginx to write its pid and temp files as non-root
RUN chown -R appuser:appgroup /var/cache/nginx /var/log/nginx /run \
    && sed -i 's|/var/run/nginx.pid|/run/nginx.pid|' /etc/nginx/nginx.conf \
    && sed -i 's|^user.*||' /etc/nginx/nginx.conf

USER 1000

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
