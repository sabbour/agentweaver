#!/usr/bin/env bash
# 20-build-push-images.sh -- Build and push Agentweaver container images to ACR.
#
# Builds two images using 'az acr build' (no local Docker daemon required):
#   agentweaver-api      -- .NET 10 API         (context: repo root, Dockerfile: apps/Agentweaver.Api/Dockerfile)
#   agentweaver-frontend -- ASP.NET Core + SPA   (context: repo root, Dockerfile: apps/web/Dockerfile)
#
# Both images use the repo root as build context because both Dockerfiles reference
# multiple subdirectories (apps/ and packages/ for the API; apps/web/ and
# apps/Agentweaver.Web/ for the frontend).
#
# Usage:
#   source scripts/aks/00-variables.sh
#   bash scripts/aks/20-build-push-images.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# shellcheck source=00-variables.sh
source "${SCRIPT_DIR}/00-variables.sh"

echo ""
echo "=== Building and pushing Agentweaver images ==="
echo "  ACR:       ${ACR_LOGIN_SERVER}"
echo "  Image tag: ${IMAGE_TAG}"
echo ""

cd "${REPO_ROOT}"

# -- Image 1: agentweaver-api -------------------------------------------------
# Build context: repo root (contains apps/ and packages/ needed by the Dockerfile)
# Dockerfile: apps/Agentweaver.Api/Dockerfile
echo "--- Building agentweaver-api ---"
az acr build \
  --registry "${ACR_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --image "agentweaver-api:${IMAGE_TAG}" \
  --file "apps/Agentweaver.Api/Dockerfile" \
  .

echo ""
echo "  Pushed: ${ACR_LOGIN_SERVER}/agentweaver-api:${IMAGE_TAG}"

# -- Image 2: agentweaver-frontend --------------------------------------------
# Build context: repo root (Dockerfile needs both apps/web/ and apps/Agentweaver.Web/)
# Dockerfile: apps/web/Dockerfile
echo ""
echo "--- Building agentweaver-frontend ---"
az acr build \
  --registry "${ACR_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --image "agentweaver-frontend:${IMAGE_TAG}" \
  --file "apps/web/Dockerfile" \
  .

echo ""
echo "  Pushed: ${ACR_LOGIN_SERVER}/agentweaver-frontend:${IMAGE_TAG}"

# -- Summary ------------------------------------------------------------------
echo ""
echo "==================================================="
echo " IMAGES BUILT AND PUSHED"
echo "==================================================="
echo ""
echo "  ${ACR_LOGIN_SERVER}/agentweaver-api:${IMAGE_TAG}"
echo "  ${ACR_LOGIN_SERVER}/agentweaver-frontend:${IMAGE_TAG}"
echo ""
echo "Export for deploy step:"
echo "  export ACR_NAME=${ACR_NAME}"
echo "  export IMAGE_TAG=${IMAGE_TAG}"
echo ""
echo "  Next step:"
echo "    bash scripts/aks/30-deploy.sh"
