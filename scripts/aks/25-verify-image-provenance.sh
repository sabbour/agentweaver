#!/usr/bin/env bash
# 25-verify-image-provenance.sh -- Independent, post-deploy safety net for #251
# ("release image retag-forward can ship stale code").
# Keep in sync with 25-verify-image-provenance.ps1 (PowerShell equivalent).
#
# 20-build-push-images.sh decides build-vs-retag at build time and, since the
# #251/#303 fix, stamps every image it produces with an extra immutable ACR
# tag 'prov-<sha>' recording the commit its content actually corresponds
# to. This script re-checks that decision independently and *after* the fact:
# for each of the 4 workloads it finds the prov-<sha> tag pointing at the
# exact digest that is CURRENTLY RUNNING in live pods (per api/frontend/mcp
# Deployments and the agent-host warm pool), then verifies that commit has no
# diff in the paths that feed that image, versus the target commit being
# verified (HEAD by default, or VERIFY_GIT_REF).
#
# This deliberately does NOT re-derive or trust 20-build-push-images.sh's own
# in-process TARGET_COMMIT/paths_changed() decision -- it re-derives
# everything from what is actually running in ACR/AKS right now, so it also
# catches: a manual 'az acr import' done outside the script, deploying a tag
# that was never re-verified, or a bug in the build script itself.
#
# Usage:
#   source scripts/aks/00-variables.sh
#   bash scripts/aks/25-verify-image-provenance.sh
#
# Optional:
#   VERIFY_GIT_REF=<ref>   Commit/ref to diff running images against (default: HEAD)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
source "${SCRIPT_DIR}/00-variables.sh"
cd "${REPO_ROOT}"

VERIFY_GIT_REF="${VERIFY_GIT_REF:-HEAD}"
# Defensive check: VERIFY_GIT_REF is sometimes passed in from a caller (e.g.
# 30-deploy.sh) as an IMAGE_TAG-derived string. Since ~v0.9.36, release tags
# are no longer created in git for every VERSION bump (see release_ref_for_tag()
# in 20-build-push-images.sh), so a caller-supplied ref may not resolve. Fail
# with a clear, actionable message instead of git's generic
# "fatal: Needed a single revision".
if ! VERIFY_COMMIT="$(git rev-parse --verify "${VERIFY_GIT_REF}^{commit}" 2>/dev/null)"; then
  echo "ERROR: VERIFY_GIT_REF='${VERIFY_GIT_REF}' does not resolve to a commit in this repository." >&2
  echo "  This is usually because VERIFY_GIT_REF was derived from IMAGE_TAG (a VERSION-file" >&2
  echo "  semver string), which is not necessarily an actual git tag/ref. Pass an explicit," >&2
  echo "  resolvable commit/ref via VERIFY_GIT_REF, or leave it unset to default to HEAD." >&2
  exit 1
fi

PASS=0
FAIL=0
ok()   { echo "  [OK]   $*"; (( PASS++ )) || true; }
fail() { echo "  [FAIL] $*"; (( FAIL++ )) || true; }
info() { echo "  [INFO] $*"; }

COMMON_DOTNET_PATHS=(
  "agentweaver.sln"
  "global.json"
  "Directory.Build.props"
  "Directory.Packages.props"
  "NuGet.config"
  "packages"
)

echo ""
echo "=== Image provenance verification (against ${VERIFY_GIT_REF} = ${VERIFY_COMMIT:0:12}) ==="
echo ""

# Returns the desired replica count for a Deployment.
desired_deployment_replicas() {
  local deployment="$1"
  kubectl get deployment "${deployment}" \
    --namespace "${NAMESPACE}" \
    --output jsonpath='{.spec.replicas}' 2>/dev/null || true
}

# Returns the desired warm replica count for the AgentHost pool.
desired_agenthost_replicas() {
  kubectl get sandboxwarmpool agentweaver-agent-host \
    --namespace "${NAMESPACE}" \
    --output jsonpath='{.spec.replicas}' 2>/dev/null || true
}

pod_status_lines_for_selector() {
  local selector="$1"
  kubectl get pods \
    --namespace "${NAMESPACE}" \
    --selector "${selector}" \
    --output jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.status.phase}{"\t"}{.status.containerStatuses[0].ready}{"\t"}{.status.containerStatuses[0].image}{"\t"}{.status.containerStatuses[0].imageID}{"\n"}{end}' 2>/dev/null || true
}

image_tag_from_ref() {
  local image_ref="$1"
  local last_segment="${image_ref##*/}"
  if [[ "${last_segment}" == *:* ]]; then
    printf '%s\n' "${last_segment##*:}"
  fi
}

image_digest_from_id() {
  local image_id="$1"
  sed -nE 's#.*@(sha256:[0-9a-f]{64})#\1#p' <<< "${image_id}"
}

LIVE_DIGEST_STATE_DIGEST=""
LIVE_DIGEST_STATE_TAG=""
LIVE_DIGEST_STATE_POD_COUNT=""

live_digest_state_for_selector() {
  local label="$1"
  local selector="$2"
  local expected_replicas="$3"
  local lines=""
  local pod_name phase ready image_ref image_id pod_digest pod_tag
  local digest=""
  local tag=""
  local pod_count=0

  LIVE_DIGEST_STATE_DIGEST=""
  LIVE_DIGEST_STATE_TAG=""
  LIVE_DIGEST_STATE_POD_COUNT=""

  if [[ -z "${expected_replicas}" ]]; then
    fail "${label}: could not determine desired replica count for selector '${selector}'"
    return 1
  fi

  lines="$(pod_status_lines_for_selector "${selector}")"
  if [[ -z "${lines}" ]]; then
    fail "${label}: no pods found for selector '${selector}'"
    return 1
  fi

  while IFS=$'\t' read -r pod_name phase ready image_ref image_id; do
    [[ -z "${pod_name}" ]] && continue
    (( pod_count++ )) || true

    if [[ "${phase}" != "Running" ]]; then
      fail "${label}: pod ${pod_name} is phase='${phase}' (expected Running); refusing provenance check while replicas are unavailable"
      return 1
    fi
    if [[ "${ready}" != "true" ]]; then
      fail "${label}: pod ${pod_name} is not Ready; refusing provenance check while replicas are unavailable"
      return 1
    fi

    pod_digest="$(image_digest_from_id "${image_id}")"
    if [[ -z "${pod_digest}" ]]; then
      fail "${label}: pod ${pod_name} has no resolvable imageID digest yet; refusing provenance check while replicas are unavailable"
      return 1
    fi

    pod_tag="$(image_tag_from_ref "${image_ref}")"
    if [[ -z "${digest}" ]]; then
      digest="${pod_digest}"
      tag="${pod_tag}"
      continue
    fi

    if [[ "${pod_digest}" != "${digest}" ]]; then
      fail "${label}: mixed live digests across replicas (${digest} vs ${pod_digest}); rollout/retag state is not uniform, refusing provenance check"
      return 1
    fi
  done <<< "${lines}"

  if [[ "${pod_count}" -eq 0 ]]; then
    fail "${label}: no pods found for selector '${selector}'"
    return 1
  fi
  if [[ -n "${expected_replicas}" && "${pod_count}" -ne "${expected_replicas}" ]]; then
    fail "${label}: expected ${expected_replicas} pod(s) for selector '${selector}', found ${pod_count}; refusing provenance check while replicas are unavailable"
    return 1
  fi

  LIVE_DIGEST_STATE_DIGEST="${digest}"
  LIVE_DIGEST_STATE_TAG="${tag}"
  LIVE_DIGEST_STATE_POD_COUNT="${pod_count}"
}

# Finds prov-<sha> tag(s) on the same repository whose digest matches the
# given live digest, and prints unique tag names one per line.
provenance_tags_for_digest() {
  local image="$1"
  local digest="$2"
  az acr repository show-manifests \
    --name "${ACR_NAME}" \
    --repository "${image}" \
    --query "[?digest=='${digest}'].tags[]" \
    --output tsv 2>/dev/null \
  | tr '\t' '\n' \
  | grep -E '^prov-[0-9a-f]{12}$|^prov-[0-9a-f]{40}$' \
  | sort -u
}

resolve_provenance_commit() {
  local commitish="$1"
  if git rev-parse --verify "${commitish}^{commit}" >/dev/null 2>&1; then
    git rev-parse --verify "${commitish}^{commit}"
    return 0
  fi

  git log --all --format=%H | grep -E "^${commitish}" | head -n1 || true
}

verify_image() {
  local label="$1"
  local image="$2"
  local pod_selector="$3"
  local expected_replicas="$4"
  shift 4
  local paths=("$@")
  local live_digest=""
  local live_tag=""
  local live_pod_count=""
  local resolved_commit=""
  local -a prov_tags=()

  live_digest_state_for_selector "${label}" "${pod_selector}" "${expected_replicas}" || return
  live_digest="${LIVE_DIGEST_STATE_DIGEST}"
  live_tag="${LIVE_DIGEST_STATE_TAG}"
  live_pod_count="${LIVE_DIGEST_STATE_POD_COUNT}"
  if [[ -z "${live_digest}" ]]; then
    fail "${label}: could not determine live digest from running pods"
    return
  fi

  mapfile -t prov_tags < <(provenance_tags_for_digest "${image}" "${live_digest}")
  if [[ "${#prov_tags[@]}" -eq 0 ]]; then
    fail "${label}: no prov-<sha> tag found for live digest ${live_digest:0:19} -- image predates the #251/#303 provenance fix, or was pushed by a route other than 20-build-push-images.sh. Cannot verify provenance; treat as unverified, not passing."
    return
  fi

  # An unchanged image can accumulate multiple prov-<sha> tags across successive
  # releases (each release's 'az acr import' retag stamps a fresh prov tag onto
  # the SAME already-existing digest, since the content genuinely didn't change).
  # That is not ambiguous -- all such tags describe bit-identical content. It is
  # sufficient for ANY one of the accumulated commits to show no drift in the
  # watched paths vs VERIFY_COMMIT; report which one, plus the ones we skipped.
  local -a resolved_ok=() resolved_stale=() resolved_unresolvable=()
  for prov_tag in "${prov_tags[@]}"; do
    local candidate_commit
    candidate_commit="$(resolve_provenance_commit "${prov_tag#prov-}")"
    if [[ -z "${candidate_commit}" ]]; then
      resolved_unresolvable+=("${prov_tag}")
      continue
    fi
    if git diff --quiet "${candidate_commit}" "${VERIFY_COMMIT}" -- "${paths[@]}"; then
      resolved_ok+=("${candidate_commit}")
    else
      resolved_stale+=("${candidate_commit}")
    fi
  done

  if [[ "${#resolved_ok[@]}" -gt 0 ]]; then
    resolved_commit="${resolved_ok[0]}"
    local extra_note=""
    if [[ "${#prov_tags[@]}" -gt 1 ]]; then
      extra_note=" (${#prov_tags[@]} prov tags accumulated on this unchanged digest across releases; using ${resolved_commit:0:12})"
    fi
    ok "${label}: ${live_pod_count} live pod(s) run ${image}:${live_tag:-<digest-only>} at ${live_digest:0:19}, provably built from ${resolved_commit:0:12}; no drift in watched paths vs ${VERIFY_COMMIT:0:12}${extra_note}"
    return
  fi

  if [[ "${#resolved_stale[@]}" -gt 0 ]]; then
    fail "${label}: ${live_pod_count} live pod(s) run ${image}:${live_tag:-<digest-only>} at ${live_digest:0:19}, built from ${resolved_stale[0]:0:12}, but watched paths changed since then vs ${VERIFY_COMMIT:0:12} -- STALE IMAGE (this is exactly the #251 failure mode). Re-run scripts/aks/20-build-push-images.sh with FORCE_REBUILD=true for this image."
    return
  fi

  fail "${label}: none of the ${#prov_tags[@]} prov tag(s) for live digest ${live_digest:0:19} resolve in local git history (shallow clone or rewritten history?): ${resolved_unresolvable[*]}"
}

verify_image "api"         "agentweaver-api"        "app=agentweaver-api"                                     "$(desired_deployment_replicas agentweaver-api)"        "${COMMON_DOTNET_PATHS[@]}" "apps/Agentweaver.Api"
verify_image "frontend"    "agentweaver-frontend"   "app=agentweaver-frontend"                                "$(desired_deployment_replicas agentweaver-frontend)"   "apps/web" "apps/Agentweaver.Web"
verify_image "mcp"         "agentweaver-mcp"        "app=agentweaver-mcp"                                     "$(desired_deployment_replicas agentweaver-mcp)"        "${COMMON_DOTNET_PATHS[@]}" "apps/Agentweaver.Mcp"
verify_image "agent-host"  "agentweaver-agent-host" "app=agentweaver-sandbox,app.kubernetes.io/component=agent-host" "$(desired_agenthost_replicas)"                  "${COMMON_DOTNET_PATHS[@]}" "apps/Agentweaver.AgentHost"

echo ""
echo "==================================================="
echo " PROVENANCE VERIFICATION SUMMARY: ${PASS} passed, ${FAIL} failed"
echo "==================================================="
[[ "${FAIL}" -eq 0 ]] && echo " ALL IMAGES VERIFIED AGAINST SOURCE" || echo " SOME IMAGES FAILED PROVENANCE CHECK -- see output above"
echo ""

[[ "${FAIL}" -eq 0 ]]
