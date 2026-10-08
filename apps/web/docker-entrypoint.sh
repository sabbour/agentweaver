#!/bin/sh
set -eu

encode_base64() {
  printf '%s' "$1" | base64 | tr -d '\r\n'
}

cat > /usr/share/nginx/html/env-config.js <<EOC
window.__AGENTWEAVER_CONFIG_BASE64__ = {
  GATEWAY_URL: "$(encode_base64 "${VITE_GATEWAY_URL:-/api/v1}")",
  IDENTITY_BROKER_URL: "$(encode_base64 "${VITE_IDENTITY_BROKER_URL:-}")",
  OAUTH_CLIENT_ID: "$(encode_base64 "${VITE_OAUTH_CLIENT_ID:-}")",
  OAUTH_REDIRECT_URI: "$(encode_base64 "${VITE_OAUTH_REDIRECT_URI:-}")",
  OAUTH_SCOPES: "$(encode_base64 "${VITE_OAUTH_SCOPES:-}")"
};
EOC

exec /docker-entrypoint.sh "$@"
