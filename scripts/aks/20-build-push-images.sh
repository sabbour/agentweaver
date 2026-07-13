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

# --- UTF-8-safe az wrapper (Windows) -----------------------------------------
# The Azure CLI MSI launcher runs `python.exe -IBm azure.cli`. The -I (isolated)
# flag implies -E, so Python IGNORES the PYTHONUTF8 / PYTHONIOENCODING env vars.
# When az's stdout is redirected to a pipe/file (as in CI, or under Git Bash),
# Python then falls back to the host ANSI code page (cp1252 on US-English Windows)
# and `az acr build` CRASHES while streaming Unicode build-log glyphs — e.g. the
# vite '✓' (U+2713) checkmark — through colorama:
#     UnicodeEncodeError: 'charmap' codec can't encode character '\u2713'
# Setting PYTHONUTF8/PYTHONIOENCODING does NOT help because -I discards them.
# Root-cause fix: bypass the -I launcher and invoke the bundled interpreter
# directly with `-X utf8` (a command-line flag, which -I cannot suppress). This
# enables Python UTF-8 mode even for redirected stdio, so log streaming succeeds.
# On Linux/macOS (UTF-8 locale) the stock `az` is fine, so only override on Windows.
AZ_PYEXE=""
case "$(uname -s 2>/dev/null || echo unknown)" in
  MINGW*|MSYS*|CYGWIN*|Windows_NT)
    _az_launcher="$(command -v az 2>/dev/null || true)"
    if [[ -n "${_az_launcher}" ]]; then
      _az_py="$(dirname "${_az_launcher}")/../python.exe"
      [[ -f "${_az_py}" ]] && AZ_PYEXE="${_az_py}"
    fi
    ;;
esac

# Shell function shadowing `az`: all `az ...` calls below route through the
# UTF-8-safe interpreter on Windows, or the stock CLI everywhere else.
az() {
  if [[ -n "${AZ_PYEXE}" ]]; then
    AZ_INSTALLER=MSI "${AZ_PYEXE}" -X utf8 -B -m azure.cli "$@"
  else
    command az "$@"
  fi
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
FRONTEND_NODE_MODULES_DIR="${REPO_ROOT}/apps/web/node_modules"
FRONTEND_NODE_MODULES_BACKUP_DIR="${REPO_ROOT}.frontend-node_modules.$$"
# shellcheck source=00-variables.sh
source "${SCRIPT_DIR}/00-variables.sh"
trap cleanup_frontend_build_artifacts EXIT

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

frontend_npm_password_b64() {
  if [[ -n "${AZURE_ARTIFACTS_NPM_PASSWORD_B64:-}" ]]; then
    printf '%s' "${AZURE_ARTIFACTS_NPM_PASSWORD_B64}"
    return 0
  fi

  if [[ -n "${AZURE_ARTIFACTS_NPM_PAT:-}" ]]; then
    node -e "process.stdout.write(Buffer.from(process.argv[1], 'utf8').toString('base64'))" "${AZURE_ARTIFACTS_NPM_PAT}"
    return 0
  fi

  return 1
}

frontend_npm_userconfig() {
  local home_npmrc="${HOME:-}/.npmrc"
  local build_npmrc="${REPO_ROOT}/apps/web/.npmrc.build"
  local password_b64=""

  if password_b64="$(frontend_npm_password_b64 2>/dev/null)"; then
    cp "${REPO_ROOT}/apps/web/.npmrc" "${build_npmrc}"
    printf '%s\n' \
      '; begin auth token' \
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/registry/:username=agentweaver' \
      "//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/registry/:_password=${password_b64}" \
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/registry/:email=npm requires email to be set but does not use the value' \
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/:username=agentweaver' \
      "//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/:_password=${password_b64}" \
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/:email=npm requires email to be set but does not use the value' \
      '; end auth token' >> "${build_npmrc}"
    printf '%s' "${build_npmrc}"
    return 0
  fi

  if [[ -f "${home_npmrc}" ]] && grep -q -E '^//pkgs\.dev\.azure\.com/office/Office/_packaging/1JS/npm(/registry)?/:_password=' "${home_npmrc}"; then
    printf '%s' "${home_npmrc}"
    return 0
  fi

  return 1
}

cleanup_frontend_npmrc_build() {
  rm -f "${REPO_ROOT}/apps/web/.npmrc.build"
}

stash_frontend_node_modules_outside_acr_context() {
  if [[ ! -d "${FRONTEND_NODE_MODULES_DIR}" ]]; then
    return 0
  fi

  rm -rf "${FRONTEND_NODE_MODULES_BACKUP_DIR}"
  mv "${FRONTEND_NODE_MODULES_DIR}" "${FRONTEND_NODE_MODULES_BACKUP_DIR}"
  echo "  [frontend] Temporarily moved node_modules out of the ACR build context"
}

restore_frontend_node_modules() {
  if [[ ! -e "${FRONTEND_NODE_MODULES_BACKUP_DIR}" ]]; then
    return 0
  fi

  rm -rf "${FRONTEND_NODE_MODULES_DIR}"
  mv "${FRONTEND_NODE_MODULES_BACKUP_DIR}" "${FRONTEND_NODE_MODULES_DIR}"
}

cleanup_frontend_build_artifacts() {
  cleanup_frontend_npmrc_build
  restore_frontend_node_modules
}

run_frontend_npm_credential_provider() {
  local uname_s
  uname_s="$(uname -s 2>/dev/null || echo unknown)"
  if [[ "${uname_s}" == "Linux" ]]; then
    echo "ERROR: interactive frontend feed auth is currently unavailable on Linux/WSL in this script." >&2
    echo "  The ado-npm-auth fallback bundles artifacts-credprovider v1.4.1 but requests a RID-specific" >&2
    echo "  Microsoft.Net8.<rid>.NuGet.CredentialProvider.tar.gz asset that GitHub serves as a non-gzip error page." >&2
    echo "  That is why previous runs ended with 'gzip: stdin: not in gzip format'." >&2
    echo "  Export AZURE_ARTIFACTS_NPM_PAT (preferred), AZURE_ARTIFACTS_NPM_PASSWORD_B64, or refresh ~/.npmrc" >&2
    echo "  with a valid 1JS feed token before rerunning the build." >&2
    return 1
  fi

  npm_config_registry=https://registry.npmjs.org npx --yes ado-npm-auth -c "${REPO_ROOT}/apps/web/.npmrc"
}

prepare_frontend_dist() {
  if ! command -v npm >/dev/null 2>&1; then
    echo "ERROR: npm is required to build apps/web before az acr build." >&2
    return 1
  fi

  echo "--- Building local frontend assets for agentweaver-frontend ---"
  local userconfig=""
  if userconfig="$(frontend_npm_userconfig 2>/dev/null)"; then
    echo "  [frontend] Using PAT-backed npm userconfig outside the Docker context"
  else
    echo "  [frontend] No PAT-backed npm userconfig found; attempting interactive auth helper"
  fi

  (
    cd "${REPO_ROOT}/apps/web"
    if [[ -n "${userconfig}" ]]; then
      NPM_CONFIG_USERCONFIG="${userconfig}" npm ci --legacy-peer-deps
    else
      run_frontend_npm_credential_provider
      npm ci --legacy-peer-deps
    fi
    unset VITE_API_URL VITE_API_KEY
    npm run build
  )

  cleanup_frontend_npmrc_build
  # Keep prebuilt dist/ but move node_modules out of the repo before az acr build:
  # az's context tar step can choke on broken symlinks even when .dockerignore excludes them.
  stash_frontend_node_modules_outside_acr_context
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
    --output none \
    .
  echo "  [built]  ${ACR_LOGIN_SERVER}/${image}:${tag}"
  # Also tag as latest-release so it always points at the most recently built version
  retag_image "${image}" "${tag}" "latest-release"
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
    --name "${ACR_NAME}" \
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

API_DEPLOYED_TAG="$(current_deployment_tag agentweaver-api)"
FRONTEND_DEPLOYED_TAG="$(current_deployment_tag agentweaver-frontend)"
MCP_DEPLOYED_TAG="$(current_deployment_tag agentweaver-mcp)"
AGENTHOST_DEPLOYED_TAG="$(current_agenthost_tag)"

FRONTEND_SOURCE_TAG="${PREVIOUS_IMAGE_TAG:-${FRONTEND_DEPLOYED_TAG}}"
if [[ "${FORCE_REBUILD:-false}" == "true" || -z "${FRONTEND_SOURCE_TAG}" ]] || \
  paths_changed "${FRONTEND_SOURCE_TAG}" "${TARGET_GIT_REF}" "apps/web" "apps/Agentweaver.Web"; then
  # All images share the repo root as the ACR build context, so move frontend
  # node_modules out of that context before any parallel az acr build starts.
  prepare_frontend_dist
fi

schedule_image \
  "agentweaver-api" \
  "${IMAGE_TAG}" \
  "apps/Agentweaver.Api/Dockerfile" \
  "${API_DEPLOYED_TAG}" \
  "${COMMON_DOTNET_PATHS[@]}" \
  "apps/Agentweaver.Api"

schedule_image \
  "agentweaver-frontend" \
  "${IMAGE_TAG}" \
  "apps/web/Dockerfile" \
  "${FRONTEND_DEPLOYED_TAG}" \
  "apps/web" \
  "apps/Agentweaver.Web"

schedule_image \
  "agentweaver-mcp" \
  "${IMAGE_TAG}" \
  "apps/Agentweaver.Mcp/Dockerfile" \
  "${MCP_DEPLOYED_TAG}" \
  "${COMMON_DOTNET_PATHS[@]}" \
  "apps/Agentweaver.Mcp"

schedule_image \
  "agentweaver-agent-host" \
  "${AGENTHOST_IMAGE_TAG}" \
  "apps/Agentweaver.AgentHost/Dockerfile" \
  "${AGENTHOST_DEPLOYED_TAG}" \
  "${COMMON_DOTNET_PATHS[@]}" \
  "apps/Agentweaver.AgentHost"

terminate_remaining_jobs() {
  local failed_pid="$1"
  local i
  for i in "${!PIDS[@]}"; do
    local pid="${PIDS[$i]}"
    if [[ "${pid}" == "${failed_pid}" ]]; then
      continue
    fi
    if kill -0 "${pid}" 2>/dev/null; then
      echo "  [STOP] ${JOBS[$i]}" >&2
      kill "${pid}" 2>/dev/null || true
    fi
  done

  for i in "${!PIDS[@]}"; do
    local pid="${PIDS[$i]}"
    if [[ "${pid}" == "${failed_pid}" ]]; then
      continue
    fi
    wait "${pid}" 2>/dev/null || true
  done
}

wait_for_image_jobs() {
  local pending_pids=("${PIDS[@]}")
  local pending_jobs=("${JOBS[@]}")
  local next_pids=()
  local next_jobs=()
  local i

  while [[ "${#pending_pids[@]}" -gt 0 ]]; do
    next_pids=()
    next_jobs=()
    for i in "${!pending_pids[@]}"; do
      local pid="${pending_pids[$i]}"
      local job="${pending_jobs[$i]}"
      if kill -0 "${pid}" 2>/dev/null; then
        next_pids+=("${pid}")
        next_jobs+=("${job}")
        continue
      fi

      if wait "${pid}"; then
        echo "  [OK] ${job}"
      else
        echo "  [FAIL] ${job}" >&2
        terminate_remaining_jobs "${pid}"
        return 1
      fi
    done

    pending_pids=("${next_pids[@]}")
    pending_jobs=("${next_jobs[@]}")
    if [[ "${#pending_pids[@]}" -gt 0 ]]; then
      sleep 1
    fi
  done
}

echo ""
echo "Waiting for image jobs to finish..."
if ! wait_for_image_jobs; then
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
