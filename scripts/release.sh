#!/usr/bin/env bash
# release.sh -- Semver release script for Agentweaver.
#
# Usage:
#   bash scripts/release.sh [major|minor|patch]
#   bash scripts/release.sh --help
#
# What this script does:
#   1. Validates clean working tree
#   2. Reads current version from VERSION file, bumps per argument
#   3. Writes new VERSION, commits: "chore(release): bump version to vX.Y.Z"
#   4. Creates annotated git tag vX.Y.Z
#   5. Pushes the release commit and tag to origin
#   6. Generates changelog from merged PRs since last tag
#   7. Creates GitHub Release via gh
#   8. Determines which images changed since last tag
#   9. Builds changed images via az acr build
#  10. Retags unchanged images server-side via az acr import
#  11. Deploys with IMAGE_TAG=vX.Y.Z

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
FRONTEND_NODE_MODULES_DIR="${REPO_ROOT}/apps/web/node_modules"
FRONTEND_NODE_MODULES_BACKUP_DIR="${REPO_ROOT}.frontend-node_modules.$$"

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

trap cleanup_frontend_build_artifacts EXIT

# ---------------------------------------------------------------------------
# Help
# ---------------------------------------------------------------------------
if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  cat <<'EOF'
release.sh -- Agentweaver semver release script

Usage:
  bash scripts/release.sh [major|minor|patch]

Arguments:
  major   Bump the major version (e.g. 0.6.0 -> 1.0.0)
  minor   Bump the minor version (e.g. 0.6.0 -> 0.7.0)
  patch   Bump the patch version (e.g. 0.6.0 -> 0.6.1)

Environment variables (all optional):
  IDENTITY_CLIENT_ID   Azure workload identity client ID for deploy step
  TENANT_ID            Azure tenant ID for deploy step
  DRY_RUN=true         Print actions without executing them

Examples:
  bash scripts/release.sh patch   # bug fix release
  bash scripts/release.sh minor   # feature release
  bash scripts/release.sh major   # breaking change release
  DRY_RUN=true bash scripts/release.sh patch   # preview changes only

What the script does:
  1.  Validates clean working tree (no uncommitted changes)
  2.  Reads current version from VERSION file, bumps per argument
  3.  Writes new VERSION, commits: "chore(release): bump version to vX.Y.Z"
  4.  Creates annotated git tag vX.Y.Z
  5.  Pushes the release commit and tag to origin
  6.  Generates changelog from merged PRs since last tag
  7.  Creates GitHub Release via gh release create
  8.  Determines which images changed since last tag (git diff --name-only)
  9.  Builds changed images via az acr build
  10. Retags unchanged images server-side via az acr import
  11. Deploys: IMAGE_TAG=vX.Y.Z bash scripts/aks/30-deploy.sh

To verify what version is deployed:
  kubectl get deployment agentweaver-api -n agentweaver \
    -o jsonpath='{.spec.template.spec.containers[0].image}'
EOF
  exit 0
fi

# ---------------------------------------------------------------------------
# Argument validation
# ---------------------------------------------------------------------------
BUMP="${1:-}"
if [[ "${BUMP}" != "major" && "${BUMP}" != "minor" && "${BUMP}" != "patch" ]]; then
  echo "ERROR: argument must be one of: major, minor, patch" >&2
  echo "       Run 'bash scripts/release.sh --help' for usage." >&2
  exit 1
fi

DRY_RUN="${DRY_RUN:-false}"
run() {
  if [[ "${DRY_RUN}" == "true" ]]; then
    echo "[dry-run] $*"
  else
    "$@"
  fi
}

# ---------------------------------------------------------------------------
# 1. Validate clean working tree
# ---------------------------------------------------------------------------
echo "==> Checking working tree..."
cd "${REPO_ROOT}"
if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "ERROR: working tree has uncommitted changes. Commit or stash first." >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# 2. Read and bump version
# ---------------------------------------------------------------------------
VERSION_FILE="${REPO_ROOT}/VERSION"
if [[ ! -f "${VERSION_FILE}" ]]; then
  echo "ERROR: VERSION file not found at ${VERSION_FILE}" >&2
  exit 1
fi
CURRENT_VERSION="$(cat "${VERSION_FILE}" | tr -d '[:space:]')"
if [[ ! "${CURRENT_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "ERROR: VERSION file contains invalid semver: '${CURRENT_VERSION}'" >&2
  exit 1
fi

IFS='.' read -r MAJOR MINOR PATCH <<< "${CURRENT_VERSION}"
case "${BUMP}" in
  major) MAJOR=$(( MAJOR + 1 )); MINOR=0; PATCH=0 ;;
  minor) MINOR=$(( MINOR + 1 )); PATCH=0 ;;
  patch) PATCH=$(( PATCH + 1 )) ;;
esac
NEW_VERSION="${MAJOR}.${MINOR}.${PATCH}"
NEW_TAG="v${NEW_VERSION}"

echo "==> Bumping version: ${CURRENT_VERSION} -> ${NEW_VERSION} (${BUMP})"

# ---------------------------------------------------------------------------
# Find last tag (used for changelog date and diff)
# ---------------------------------------------------------------------------
LAST_TAG="$(git describe --tags --abbrev=0 2>/dev/null || true)"
if [[ -z "${LAST_TAG}" ]]; then
  echo "  (no previous tag found; treating first commit as baseline)"
  LAST_TAG_DATE="1970-01-01T00:00:00Z"
  LAST_TAG_COMMIT="$(git rev-list --max-parents=0 HEAD)"
else
  echo "  Last tag: ${LAST_TAG}"
  LAST_TAG_DATE="$(git log -1 --format=%aI "${LAST_TAG}")"
  LAST_TAG_COMMIT="${LAST_TAG}"
fi

# ---------------------------------------------------------------------------
# 3. Write new VERSION and commit
# ---------------------------------------------------------------------------
echo "==> Writing VERSION file..."
run bash -c "printf '%s\n' '${NEW_VERSION}' > '${VERSION_FILE}'"

echo "==> Committing version bump..."
run git add "${VERSION_FILE}"
run git commit -m "chore(release): bump version to ${NEW_TAG}"

# ---------------------------------------------------------------------------
# 4. Create annotated git tag
# ---------------------------------------------------------------------------
echo "==> Creating annotated tag ${NEW_TAG}..."
run git tag -a "${NEW_TAG}" -m "Release ${NEW_TAG}"

# ---------------------------------------------------------------------------
# 5. Push release commit and tag
# ---------------------------------------------------------------------------
echo "==> Pushing release commit and tag to origin..."
run git push origin HEAD
run git push origin "${NEW_TAG}"

# ---------------------------------------------------------------------------
# 6. Generate changelog
# ---------------------------------------------------------------------------
echo "==> Generating changelog from merged PRs since ${LAST_TAG_DATE}..."
CHANGELOG=""
if command -v gh >/dev/null 2>&1; then
  CHANGELOG="$(gh pr list \
    --repo sabbour/agentweaver \
    --state merged \
    --search "merged:>${LAST_TAG_DATE}" \
    --json number,title,mergedAt \
    --jq '.[] | "- \(.title) (#\(.number))"' 2>/dev/null || true)"
fi
if [[ -z "${CHANGELOG}" ]]; then
  CHANGELOG="No pull requests found since ${LAST_TAG}."
fi
echo "${CHANGELOG}"

# ---------------------------------------------------------------------------
# 7. Create GitHub Release
# ---------------------------------------------------------------------------
echo "==> Creating GitHub release ${NEW_TAG}..."
run gh release create "${NEW_TAG}" \
  --title "${NEW_TAG}" \
  --notes "${CHANGELOG}"

# ---------------------------------------------------------------------------
# 8. Determine changed images
# ---------------------------------------------------------------------------
COMMON_DOTNET_PATHS=(
  "agentweaver.sln"
  "global.json"
  "Directory.Build.props"
  "Directory.Packages.props"
  "NuGet.config"
  "packages"
)

declare -A IMAGE_PATHS
IMAGE_PATHS["agentweaver-api"]="apps/Agentweaver.Api ${COMMON_DOTNET_PATHS[*]}"
IMAGE_PATHS["agentweaver-frontend"]="apps/web apps/Agentweaver.Web"
IMAGE_PATHS["agentweaver-mcp"]="apps/Agentweaver.Mcp ${COMMON_DOTNET_PATHS[*]}"
IMAGE_PATHS["agentweaver-agent-host"]="apps/Agentweaver.AgentHost ${COMMON_DOTNET_PATHS[*]}"

declare -A IMAGE_DOCKERFILES
IMAGE_DOCKERFILES["agentweaver-api"]="apps/Agentweaver.Api/Dockerfile"
IMAGE_DOCKERFILES["agentweaver-frontend"]="apps/web/Dockerfile"
IMAGE_DOCKERFILES["agentweaver-mcp"]="apps/Agentweaver.Mcp/Dockerfile"
IMAGE_DOCKERFILES["agentweaver-agent-host"]="apps/Agentweaver.AgentHost/Dockerfile"

# Source variables to get ACR_NAME etc.
# shellcheck source=aks/00-variables.sh
source "${SCRIPT_DIR}/aks/00-variables.sh"

image_changed_since_tag() {
  local paths_str="$1"
  # shellcheck disable=SC2086
  if git diff --quiet "${LAST_TAG_COMMIT}" HEAD -- ${paths_str} 2>/dev/null; then
    return 1  # not changed
  fi
  return 0  # changed
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
    exit 1
  fi

  echo "  [frontend] Building local dist/ before az acr build"
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

# ---------------------------------------------------------------------------
# 9 & 10. Build changed images / retag unchanged images
# ---------------------------------------------------------------------------
echo ""
echo "==> Processing images for ${NEW_TAG} (previous: ${LAST_TAG:-none})..."
PIDS=()
JOBS=()

if [[ "${DRY_RUN}" != "true" ]] && { [[ -z "${LAST_TAG}" ]] || image_changed_since_tag "${IMAGE_PATHS[agentweaver-frontend]}"; }; then
  # All images share the repo root as the ACR build context, so move frontend
  # node_modules out of that context before any parallel az acr build starts.
  prepare_frontend_dist
fi

for IMAGE in "agentweaver-api" "agentweaver-frontend" "agentweaver-mcp" "agentweaver-agent-host"; do
  DOCKERFILE="${IMAGE_DOCKERFILES[$IMAGE]}"
  PATHS="${IMAGE_PATHS[$IMAGE]}"

  if [[ -z "${LAST_TAG}" ]] || image_changed_since_tag "${PATHS}"; then
    echo "  [build]  ${IMAGE} (changed)"
    run az acr build \
      --registry "${ACR_NAME}" \
      --resource-group "${RESOURCE_GROUP}" \
      --image "${IMAGE}:${NEW_TAG}" \
      --file "${DOCKERFILE}" \
      --build-arg "IMAGE_TAG=${NEW_TAG}" \
      --build-arg "GIT_SHA=$(git rev-parse HEAD)" \
      . &
    PIDS+=("$!")
    JOBS+=("build:${IMAGE}:${NEW_TAG}")
  else
    echo "  [retag]  ${IMAGE} (unchanged, retagging ${LAST_TAG} -> ${NEW_TAG})"
    run az acr import \
      --name "${ACR_NAME}" \
      --source "${ACR_LOGIN_SERVER}/${IMAGE}:${LAST_TAG}" \
      --image "${IMAGE}:${NEW_TAG}" \
      --force &
    PIDS+=("$!")
    JOBS+=("retag:${IMAGE}:${NEW_TAG}")
  fi
done

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
        echo "  [OK]   ${job}"
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
if [[ "${DRY_RUN}" != "true" ]] && ! wait_for_image_jobs; then
  echo "ERROR: one or more image jobs failed." >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# 10. Deploy
# ---------------------------------------------------------------------------
echo ""
echo "==> Deploying ${NEW_TAG} to AKS..."
run env \
  IMAGE_TAG="${NEW_TAG}" \
  IDENTITY_CLIENT_ID="${IDENTITY_CLIENT_ID:-}" \
  TENANT_ID="${TENANT_ID:-}" \
  bash "${SCRIPT_DIR}/aks/30-deploy.sh"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo ""
echo "==================================================="
echo " RELEASE ${NEW_TAG} COMPLETE"
echo "==================================================="
echo ""
echo "  GitHub Release: https://github.com/sabbour/agentweaver/releases/tag/${NEW_TAG}"
echo ""
echo "To verify what version is deployed:"
echo "  kubectl get deployment agentweaver-api -n agentweaver \\"
echo "    -o jsonpath='{.spec.template.spec.containers[0].image}'"
