# Link decision: broker rootless BuildKit instead of granting AgentHost Kubernetes RBAC

- Context: Issue #582 asks for AgentHost image builds through Buildx's rootless Kubernetes driver.
  Agent-controlled shell commands run in the AgentHost pod, so any Kubernetes token or registry
  credential mounted there is reachable by untrusted code. Upstream rootless Buildx also emits an
  unconfined seccomp/AppArmor pod template that cannot pass the existing baseline Pod Security
  policy without a narrowly-contained exception.
- Decision: keep AgentHost's service account and identity unchanged. Add the future capability
  through a trusted build broker, a dedicated build namespace, exact namespace-scoped RBAC,
  validating admission policies, one ephemeral Kata-isolated rootless BuildKit Deployment per
  build, and broker-only short-lived registry authorization.
- Rationale: this preserves the existing sandbox trust boundary, avoids privileged DinD and
  `docker.sock`, prevents shared builder/cache state across tenants, and makes the unavoidable
  upstream unconfined security profile explicit and auditable rather than silently widening the
  AgentHost namespace.
