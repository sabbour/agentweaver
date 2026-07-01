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
#   5. Generates changelog from merged PRs since last tag
#   6. Creates GitHub Release via gh
#   7. Determines which images changed since last tag
#   8. Builds changed images via az acr build
#   9. Retags unchanged images server-side via az acr import
#  10. Deploys with IMAGE_TAG=vX.Y.Z

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

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
  5.  Generates changelog from merged PRs since last tag
  6.  Creates GitHub Release via gh release create
  7.  Determines which images changed since last tag (git diff --name-only)
  8.  Builds changed images via az acr build
  9.  Retags unchanged images server-side via az acr import
  10. Deploys: IMAGE_TAG=vX.Y.Z bash scripts/aks/30-deploy.sh

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
# 5. Generate changelog
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
# 6. Create GitHub Release
# ---------------------------------------------------------------------------
echo "==> Creating GitHub release ${NEW_TAG}..."
run gh release create "${NEW_TAG}" \
  --title "${NEW_TAG}" \
  --notes "${CHANGELOG}"

# ---------------------------------------------------------------------------
# 7. Determine changed images
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

# ---------------------------------------------------------------------------
# 8 & 9. Build changed images / retag unchanged images
# ---------------------------------------------------------------------------
echo ""
echo "==> Processing images for ${NEW_TAG} (previous: ${LAST_TAG:-none})..."
PIDS=()
JOBS=()

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

echo ""
echo "Waiting for image jobs to finish..."
FAILED=0
for i in "${!PIDS[@]}"; do
  if [[ "${DRY_RUN}" != "true" ]]; then
    if wait "${PIDS[$i]}"; then
      echo "  [OK]   ${JOBS[$i]}"
    else
      echo "  [FAIL] ${JOBS[$i]}" >&2
      FAILED=1
    fi
  fi
done
if [[ "${FAILED}" -ne 0 ]]; then
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
# Push tag and summary
# ---------------------------------------------------------------------------
echo ""
echo "==> Pushing commit and tag to origin..."
run git push origin HEAD
run git push origin "${NEW_TAG}"

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
