#!/bin/sh
set -eu
json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}
api_url="$(json_escape "${AGENTWEAVER_API_URL:-}")"
cat > /app/wwwroot/env-config.js <<EOC
window.__AGENTWEAVER_CONFIG__ = {
  API_URL: "${api_url}"
};
EOC
exec dotnet Agentweaver.Web.dll
