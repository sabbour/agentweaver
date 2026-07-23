---
"agentweaver": patch
---

Hardened sandbox RBAC (High-severity security-assessment finding): split the
combined API/worker sandbox permissions into distinct least-privilege Roles
(`agentweaver-api-sandbox`, `agentweaver-worker-sandbox`) each bound to its own
ServiceAccount, added a namespace-wide default-deny `NetworkPolicy` with
explicit compensating allows for DNS, Postgres, and AgentHost orchestration
traffic, and restricted `pods/exec` — which cannot be scoped via RBAC
`resourceNames` because sandbox pod names are dynamic — with a
`ValidatingAdmissionPolicy` (`k8s/base/vap-sandbox-exec.yaml`) that permits
exec only from the `agentweaver-api`/`agentweaver-worker` ServiceAccounts
against pods named `agentweaver-agent-host-*`, closing the lateral-movement
path where either identity could previously exec into any pod in the
namespace (including each other or Postgres).
