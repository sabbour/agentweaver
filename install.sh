#!/usr/bin/env bash
# install.sh — Agentweaver one-liner installer
#
# Usage (local dev, default):
#   bash install.sh
#   bash install.sh --local
#
# Usage (AKS deploy):
#   bash install.sh --aks [--skip-postgres] [--skip-oauth-key] [--image-tag <tag>]
#
# Usage (AKS redeploy with a specific image tag):
#   bash install.sh --aks --image-tag abc1234
#
# Runnable via:
#   curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks
#   curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks --image-tag abc1234

set -euo pipefail

# ── Colour helpers ─────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; RESET='\033[0m'

info()    { echo -e "${CYAN}[info]${RESET}  $*"; }
success() { echo -e "${GREEN}[ok]${RESET}    $*"; }
warn()    { echo -e "${YELLOW}[warn]${RESET}  $*"; }
die()     { echo -e "${RED}[error]${RESET} $*" >&2; exit 1; }

# ── Argument parsing ───────────────────────────────────────────────────────────
MODE="local"
SKIP_POSTGRES=false
SKIP_OAUTH_KEY=false
IMAGE_TAG_OVERRIDE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --local)          MODE="local"; shift ;;
    --aks)            MODE="aks"; shift ;;
    --skip-postgres)  SKIP_POSTGRES=true; shift ;;
    --skip-oauth-key) SKIP_OAUTH_KEY=true; shift ;;
    --image-tag)      IMAGE_TAG_OVERRIDE="${2:-}"; shift 2 ;;
    --image-tag=*)    IMAGE_TAG_OVERRIDE="${1#*=}"; shift ;;
    -h|--help)
      echo "Usage: install.sh [--local|--aks] [--skip-postgres] [--skip-oauth-key] [--image-tag <tag>]"
      echo ""
      echo "  --local              Set up local dev environment (default)"
      echo "  --aks                Deploy to Azure Kubernetes Service"
      echo "  --skip-postgres      (AKS) Skip Postgres provisioning (17-provision-postgres.sh)"
      echo "  --skip-oauth-key     (AKS) Skip OAuth signing key provisioning (16-provision-oauth-signing-key.sh)"
      echo "  --image-tag <tag>    (AKS) Use this image tag instead of the short git SHA."
      echo "                             Re-run with a new tag to build, push, and redeploy."
      echo "                             Never use 'latest' — always pin to a specific SHA."
      exit 0
      ;;
    *) die "Unknown argument: $1. Run with --help for usage." ;;
  esac
done

# ── Locate repo root ───────────────────────────────────────────────────────────
INSTALL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || pwd)"
# When piped through bash, BASH_SOURCE[0] may be empty; fall back to cwd.

# Override for forks: set AGENTWEAVER_REPO_URL to point at your fork.
AGENTWEAVER_REPO_URL="${AGENTWEAVER_REPO_URL:-https://github.com/sabbour/agentweaver.git}"

if [[ ! -f "${INSTALL_DIR}/agentweaver.sln" && ! -f "${INSTALL_DIR}/global.json" ]]; then
  # ── Bootstrap: running piped (curl | bash) outside a checkout ───────────────
  if ! command -v git &>/dev/null; then
    die "git is not installed and no checkout was found. Install git then re-run."
  fi
  CLONE_DIR="${HOME}/agentweaver"
  if [[ -f "${CLONE_DIR}/agentweaver.sln" || -f "${CLONE_DIR}/global.json" ]]; then
    info "Found existing checkout at ${CLONE_DIR} — using it."
  else
    info "No checkout found. Cloning ${AGENTWEAVER_REPO_URL} → ${CLONE_DIR}"
    git clone --depth 1 "${AGENTWEAVER_REPO_URL}" "${CLONE_DIR}"
  fi
  exec bash "${CLONE_DIR}/install.sh" "$@"
fi
REPO_ROOT="${INSTALL_DIR}"
AKS_DIR="${REPO_ROOT}/scripts/aks"

echo ""
echo -e "${BOLD}  Agentweaver Installer${RESET}  (mode: ${MODE})"
echo ""

# ══════════════════════════════════════════════════════════════════════════════
# LOCAL DEV MODE
# ══════════════════════════════════════════════════════════════════════════════
install_local() {
  echo -e "${BOLD}── Checking prerequisites ──${RESET}"

  # git
  if ! command -v git &>/dev/null; then
    die "git is not installed. Install git then re-run."
  fi
  success "git $(git --version | awk '{print $3}')"

  # .NET 10
  if ! command -v dotnet &>/dev/null; then
    die ".NET SDK is not installed. Install .NET 10 SDK from https://dot.net/download then re-run."
  fi
  DOTNET_VERSION="$(dotnet --version 2>/dev/null || echo "unknown")"
  DOTNET_MAJOR="$(echo "${DOTNET_VERSION}" | cut -d. -f1)"
  if [[ "${DOTNET_MAJOR}" -lt 10 ]]; then
    die ".NET 10 SDK is required (found ${DOTNET_VERSION}). Install from https://dot.net/download"
  fi
  success "dotnet ${DOTNET_VERSION}"

  # Node / npm
  if ! command -v node &>/dev/null || ! command -v npm &>/dev/null; then
    die "Node.js (>=20.19) and npm are required. Install from https://nodejs.org/"
  fi
  NODE_VERSION="$(node --version | tr -d 'v')"
  NODE_MAJOR="$(echo "${NODE_VERSION}" | cut -d. -f1)"
  if [[ "${NODE_MAJOR}" -lt 20 ]]; then
    die "Node.js 20.19+ or 22.12+ is required (found ${NODE_VERSION}). Install from https://nodejs.org/"
  fi
  success "node v${NODE_VERSION}"

  echo ""
  echo -e "${BOLD}── Installing web dependencies ──${RESET}"
  npm --prefix "${REPO_ROOT}/apps/web" install
  success "Web dependencies installed."

  echo ""
  echo -e "${BOLD}── Restoring .NET packages ──${RESET}"
  dotnet restore "${REPO_ROOT}/agentweaver.sln" -v q --nologo
  success ".NET packages restored."

  echo ""
  echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${RESET}"
  echo -e "${BOLD}  LOCAL DEV READY${RESET}"
  echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${RESET}"
  echo ""
  echo "  Start the API (terminal 1):"
  echo "    dotnet run --project apps/Agentweaver.Api"
  echo ""
  echo "  Configure the GitHub OAuth client secret (once, from repo root):"
  echo "    cd apps/Agentweaver.Api"
  echo "    dotnet user-secrets set \"Auth:GitHub:ClientSecret\" \"<your-oauth-app-client-secret>\""
  echo ""
  echo "  Start the MCP server (terminal 2, optional):"
  echo "    dotnet run --project apps/Agentweaver.Mcp"
  echo ""
  echo "  Start the Web UI (terminal 3):"
  echo "    npm --prefix apps/web run dev"
  echo ""
  echo "  API  → http://localhost:5000"
  echo "  Web  → http://localhost:8080"
  echo ""
  echo "  On Windows (WSL2), use start-dev.ps1 instead:"
  echo "    .\\start-dev.ps1"
  echo ""
}

# ══════════════════════════════════════════════════════════════════════════════
# AKS DEPLOY MODE
# ══════════════════════════════════════════════════════════════════════════════
install_aks() {
  echo -e "${BOLD}── Checking AKS prerequisites ──${RESET}"

  require_cmd() {
    local cmd="$1"; local hint="${2:-}"
    if ! command -v "${cmd}" &>/dev/null; then
      die "'${cmd}' not found.${hint:+ ${hint}}"
    fi
    success "${cmd} found"
  }

  require_cmd bash
  require_cmd git
  require_cmd az         "Install Azure CLI: https://aka.ms/install-azure-cli"
  require_cmd kubectl    "Install kubectl: https://kubernetes.io/docs/tasks/tools/"
  require_cmd envsubst   "Install gettext: apt install gettext  or  brew install gettext"
  require_cmd openssl    "Install openssl: apt install openssl  or  brew install openssl"

  # Verify az login
  if ! az account show &>/dev/null; then
    die "Azure CLI is not logged in. Run: az login"
  fi
  success "az account: $(az account show --query name -o tsv 2>/dev/null)"

  # Verify aks-preview feature is registered or at least extension available
  info "Checking aks-preview extension..."
  if ! az extension show --name aks-preview &>/dev/null; then
    warn "aks-preview extension not installed — 10-create-cluster.sh will install it automatically."
  else
    success "aks-preview extension present"
  fi

  echo ""
  echo -e "${BOLD}── Sourcing variables ──${RESET}"
  # Pre-set IMAGE_TAG if --image-tag was provided; 00-variables.sh respects a pre-set value.
  if [[ -n "${IMAGE_TAG_OVERRIDE}" ]]; then
    export IMAGE_TAG="${IMAGE_TAG_OVERRIDE}"
    info "Using specified image tag: ${IMAGE_TAG}"
  fi
  # shellcheck source=scripts/aks/00-variables.sh
  source "${AKS_DIR}/00-variables.sh"

  run_step() {
    local label="$1"; local script="$2"; shift 2
    echo ""
    echo -e "${BOLD}── ${label} ──${RESET}"
    bash "${AKS_DIR}/${script}" "$@"
    success "${label} complete."
  }

  run_step "10 — Create cluster (ACR + AKS)"  "10-create-cluster.sh"
  run_step "15 — Set up identity"              "15-setup-identity.sh"

  if [[ "${SKIP_OAUTH_KEY}" == "false" ]]; then
    run_step "16 — Provision OAuth signing key" "16-provision-oauth-signing-key.sh"
  else
    warn "Skipping 16-provision-oauth-signing-key.sh (--skip-oauth-key)"
  fi

  if [[ "${SKIP_POSTGRES}" == "false" ]]; then
    run_step "17 — Provision Postgres"          "17-provision-postgres.sh"
  else
    warn "Skipping 17-provision-postgres.sh (--skip-postgres)"
  fi

  run_step "20 — Build and push images"        "20-build-push-images.sh"

  # gen-a2a-mtls-certs.sh MUST run before 30-deploy.sh
  run_step "gen — A2A mTLS certificates"       "gen-a2a-mtls-certs.sh"

  run_step "30 — Deploy manifests"             "30-deploy.sh"
  run_step "40 — Verify deployment"            "40-verify.sh"

  echo ""
  echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${RESET}"
  echo -e "${BOLD}  AKS DEPLOYMENT COMPLETE${RESET}"
  echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${RESET}"
  echo ""
  echo "  Resource Group: ${RESOURCE_GROUP}"
  echo "  Cluster:        ${CLUSTER_NAME}"
  echo "  ACR:            ${ACR_LOGIN_SERVER}"
  echo "  Namespace:      ${NAMESPACE}"
  echo "  Image tag:      ${IMAGE_TAG}"
  echo ""
  echo "  Check pods:"
  echo "    kubectl get pods -n ${NAMESPACE}"
  echo ""
  echo "  View gateway/routes:"
  echo "    kubectl get gateway,httproute -n ${NAMESPACE}"
  echo ""
}

case "${MODE}" in
  local) install_local ;;
  aks)   install_aks ;;
esac
