---
"agentweaver": patch
---

Fixed a Critical security-assessment finding: AgentHost sandbox pods (which
execute untrusted agent/tool shell commands) previously federated to the same
Key Vault identity as the API (`agentweaver-api-identity`), granted Key Vault
Secrets User/Officer roles. Untrusted code running in a sandbox could exchange
its projected workload-identity token for a Key Vault access token and read
every user's secrets.

AgentHost now federates to a dedicated, least-privilege managed identity
(`agentweaver-agenthost-identity`) with no Key Vault role assignments. This is
a functional no-op for legitimate use: the run owner's GitHub token is already
brokered per-run by the API through the `/configure` call rather than fetched
directly from Key Vault by the sandbox. Deploying this change to an existing
cluster also removes the legacy `agentweaver-agenthost-fedcred` federated
credential from the API identity so older deployments can't retain the
vault-privileged mapping.
