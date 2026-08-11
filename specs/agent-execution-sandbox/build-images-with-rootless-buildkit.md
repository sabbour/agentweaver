# Build Docker/OCI images from AgentHost via rootless BuildKit on Kubernetes

**Issue:** [#582](https://github.com/sabbour/agentweaver/issues/582)
**Area:** Agent execution & sandbox
**Status:** Implementation-ready design. The capability remains disabled and unimplemented until
the security review and cluster compatibility gates in this spec pass.

## User story

As an agent working inside a sandboxed run, I want to build a Docker/OCI image from a Dockerfile
in the run's workspace and push it to an approved registry, so that I can produce a deployable
artifact without Docker-in-Docker, a mounted Docker socket, privileged containers, or host
filesystem access.

## Context / problem

AgentHost currently has no container-image build tool. Its image contains developer tools but not
Docker or Buildx, and its Kubernetes service account has no Role granting pod or Deployment
management. AgentHost runs in a Kata-backed `SandboxTemplate`, with a dedicated workload identity
that deliberately has no Key Vault access. This is the correct starting posture and must not be
weakened. The current Kubernetes manifests do not configure a gVisor runtime class, so the first
implementation targets the existing Kata contract only.

The initial design-only version of this spec proposed granting AgentHost enough Kubernetes access
to drive Buildx directly. The implementation audit found that unsafe: agent-controlled
`run_command` executes in the same pod and can read any mounted service-account token. Giving the
AgentHost service account Buildx RBAC would therefore give untrusted shell code the same
Deployment/pod-exec permissions.

Buildx's Kubernetes driver also has a security compatibility caveat that must be explicit.
`rootless=true` generates a BuildKit pod with unconfined seccomp and AppArmor and
`--oci-worker-no-process-sandbox`. BuildKit documents that the latter lets build steps signal or
potentially ptrace processes in the BuildKit daemon container. Rootless mode removes root-on-host
and privileged-container access, but it does not satisfy Kubernetes Pod Security `baseline` or
`restricted` by itself.

The secure boundary is therefore a trusted build broker. AgentHost receives a narrow native tool
and streams a validated build context to the API. A broker, using a different service account and
managed identity, creates one short-lived, rootless, Kata-isolated BuildKit Deployment in a
dedicated namespace and connects Buildx to that pre-created Deployment. Registry credentials stay
in the broker's memory-backed runtime directory and BuildKit session; they never enter AgentHost,
the build context, command arguments, Kubernetes Secrets, or image layers.

## Current-state audit

| Contract | Current implementation | Consequence for this feature |
|---|---|---|
| AgentHost isolation | `k8s/base/sandbox-template-agenthost.yaml` runs UID/GID 1000, drops all capabilities, disables privilege escalation, and uses `kata-vm-isolation`. | Keep this pod unchanged. Do not install Docker/Buildx or add BuildKit RBAC to it. |
| AgentHost identity | `k8s/base/serviceaccount-agenthost.yaml` uses a dedicated managed identity with no Key Vault roles. | Do not reuse it for registry push or credential retrieval. |
| Kubernetes RBAC | `k8s/base/rbac-api.yaml` grants sandbox lifecycle permissions only to API/worker identities. AgentHost has no RoleBinding. | Add a separate broker service account and a namespace-scoped Role; AgentHost receives no new Kubernetes verbs. |
| Network | AgentHost egress is DNS, API/MCP east-west, and public TCP/443 with private/link-local ranges excluded. | AgentHost only needs its existing API route. BuildKit gets a separate default-deny policy and approved registry/package HTTPS egress. |
| Workspace | `k8s/base/pvc-workspace.yaml` is a shared RWX volume and explicitly is not a cross-run isolation boundary. Pod-local implementation checkouts use `/local-workspace`. | The broker must never mount the shared workspace PVC. AgentHost archives only the active run's resolved root and streams it. |
| Quotas | `k8s/base/quota.yaml` bounds objects in `agentweaver`, but no build namespace exists. | The build namespace needs its own ResourceQuota, LimitRange, and concurrency gate. |
| Registry | Deployment tooling builds/publishes Agentweaver images outside AgentHost. The cluster identity pulls from ACR; no sandbox identity can push. | Introduce a dedicated build-broker identity and an explicit per-project destination policy. |
| Tool governance | Native tools pass through the sandbox policy backend; consequential operator tools fail closed to approval. | `build_docker_image` is a native, always-approval-required tool and is absent unless both global and project switches are enabled. |

## Proposed architecture

### Components and trust boundaries

1. **AgentHost native tool**
   - Register `build_docker_image` through a dedicated tool provider, not through `run_command`.
   - AgentHost validates paths against the active run root and creates a deterministic tar stream
     without following links outside that root.
   - AgentHost sends the request to an authenticated internal API endpoint using the existing
     per-turn bearer path. It receives no Kubernetes or registry credential.

2. **Agentweaver API**
   - Authorizes the run, project, effective sandbox policy, registry profile, and approval.
   - Persists the build record and emits lifecycle events.
   - Streams the context to the broker with backpressure; it does not write the archive to the
     shared workspace PVC.

3. **Build broker**
   - Run a new trusted Deployment in the `agentweaver` namespace under service account
     `agentweaver-build-broker`.
   - Ship pinned Docker CLI and Buildx plugin versions in the broker image.
   - Resolve registry authorization using its own dedicated workload identity.
   - Create a hardened BuildKit Deployment directly, then point Buildx's Kubernetes driver at that
     already-existing Deployment. Do not let Buildx generate the pod template.
   - Keep the Buildx client state, context archive, and Docker config in size-limited `emptyDir`
     volumes. The Docker config directory is memory-backed.

4. **Ephemeral BuildKit daemon**
   - Run one Deployment with one replica per build in namespace `agentweaver-buildkit`.
   - Use a unique DNS-safe name derived from the opaque build ID, never a model-supplied name.
   - Run `moby/buildkit:rootless` pinned by digest on `kata-vm-isolation`.
   - Carry no service-account token and no Kubernetes RBAC.
   - Use ephemeral cache only in the first implementation. Delete the Deployment after every
     terminal outcome.

The broker is the only component that can create/delete BuildKit Deployments or execute
`buildctl dial-stdio` in their pods. AgentHost, API, BuildKit, and model-written commands cannot
reuse that credential.

Upstream contracts reviewed for this design:

- [Docker Buildx Kubernetes driver options](https://docs.docker.com/build/builders/drivers/kubernetes/)
- [Buildx Kubernetes driver lifecycle and `pods/exec` connection](https://github.com/docker/buildx/blob/master/driver/kubernetes/driver.go)
- [Buildx-generated rootless pod security context](https://github.com/docker/buildx/blob/master/driver/kubernetes/manifest/manifest.go)
- [BuildKit rootless limitations and Kubernetes caveats](https://github.com/moby/buildkit/blob/master/docs/rootless.md)
- [Official rootless Kubernetes Deployment example](https://github.com/moby/buildkit/blob/master/examples/kubernetes/deployment%2Bservice.rootless.yaml)

### Tool contract

The first version exposes:

```json
{
  "context_path": ".",
  "dockerfile_path": "Dockerfile",
  "image": "approved.azurecr.io/project-prefix/app:tag",
  "target": null
}
```

- `context_path` is relative to the active run root. Absolute paths and `..` escapes are rejected.
- `dockerfile_path` is relative to the resolved context and must be a regular file.
- `image` must be a canonical OCI reference whose registry and repository prefix match the
  project's configured registry profile.
- `target` is an optional Dockerfile stage name with a conservative length/character allowlist.
- The first version supports only `linux/amd64`, one pushed registry output, and no cache export.
- The first version does not accept build secrets, SSH forwarding, custom BuildKit flags,
  entitlements, build arguments, custom outputs, custom builders, arbitrary platforms, network
  mode, or registry credentials.
- Successful output includes `build_id`, canonical image reference, immutable pushed digest,
  duration, and a redacted/truncated log summary. Failure output uses a stable reason code.

`build_docker_image` is available only for a Kubernetes pod-per-run AgentHost. Local execution
returns `image_build_not_supported` rather than falling back to local Docker.

### API contract

AgentHost uses run-scoped internal endpoints:

- `POST /api/runs/{runId}/image-builds` uploads multipart metadata plus the tar stream and returns
  `202 Accepted` with an opaque `build_id`;
- `GET /api/runs/{runId}/image-builds/{buildId}` returns bounded status/progress and the terminal
  digest or stable failure code;
- `DELETE /api/runs/{runId}/image-builds/{buildId}` requests cancellation and is idempotent.

All three endpoints require the existing per-turn AgentHost bearer, verify that the route run ID
matches the bearer/run context, and authorize the project before contacting the broker. Browser
users do not call these internal endpoints directly. Project configuration remains on the
Owner-authorized sandbox-policy surface; no request can override global limits or registry
profiles.

### Context containment

Before uploading, AgentHost must:

- resolve the active run root supplied by runtime state, not a model-provided repository root;
- reject any requested path outside that root after canonicalization;
- enumerate archive entries without following directory symlinks;
- reject symlink or hardlink entries whose resolved target escapes the root;
- honor `.dockerignore`, while always excluding `.git`, `.squad` credential/runtime state,
  `.agentweaver` runtime state, socket/device/FIFO entries, and the broker metadata file;
- cap uncompressed bytes, file count, per-file size, and archive bytes;
- avoid logging file contents or the complete file list.

The API and broker verify the declared archive digest and limits again. The broker extracts with
no absolute paths, parent traversal, devices, setuid/setgid bits, ownership preservation, or link
escape. A failed validation deletes the partial context and never starts BuildKit.

The initial defaults are 1 GiB uncompressed context, 100,000 entries, 256 MiB per file, and a
30-minute build timeout. Operators may lower these values globally; projects cannot raise them.

### Namespace, service accounts, and RBAC

Create namespace `agentweaver-buildkit`. Because upstream rootless BuildKit requires unconfined
seccomp/AppArmor, this namespace cannot use the repository's normal Pod Security `baseline`
enforcement. It must be labeled to audit/warn against `restricted`, and may use `privileged`
enforcement only when both admission policies below are installed and enforced. If the cluster
does not support those admission policies, the feature must remain disabled.

Service accounts:

- `agentweaver-build-broker` in `agentweaver`: workload identity for registry authorization;
  `automountServiceAccountToken: true` because the broker calls the Kubernetes API.
- `agentweaver-buildkit-daemon` in `agentweaver-buildkit`: no cloud federation, no RoleBinding,
  and `automountServiceAccountToken: false`.

Bind the following Role in `agentweaver-buildkit` only to the broker service account:

| API group/resource | Verbs | Reason |
|---|---|---|
| `apps/deployments` | `get`, `list`, `create`, `delete` | Create, observe, clean up one BuildKit Deployment per build. |
| core `pods` | `get`, `list` | Select the ready pod created by the Deployment controller. |
| core `pods/exec` | `create` | Buildx Kubernetes driver connects with `buildctl dial-stdio`. |

Do not grant `update`, `patch`, `watch`, Secret, ConfigMap, Service, Job, StatefulSet, PVC,
namespace, node, impersonation, token, attach, port-forward, or pod-create verbs unless a later
implementation proves one is necessary and updates this spec/security review. Persistent Buildx
cache is intentionally excluded, so StatefulSet/PVC verbs are unnecessary.

Add two validating admission policies scoped to `agentweaver-buildkit`:

- Deployment policy: names must start `awb-`; required ownership labels must be present; one
  replica only; exact pinned BuildKit image digest; exact daemon service account; no init,
  ephemeral, or additional containers; no host namespaces, host ports, hostPath, devices, or
  extra volumes; fixed Kata runtime and resource bounds.
- Pod policy: admit only pods controlled by a conforming `awb-` ReplicaSet; require the exact
  rootless security context and volumes below; deny every other pod.

The broker must fail closed if either policy or its binding is absent.

### BuildKit pod security contract

The pre-created Deployment must specify:

- `runtimeClassName: kata-vm-isolation`;
- `runAsNonRoot: true`, `runAsUser: 1000`, and `runAsGroup: 1000`;
- `privileged: false`, `allowPrivilegeEscalation: false`, and drop all capabilities;
- `seccompProfile: Unconfined` and `appArmorProfile: Unconfined`, narrowly accepted only in the
  dedicated namespace by the admission policy;
- `readOnlyRootFilesystem: true`, with writable size-limited `emptyDir` mounts only for BuildKit
  state, `/tmp`, and `/run/user/1000`;
- `automountServiceAccountToken: false`;
- no `hostNetwork`, `hostPID`, `hostIPC`, `hostUsers: true`, hostPath, Docker socket, `/dev/fuse`,
  device plugin, or privileged QEMU initializer;
- `--oci-worker-no-process-sandbox` and `--oci-worker-snapshotter=native` for the initial
  compatibility profile;
- requests of 1 CPU, 2 GiB memory, and 8 GiB ephemeral storage; limits of 2 CPU, 4 GiB memory,
  and 20 GiB ephemeral storage.

`qemu.install=true` is prohibited because Buildx implements it with a privileged init container.
Multi-architecture builds require a later design using native architecture-specific nodes.

Rootless BuildKit under Kata, read-only root filesystem support, unprivileged user namespaces, and
the native snapshotter must be proven on the deployed AKS node image in a non-production cluster.
If any check fails, do not fall back to privileged mode, Docker-in-Docker, `docker.sock`, runc on
the host kernel, `/dev/fuse`, or a broader security profile. Keep the capability disabled and open
a follow-up design issue.

### Registry authentication and destination policy

The first implementation supports ACR through a named operator-created registry profile:

```text
profile name
registry host
allowed repository prefix
managed identity client ID
maximum context/build limits (optional lower overrides)
```

- Global configuration contains the available profiles but no credential material.
- A project Owner may select one profile and enable image builds for that project.
- The broker service account federates to a dedicated `agentweaver-buildkit-identity`, separate
  from API, worker, AgentHost, and cluster pull identities.
- The identity must receive repository-scoped `Container Registry Repository Writer` access on
  an ABAC-enabled ACR for the configured prefix. Provisioning must not silently fall back to
  registry-wide `AcrPush`.
- The broker exchanges workload identity for a short-lived ACR token. It writes the minimal
  Docker config to a mode-0700 memory-backed directory, passes only the directory path to Buildx,
  and recursively deletes it in `finally`.
- The token, Docker config, authorization headers, and credential-provider output are redacted
  from logs, traces, exceptions, events, and tool responses.
- The credential is forwarded through the BuildKit session for the approved registry only. It is
  not mounted into the BuildKit pod and is not available to Dockerfile `RUN` steps.

Generic registries, static passwords, personal access tokens, project-supplied credentials, and
cross-registry cache are out of scope. They require a separate credential-provider review.

### Network policy

Apply default-deny ingress and egress in `agentweaver-buildkit`.

BuildKit pods may:

- resolve DNS through kube-dns;
- use TCP/443 to public endpoints while excluding RFC1918, link-local, cluster, and metadata
  ranges;
- use explicit operator-configured CIDRs on TCP/443 for an approved private ACR endpoint.

No broad east-west access is allowed. The broker connects through Kubernetes `pods/exec`, so no
BuildKit Service or pod ingress exception is required. The daemon has no Kubernetes API token.

The broker receives ingress only from API pods on its internal port. Its egress is limited to
kube-dns, the Kubernetes API server on TCP/443, Entra token exchange, approved ACR endpoints, and
Application Insights/OpenTelemetry destinations already used by the platform.

Dockerfile build steps share the BuildKit pod network. Approval text must therefore state that a
Dockerfile can send files present in the approved context to public HTTPS endpoints. Network
policy reduces lateral movement but cannot make an untrusted Dockerfile safe to run on secret
material.

### Resource quotas and multi-tenant isolation

Initial cluster defaults:

- two concurrent builds globally;
- one active build per project and per run;
- `pods: 6`, `count/deployments.apps: 4`;
- namespace request/limit quotas sized for two default builders plus headroom;
- no PVCs, Services, Secrets, Jobs, or StatefulSets created by the build path.

Concurrency is acquired before context upload and released in `finally`. Requests beyond the
limit return a visible queued/busy result rather than creating unbounded Pending pods.

Each build gets:

- a unique Deployment, Buildx state directory, context directory, and build record;
- an empty local cache, preventing cache or metadata reuse between projects;
- labels for opaque `build_id`, `run_id`, and a one-way project identifier;
- no shared writable volume with AgentHost, API, another build, or another tenant.

Registry cache may be added later only with a per-project repository prefix and explicit lifecycle
policy. A shared BuildKit daemon or shared local cache is not allowed.

### Configuration and approval

Global configuration defaults:

```text
ImageBuilds:Enabled=false
ImageBuilds:Namespace=agentweaver-buildkit
ImageBuilds:MaxConcurrentBuilds=2
ImageBuilds:MaxContextBytes=1073741824
ImageBuilds:BuildTimeout=00:30:00
ImageBuilds:BuildKitImage=<pinned digest>
ImageBuilds:RegistryProfiles=[]
```

Per-project sandbox policy adds:

```text
ImageBuildsEnabled=false
ImageBuildRegistryProfile=null
```

Effective enablement requires all of:

- global `ImageBuilds:Enabled=true`;
- an installed and healthy broker, namespace, quota, policies, and bindings;
- project `ImageBuildsEnabled=true`;
- a valid configured registry profile;
- project network policy enabled;
- a Kubernetes pod-per-run AgentHost;
- an explicit human approval for this invocation.

Only a project Owner may change the project settings. `build_docker_image` remains on the
destructive/irreversible approval floor: unattended pickup, autopilot, and
`PickupAutoApproveTools` cannot approve it. The approval prompt shows image destination, context
relative path, Dockerfile relative path, size/file counts, target stage, and timeout.

### Lifecycle and cleanup

Build states are `queued`, `uploading`, `starting`, `building`, `pushing`, `succeeded`, `failed`,
`cancelled`, and `cleanup_failed`.

- A 30-minute deadline covers upload, bootstrap, build, and push.
- Cancellation terminates the Buildx process, deletes the Deployment with foreground cascading,
  removes context/auth/Buildx directories, and marks the record terminal.
- Normal completion performs the same cleanup before returning success.
- A broker reaper runs every five minutes and deletes labeled Deployments and local build
  directories older than one hour when no non-terminal record owns them.
- Cleanup is idempotent and retries with bounded backoff.
- A cleanup failure is visible to operators and increments an alerting metric; it never changes a
  failed build into success.
- Cleanup does not delete a successfully pushed registry manifest. The immutable digest remains
  an auditable external side effect.

### Observability

Emit structured logs, traces, run events, and metrics without secrets or context contents.

Required correlation fields: `build_id`, `run_id`, one-way `project_id`, registry host, repository
prefix, image tag, phase, result code, BuildKit image digest, and broker version.

Required metrics:

- active/queued builds and quota rejections;
- context bytes/files and upload duration;
- bootstrap, build, push, total, and cleanup duration;
- success/failure/cancel/timeout counts by stable reason code;
- orphaned Deployment count and cleanup failures;
- broker-to-Kubernetes and registry-auth failures.

Required run events: `image_build.requested`, `image_build.started`, periodic bounded progress,
`image_build.succeeded` with pushed digest, and `image_build.failed`/`cancelled`. Raw BuildKit logs
are bounded, redacted, and stored as a run artifact only when platform artifact retention permits.

## Scope

### In

- implementation-ready component and trust-boundary design
- rootless BuildKit Kubernetes-driver integration through a trusted broker
- namespace, service account, RBAC, admission, network, resource, storage, and cleanup contracts
- ACR workload-identity authentication without exposing credentials to AgentHost or Dockerfile steps
- disabled-by-default global/project configuration and mandatory approval
- multi-tenant isolation, observability, and validation gates

### Out

- implementing or enabling the feature in this issue
- local Docker builds or fallback to a developer Docker daemon
- privileged containers, Docker-in-Docker, Docker socket mounts, hostPath, `/dev/fuse`, or
  privileged QEMU/binfmt
- generic registry passwords/tokens, arbitrary registry destinations, shared builders, persistent
  local cache, cross-project cache, or multi-platform emulation
- arbitrary Buildx/BuildKit flags, entitlements, secrets, SSH forwarding, or custom outputs

## Acceptance criteria

- [ ] AgentHost receives no new Kubernetes RoleBinding, cloud role, registry credential, Docker
      socket, Docker daemon, or privileged container.
- [ ] The feature is absent by default and requires global enablement, project Owner enablement,
      an approved registry profile, and per-invocation human approval.
- [ ] The broker is the only identity with the exact namespace-scoped RBAC table above.
- [ ] Admission tests reject privileged/root, wrong-image, extra-container, host namespace,
      hostPath/device, service-account-token, non-Kata, over-limit, and unlabeled BuildKit pods.
- [ ] Context tests reject traversal, absolute paths, escaping symlinks/hardlinks, devices, excess
      size/count, and cross-run roots.
- [ ] Registry tests prove credentials never appear in AgentHost, build context, pod spec,
      Kubernetes Secret, command line, logs, events, traces, artifacts, or final image history.
- [ ] Concurrent builds from different projects use distinct Deployments, directories, empty
      caches, credentials, records, and repository prefixes.
- [ ] Timeout, cancellation, broker crash, BuildKit crash, failed push, and API restart all converge
      to terminal state and idempotent cleanup.
- [ ] A successful integration test builds a minimal image, pushes it to an isolated test prefix,
      verifies the returned digest, and confirms all Kubernetes/build/auth artifacts are removed.
- [ ] `kubectl kustomize k8s/overlays/production` renders every new resource, the deployment
      grouping inventory includes every rendered document, and manifest contract tests pin RBAC,
      security context, policies, quotas, and default-off configuration.
- [ ] `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj
      -p:CopilotSkipCliDownload=true`, the targeted Node deployment-render tests, and
      `npm run docs:build` pass for the implementation PR.
- [ ] Non-production AKS validation proves rootless BuildKit with native snapshotter under Kata,
      the pinned node image, read-only root filesystem, admission policies, and network policies.
- [ ] A security reviewer approves the RBAC, admission-policy exception, registry identity,
      context archiving, network egress, and credential-redaction evidence before enablement.

## Notable edge cases

- Rootless BuildKit may not run under the current Kata guest kernel or AKS user-namespace settings.
  Failure leaves the feature disabled; it never triggers a less-isolated fallback.
- BuildKit requires unconfined seccomp/AppArmor even in rootless mode. The dedicated namespace and
  exact admission allowlist contain this exception; it must not spread to AgentHost or app pods.
- A Dockerfile can intentionally copy and publish sensitive files that are legitimately in its
  approved context. The bounded context, mandatory approval, destination allowlist, and context
  exclusion rules are the controls; operators must not place credentials in source workspaces.
- A push can succeed immediately before cancellation or timeout. Reconciliation checks the
  registry digest and records the external side effect instead of claiming it was rolled back.
- Tags are mutable. The result and audit event always record the immutable digest returned by the
  registry.
- Private registries require explicit TCP/443 CIDRs because Kata prevents reliable Cilium FQDN
  policy enforcement in this deployment.
- Registry throttling, quota exhaustion, scheduling delay, and image-pull failure return distinct
  stable reason codes and do not leave a reusable builder.
