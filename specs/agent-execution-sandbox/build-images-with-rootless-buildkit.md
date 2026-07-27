# Build Docker/OCI images from AgentHost via rootless BuildKit on Kubernetes

**Issue:** [#582](https://github.com/sabbour/agentweaver/issues/582)  
**Area:** Agent execution & sandbox  
**Status:** Design only — not yet implemented. This spec exists to scope the design and get a
security review before any implementation work begins.

## User story

As an agent working inside a sandboxed run, I want to build a Docker/OCI image from a Dockerfile
in the run's workspace and push it to a registry, so that I can complete tasks that require a
container image (e.g. building a deployable artifact) without the sandbox needing Docker-in-Docker
or a mounted Docker socket.

## Context / problem

AgentHost sandboxes already run agent turns in isolated, non-privileged pods (see
[Isolate agent execution and workspaces](./isolate-agent-workspaces.md)). None of the current
sandbox tooling can build a container image: doing so today would normally require either
`privileged: true` with Docker-in-Docker, or mounting the host's `/var/run/docker.sock`, both of
which are incompatible with the isolation model this repo already commits to (kata/gVisor sandbox
execution, least-privilege service accounts, no host filesystem access from agent-controlled code).

The proposed approach uses `docker buildx`'s Kubernetes driver with rootless BuildKit
(`moby/buildkit:rootless`): buildx starts a BuildKit pod in a dedicated Kubernetes namespace,
`buildkitd` runs as a non-root user, and the image is built and pushed directly to a registry
without ever needing a privileged container or host daemon socket in the AgentHost pod itself.

Rootless BuildKit is a meaningfully smaller privilege surface than privileged DinD, but it is a
**new** privilege surface for this codebase: a new namespace, new RBAC for a service account to
create/manage BuildKit pods, and a new external-registry-push capability. None of that exists
today, so this needs a design and security review before implementation — this issue and spec are
explicitly design-only.

## Proposed design (for review)

- **Namespace boundary.** BuildKit pods run in a dedicated `buildkit` namespace, separate from the
  existing sandbox namespace(s) used for agent-host/kata pods. This keeps the new RBAC surface
  scoped and auditable independently of the existing sandbox RBAC (`k8s/base/rbac-api.yaml`).
- **RBAC.** A new, narrowly-scoped service account (distinct from `agentweaver-agent-host`'s
  existing identity) is granted only the permissions `docker buildx`'s Kubernetes driver needs to
  create/manage BuildKit pods inside the `buildkit` namespace — not cluster-wide, not on other
  namespaces. This is a net-new privilege AgentHost does not currently hold and must be reviewed
  like any other RBAC change (see the sandboxclaims patch/update precedent in #570/#571 for the
  level of verb-by-verb scrutiny expected).
- **Rootless posture.** BuildKit pods run with `runAsNonRoot: true`, `allowPrivilegeEscalation:
  false`, `privileged: false`, and drop all capabilities — matching the security posture already
  used elsewhere for sandbox pods.
- **Tool surface.** A new AgentHost-facing tool (working name `build_docker_image`), analogous to
  the existing `start_preview_process` tool, that takes a Dockerfile path (scoped to the run's own
  workspace) and a target image ref, and drives `buildx build --builder k8s-rootless --push`
  against the BuildKit pod. The tool must not let the agent choose an arbitrary builder, namespace,
  or escape the run's own workspace as the build context.
- **Registry auth.** Registry credentials for `--push` need a credential-handling story consistent
  with how AgentHost already receives per-run, per-user scoped secrets (see
  `docs/deep-dive/infra-deployment.md`'s AgentHost identity model) rather than a shared, long-lived
  credential baked into the BuildKit namespace.
- **Caching (optional, later).** Registry-based cache (`--cache-from`/`--cache-to type=registry`)
  is a reasonable follow-up once the base build path is proven; not required for a first design.

## Scope

### In (for this spec)
- design of the namespace/RBAC boundary for BuildKit pods
- design of the new AgentHost tool surface for building/pushing an image
- design of registry-credential handling for the push step
- identifying what a security review must cover before implementation starts

### Out (for this spec, and out of scope for any implementation until a follow-up issue)
- actually implementing the `buildx` Kubernetes driver wiring, the new tool, or the RBAC change
- choosing a specific base BuildKit image version/tag
- non-Kubernetes (local dev) build support
- build caching strategy details

## Open questions

- Where does the `buildkit` namespace's lifecycle live relative to cluster bring-up (`k8s/base`
  vs. a separate overlay), and who owns its capacity/cost?
- Does every project get its own BuildKit pod/builder, or is it a shared pool similar to the
  AgentHost warm pool?
- What happens when a build fails or hangs — does AgentHost enforce a timeout and tear down the
  BuildKit pod, matching the existing sandbox reaper pattern used for preview pods?
- Should the new tool require the same kind of human approval gating that other sandbox actions
  use (see [Govern agent tool use and questions](./govern-agent-tools-and-questions.md)), given it
  can push artifacts to an external registry?

## Notable edge cases

- A malicious or buggy Dockerfile must not be able to use the build context to read or exfiltrate
  files outside the run's own workspace.
- Registry push must fail closed if credentials are missing or invalid, not silently skip the push.
- The dedicated BuildKit RBAC must not be reachable by, or reusable from, other sandbox identities.
