#!/usr/bin/env bash
# 20-build-push-images.sh -- Build and push Agentweaver container images to ACR.
# Keep in sync with 20-build-push-images.ps1 (PowerShell equivalent).
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
#
# Optional:
#   DRY_RUN=true PREVIOUS_IMAGE_TAG=vX.Y.Z bash scripts/aks/20-build-push-images.sh

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
DRY_RUN="${DRY_RUN:-false}"

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
echo "    - Set DRY_RUN=true to print the build/retag plan without invoking ACR or npm."
echo "    - Every build/retag is also stamped with a 'prov-<fullsha>' ACR tag recording"
echo "      the commit its content actually corresponds to (verify with"
echo "      scripts/aks/25-verify-image-provenance.sh after 30-deploy.sh)."
echo "    - Provenance stamping is REQUIRED: if the extra prov tag cannot be written,"
echo "      the image job fails rather than shipping an unverifiable release artifact."
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

# Resolves a release image tag to the commit which wrote that version to VERSION.
# Releases before v0.9.36 were tagged, but later deploys deliberately only updated VERSION.
# Looking up the version-bump commit preserves safe selective builds for both histories.
#
# Hardening (#251): more than one commit can end up writing the same VERSION
# value -- e.g. an out-of-band/poisoned build attempt that was superseded
# without a VERSION bump (see the v0.9.48-rc1 incident). If that happens we
# only trust the match when:
#   1) the repository is NOT shallow for the VERSION-history fallback, and
#   2) every commit that wrote this version is an ancestor of the selected
#      newest match (pairwise linear-history validation).
# If any match sits off that line we have no reliable way to know which commit
# actually produced the currently-deployed image, so we refuse to guess and
# return failure -- the caller (paths_changed/schedule_image) treats an
# unresolved source commit as "changed" and takes the safe full-rebuild path
# instead of risking a stale retag-forward.
release_ref_for_tag() {
  local tag="$1"
  local version="${tag#v}"
  local commit
  local -a matches=()

  if git rev-parse --verify "${tag}^{commit}" >/dev/null 2>&1; then
    git rev-parse --verify "${tag}^{commit}"
    return 0
  fi

  if [[ "$(git rev-parse --is-shallow-repository 2>/dev/null || echo false)" == "true" ]]; then
    echo "  [WARN] tag ${tag}: repository is shallow; refusing VERSION-based source resolution (forcing rebuild)" >&2
    return 1
  fi

  while IFS= read -r commit; do
    if [[ "$(git show "${commit}:VERSION" 2>/dev/null | tr -d '[:space:]')" == "${version}" ]]; then
      matches+=("${commit}")
    fi
  done < <(git log --format=%H --all -- VERSION)

  if [[ "${#matches[@]}" -eq 0 ]]; then
    return 1
  fi

  # git log --all lists newest-first, so matches[0] is the newest match. Every
  # other VERSION-writing commit must be an ancestor of that newest commit; if
  # any are not, VERSION history is ambiguous/diverged and we must rebuild.
  local newest="${matches[0]}"
  local candidate
  for candidate in "${matches[@]:1}"; do
    if ! git merge-base --is-ancestor "${candidate}" "${newest}" 2>/dev/null; then
      echo "  [WARN] tag ${tag}: multiple diverged commits wrote VERSION=${version}; refusing to guess source commit (forcing rebuild)" >&2
      return 1
    fi
  done

  printf '%s\n' "${newest}"
}

paths_changed() {
  local old_ref="$1"
  local new_ref="$2"
  shift 2
  if [[ -z "${old_ref}" || -z "${new_ref}" ]]; then
    return 0
  fi
  ! git diff --quiet "${old_ref}" "${new_ref}" -- "$@"
}

TARGET_COMMIT="$(git rev-parse --verify "${TARGET_GIT_REF}^{commit}" 2>/dev/null || \
  release_ref_for_tag "${IMAGE_TAG}" 2>/dev/null || git rev-parse HEAD)"

source_commit_for_tag() {
  local tag="$1"
  release_ref_for_tag "${tag}" 2>/dev/null || true
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
  if [[ "${DRY_RUN}" == "true" ]]; then
    echo "  [dry-run] Would build local frontend assets before ACR build"
    return 0
  fi

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

# Records, as an extra immutable ACR tag pointing at the same digest, which
# commit this image tag's content actually corresponds to (#251 ask #3:
# "stamp build SHA into image label"). This is deliberately independent of
# the build/retag decision above it, so a later, out-of-band check
# (25-verify-image-provenance.sh) can answer "what commit does the image
# currently deployed actually correspond to?" without re-trusting whatever
# paths_changed() decided at build time -- it protects against script bugs,
# manual 'az acr import' outside this script, or deploying an unexpected tag.
# Stamping is mandatory: shipping an image we cannot independently map back to
# source would recreate #251's blind spot, so any stamping failure fails the job.
# The prov tag uses the full 40-char commit SHA to avoid short-tag collisions.
stamp_provenance() {
  local image="$1"
  local tag="$2"
  local commit="$3"
  local resolved_commit=""
  if [[ -z "${commit}" ]]; then
    echo "ERROR: no resolvable commit for ${image}:${tag}; refusing to ship unstamped image" >&2
    return 1
  fi
  resolved_commit="$(git rev-parse --verify "${commit}^{commit}" 2>/dev/null || true)"
  if [[ -z "${resolved_commit}" ]]; then
    echo "ERROR: provenance commit '${commit}' for ${image}:${tag} is not resolvable in local git history" >&2
    return 1
  fi
  local prov_tag="prov-${resolved_commit}"
  echo "--- Stamping provenance ${image}:${tag} -> ${image}:${prov_tag} ---"
  if [[ "${DRY_RUN}" == "true" ]]; then
    echo "  [dry-run] Would run az acr import for ${image}:${tag} -> ${prov_tag}"
    return 0
  fi

  local source_digest=""
  source_digest="$(wait_for_acr_tag_digest "${image}" "${tag}" 2>/dev/null || true)"
  if [[ -z "${source_digest}" ]]; then
    echo "ERROR: source image ${image}:${tag} never resolved to a digest in ACR; refusing to stamp unverifiable provenance" >&2
    return 1
  fi

  if az acr import \
    --name "${ACR_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --source "${ACR_LOGIN_SERVER}/${image}@${source_digest}" \
    --image "${image}:${prov_tag}" \
    --force \
    --output none; then
    :
  else
    local import_status=$?
    echo "ERROR: failed to stamp provenance tag ${image}:${prov_tag} (az acr import exit ${import_status}); refusing to ship unstamped image" >&2
    return 1
  fi

  local stamped_digest=""
  stamped_digest="$(wait_for_acr_tag_digest "${image}" "${prov_tag}" 2>/dev/null || true)"
  if [[ -z "${stamped_digest}" ]]; then
    echo "ERROR: provenance tag ${image}:${prov_tag} did not appear in ACR after import; refusing to ship unstamped image" >&2
    return 1
  fi
  if [[ "${stamped_digest}" != "${source_digest}" ]]; then
    echo "ERROR: provenance tag ${image}:${prov_tag} resolved to ${stamped_digest}, expected ${source_digest}; refusing to ship mismatched provenance" >&2
    return 1
  fi

  echo "  [prov]   ${ACR_LOGIN_SERVER}/${image}:${prov_tag} (commit ${resolved_commit})"
}

build_image() {
  local image="$1"
  local tag="$2"
  local dockerfile="$3"

  echo "--- Building ${image}:${tag} (${dockerfile}) ---"
  if [[ "${DRY_RUN}" == "true" ]]; then
    echo "  [dry-run] Would run az acr build for ${image}:${tag}"
    stamp_provenance "${image}" "${tag}" "${TARGET_COMMIT}"
    return $?
  fi
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
  stamp_provenance "${image}" "${tag}" "${TARGET_COMMIT}"
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
  if [[ "${DRY_RUN}" == "true" ]]; then
    echo "  [dry-run] Would run az acr import for ${image}:${source_tag} -> ${target_tag}"
    return 0
  fi
  az acr import \
    --name "${ACR_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --source "${ACR_LOGIN_SERVER}/${image}:${source_tag}" \
    --image "${image}:${target_tag}" \
    --force \
    --output none
  echo "  [retag]  ${ACR_LOGIN_SERVER}/${image}:${target_tag}"
}

acr_digest_for_tag() {
  local image="$1"
  local tag="$2"
  az acr repository show-manifests \
    --name "${ACR_NAME}" \
    --repository "${image}" \
    --query "[?tags[?@=='${tag}']].digest" \
    --output tsv 2>/dev/null \
  | tr '\r' '\n' \
  | awk 'NF {print; exit}'
}

wait_for_acr_tag_digest() {
  local image="$1"
  local tag="$2"
  local digest=""
  local attempt
  for attempt in 1 2 3 4 5; do
    digest="$(acr_digest_for_tag "${image}" "${tag}" 2>/dev/null || true)"
    if [[ -n "${digest}" ]]; then
      printf '%s\n' "${digest}"
      return 0
    fi
    sleep 2
  done
  return 1
}

schedule_image() {
  local image="$1"
  local target_tag="$2"
  local dockerfile="$3"
  local deployed_tag="$4"
  shift 4
  local paths=("$@")
  local source_tag="${PREVIOUS_IMAGE_TAG:-${deployed_tag}}"
  local source_commit
  source_commit="$(source_commit_for_tag "${source_tag}")"

  if [[ "${FORCE_REBUILD:-false}" == "true" || -z "${source_tag}" ]]; then
    echo "  [build]  ${image} (forced or no previous image tag)"
    build_image "${image}" "${target_tag}" "${dockerfile}" &
  elif [[ -z "${source_commit}" ]]; then
    echo "  [build]  ${image} (previous tag ${source_tag} has no resolvable VERSION commit)"
    build_image "${image}" "${target_tag}" "${dockerfile}" &
  elif paths_changed "${source_commit}" "${TARGET_COMMIT}" "${paths[@]}"; then
    echo "  [build]  ${image} (changed since ${source_tag} at ${source_commit:0:12})"
    build_image "${image}" "${target_tag}" "${dockerfile}" &
  else
    echo "  [retag]  ${image} (unchanged since ${source_tag} at ${source_commit:0:12})"
    ( retag_image "${image}" "${source_tag}" "${target_tag}" && \
      stamp_provenance "${image}" "${target_tag}" "${source_commit}" ) &
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
FRONTEND_SOURCE_COMMIT="$(source_commit_for_tag "${FRONTEND_SOURCE_TAG}")"
if [[ "${FORCE_REBUILD:-false}" == "true" || -z "${FRONTEND_SOURCE_TAG}" ]] || \
  [[ -z "${FRONTEND_SOURCE_COMMIT}" ]] || \
  paths_changed "${FRONTEND_SOURCE_COMMIT}" "${TARGET_COMMIT}" "apps/web" "apps/Agentweaver.Web"; then
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
  local completed_pid=""
  local completed_status=0
  local i
  local found_index=-1

  while [[ "${#pending_pids[@]}" -gt 0 ]]; do
    completed_pid=""
    if wait -n -p completed_pid "${pending_pids[@]}"; then
      completed_status=0
    else
      completed_status=$?
    fi

    found_index=-1
    for i in "${!pending_pids[@]}"; do
      if [[ "${pending_pids[$i]}" == "${completed_pid}" ]]; then
        found_index=$i
        break
      fi
    done

    if [[ "${found_index}" -lt 0 ]]; then
      echo "ERROR: image wait bookkeeping lost child pid '${completed_pid:-<empty>}'" >&2
      terminate_remaining_jobs ""
      return 1
    fi

    local job="${pending_jobs[$found_index]}"
    if [[ "${completed_status}" -eq 0 ]]; then
      echo "  [OK] ${job}"
    else
      echo "  [FAIL:${completed_status}] ${job}" >&2
      terminate_remaining_jobs "${completed_pid}"
      return "${completed_status}"
    fi

    unset 'pending_pids[found_index]'
    unset 'pending_jobs[found_index]'
    pending_pids=("${pending_pids[@]}")
    pending_jobs=("${pending_jobs[@]}")
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
