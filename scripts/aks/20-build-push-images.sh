#!/usr/bin/env bash
# 20-build-push-images.sh -- Build and push Agentweaver container images to ACR.
#
# Builds four images using 'az acr build' (no local Docker daemon required), or
# retags unchanged images with 'az acr import' when a previous deployed tag is known:
#   agentweaver-api      -- .NET 10 API         (context: repo root, Dockerfile: apps/Agentweaver.Api/Dockerfile)
#   agentweaver-frontend -- ASP.NET Core + SPA   (context: repo root, Dockerfile: apps/web/Dockerfile)
#   agentweaver-mcp      -- MCP server           (context: repo root, Dockerfile: apps/Agentweaver.Mcp/Dockerfile)
#   agentweaver-agent-host -- pod-per-run AgentHost (context: repo root, Dockerfile: apps/Agentweaver.AgentHost/Dockerfile)
#
# All images use the repo root as build context because their Dockerfiles reference
# multiple subdirectories.
#
# Usage:
#   source scripts/aks/00-variables.sh
#   bash scripts/aks/20-build-push-images.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# shellcheck source=00-variables.sh
source "${SCRIPT_DIR}/00-variables.sh"

TARGET_GIT_REF="${TARGET_GIT_REF:-${IMAGE_TAG}}"

echo ""
echo "=== Building, retagging, and pushing Agentweaver images ==="
echo "  ACR:                 ${ACR_LOGIN_SERVER}"
echo "  Image tag:           ${IMAGE_TAG}"
echo "  AgentHost image tag: ${AGENTHOST_IMAGE_TAG}"
echo ""
echo "  Redeploy efficiency:"
echo "    - If PREVIOUS_IMAGE_TAG or a current cluster image tag is available, unchanged"
echo "      images are retagged with 'az acr import' instead of rebuilt."
echo "    - Changed images are built in parallel with 'az acr build'."
echo "    - Set FORCE_REBUILD=true to rebuild every image."
echo ""

cd "${REPO_ROOT}"

current_deployment_tag() {
  local deployment="$1"
  if command -v kubectl >/dev/null 2>&1; then
    kubectl get deployment "${deployment}" \
      --namespace "${NAMESPACE}" \
      --output jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null \
      | awk -F: 'NF>1 {print $NF}' || true
  fi
}

current_agenthost_tag() {
  if command -v kubectl >/dev/null 2>&1; then
    kubectl get sandboxtemplate agentweaver-agent-host \
      --namespace "${NAMESPACE}" \
      --output jsonpath='{.spec.podTemplate.spec.containers[0].image}' 2>/dev/null \
      | awk -F: 'NF>1 {print $NF}' || true
  fi
}

can_diff_refs() {
  local old_ref="$1"
  local new_ref="$2"
  git rev-parse --verify "${old_ref}^{commit}" >/dev/null 2>&1 &&
    git rev-parse --verify "${new_ref}^{commit}" >/dev/null 2>&1
}

paths_changed() {
  local old_ref="$1"
  local new_ref="$2"
  shift 2
  if ! can_diff_refs "${old_ref}" "${new_ref}"; then
    return 0
  fi
  ! git diff --quiet "${old_ref}" "${new_ref}" -- "$@"
}

build_image() {
  local image="$1"
  local tag="$2"
  local dockerfile="$3"
  echo "--- Building ${image}:${tag} (${dockerfile}) ---"
  az acr build \
    --registry "${ACR_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --image "${image}:${tag}" \
    --file "${dockerfile}" \
    .
  echo "  [built]  ${ACR_LOGIN_SERVER}/${image}:${tag}"
}

retag_image() {
  local image="$1"
  local source_tag="$2"
  local target_tag="$3"
  if [[ "${source_tag}" == "${target_tag}" ]]; then
    echo "  [skip]   ${image}:${target_tag} already points at the deployed tag"
    return 0
  fi
  echo "--- Retagging ${image}:${source_tag} -> ${image}:${target_tag} ---"
  az acr import \
    --registry "${ACR_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --source "${ACR_LOGIN_SERVER}/${image}:${source_tag}" \
    --image "${image}:${target_tag}" \
    --force \
    --output none
  echo "  [retag]  ${ACR_LOGIN_SERVER}/${image}:${target_tag}"
}

schedule_image() {
  local image="$1"
  local target_tag="$2"
  local dockerfile="$3"
  local deployed_tag="$4"
  shift 4
  local paths=("$@")
  local source_tag="${PREVIOUS_IMAGE_TAG:-${deployed_tag}}"

  if [[ "${FORCE_REBUILD:-false}" == "true" || -z "${source_tag}" ]]; then
    build_image "${image}" "${target_tag}" "${dockerfile}" &
  elif paths_changed "${source_tag}" "${TARGET_GIT_REF}" "${paths[@]}"; then
    build_image "${image}" "${target_tag}" "${dockerfile}" &
  else
    retag_image "${image}" "${source_tag}" "${target_tag}" &
  fi
  PIDS+=("$!")
  JOBS+=("${image}:${target_tag}")
}

COMMON_DOTNET_PATHS=(
  "agentweaver.sln"
  "global.json"
  "Directory.Build.props"
  "Directory.Packages.props"
  "NuGet.config"
  "packages"
)

PIDS=()
JOBS=()

schedule_image \
  "agentweaver-api" \
  "${IMAGE_TAG}" \
  "apps/Agentweaver.Api/Dockerfile" \
  "$(current_deployment_tag agentweaver-api)" \
  "${COMMON_DOTNET_PATHS[@]}" \
  "apps/Agentweaver.Api"

schedule_image \
  "agentweaver-frontend" \
  "${IMAGE_TAG}" \
  "apps/web/Dockerfile" \
  "$(current_deployment_tag agentweaver-frontend)" \
  "apps/web" \
  "apps/Agentweaver.Web"

schedule_image \
  "agentweaver-mcp" \
  "${IMAGE_TAG}" \
  "apps/Agentweaver.Mcp/Dockerfile" \
  "$(current_deployment_tag agentweaver-mcp)" \
  "${COMMON_DOTNET_PATHS[@]}" \
  "apps/Agentweaver.Mcp"

schedule_image \
  "agentweaver-agent-host" \
  "${AGENTHOST_IMAGE_TAG}" \
  "apps/Agentweaver.AgentHost/Dockerfile" \
  "$(current_agenthost_tag)" \
  "${COMMON_DOTNET_PATHS[@]}" \
  "apps/Agentweaver.AgentHost"

echo ""
echo "Waiting for image jobs to finish..."
FAILED=0
for i in "${!PIDS[@]}"; do
  if wait "${PIDS[$i]}"; then
    echo "  [OK] ${JOBS[$i]}"
  else
    echo "  [FAIL] ${JOBS[$i]}" >&2
    FAILED=1
  fi
done

if [[ "${FAILED}" -ne 0 ]]; then
  echo "ERROR: one or more image jobs failed." >&2
  exit 1
fi

# -- Summary ------------------------------------------------------------------
echo ""
echo "==================================================="
echo " IMAGES READY IN ACR"
echo "==================================================="
echo ""
echo "  ${ACR_LOGIN_SERVER}/agentweaver-api:${IMAGE_TAG}"
echo "  ${ACR_LOGIN_SERVER}/agentweaver-frontend:${IMAGE_TAG}"
echo "  ${ACR_LOGIN_SERVER}/agentweaver-mcp:${IMAGE_TAG}"
echo "  ${ACR_LOGIN_SERVER}/agentweaver-agent-host:${AGENTHOST_IMAGE_TAG}"
echo ""
echo "Export for deploy step:"
echo "  export ACR_NAME=${ACR_NAME}"
echo "  export IMAGE_TAG=${IMAGE_TAG}"
echo "  export AGENTHOST_IMAGE_TAG=${AGENTHOST_IMAGE_TAG}"
echo ""
echo "  Next step:"
echo "    bash scripts/aks/30-deploy.sh"
