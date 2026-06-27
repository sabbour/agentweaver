#!/usr/bin/env bash
# c4-flip-postgres.sh - Flip API to Postgres provider + restore replicas:1
set -euo pipefail
export ACR_LOGIN_SERVER="agentweaverregistry.azurecr.io"
export IMAGE_TAG="92e4d74c-fix2"
export NAMESPACE="agentweaver"
export IDENTITY_CLIENT_ID="31d79e6e-647f-4402-b953-60d598ca9054"
export KEYVAULT_NAME="agentweaver-kv"
export TENANT_ID="72f988bf-86f1-41af-91ab-2d7cd011db47"
export HOST="agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io"
K8S_DIR="/mnt/c/Users/asabbour/Git/agentweaver-spec018/k8s"

envsubst '${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${IDENTITY_CLIENT_ID} ${KEYVAULT_NAME} ${TENANT_ID}' \
  < "${K8S_DIR}/api-deployment.yaml" | kubectl apply -f -
echo "[C4] api-deployment.yaml applied with Database__Provider=Postgres, replicas:1"
