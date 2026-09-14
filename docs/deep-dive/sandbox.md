# Sandbox Subsystem — Conceptual Deep Dive

## What the sandbox is protecting against

Agentweaver lets an AI agent inspect files, edit a workspace, search source, and optionally run shell commands. Those capabilities are useful only if the agent can act like an engineer, but they also create an escape problem: a model can be mistaken, prompt-injected, or asked to run commands whose side effects are broader than the current project.

The sandbox is therefore built around one rule: **agent actions must be useful inside the assigned workspace and boring everywhere else**. A well-behaved agent should barely notice the sandbox. A malicious or confused agent should be unable to:

- read or write files outside the run workspace;
- escape through `..`, absolute paths, Windows drive tricks, UNC/device paths, symlinks, or junctions;
- run host-level shell commands when no real process isolation exists;
- reach arbitrary internal services from a production sandbox pod;
- leave long-lived compute, network listeners, or preview tunnels after the run ends;
- exfiltrate obvious secrets through large command output.

This is not one mechanism. It is a layered isolation model:

1. **Governance decides whether a tool call is allowed.** Unknown tools and suspicious paths fail closed before tool code runs.
2. **Filesystem tools validate again at the point of use.** The implementation assumes governance can be bypassed or fed malformed arguments.
3. **Shell commands run through an executor abstraction.** The runtime can choose a local isolation backend for development or a Kubernetes-backed sandbox for production.
4. **Production sandboxes are isolated pods.** They use hardened pod settings, a shared workspace mount, bounded lifetime claims, and network policy egress controls.
5. **Output is bounded and redacted.** The sandbox assumes command output itself can become an exfiltration channel.

The result is defense in depth rather than a single perfect wall.

Where this lives: `packages/Agentweaver.AgentRuntime`, `packages/Agentweaver.AgentTools`, `packages/Agentweaver.SandboxFs`, `packages/Agentweaver.SandboxExec`, `apps/Agentweaver.Api/Sandbox`, `k8s`

## The core mental model

Think of every agent action as passing through three concentric boundaries:

Each boundary has a different job:

- **Governance boundary:** answers “is this kind of action allowed for this run?” It is intentionally deny-by-default. A tool name must be recognized, and path-bearing arguments must resolve inside the sandbox root.
- **Tool boundary:** answers “is this exact file operation safe right now?” It re-validates paths, rejects reparse-point escapes, and returns controlled failures to the agent loop.
- **Execution boundary:** answers “where does this process actually run?” It hides the host behind a container, namespace, VM, or — only in explicit non-production cases — direct passthrough.
- **Network boundary:** answers “what can this isolated process talk to?” In production, this is handled by Kubernetes/Cilium policy, not by trusting shell command text.

The important design choice is that boundaries are **redundant**. Governance is not trusted as the only check, path validation is not trusted as process isolation, and process isolation is not trusted as network isolation.

## Why command execution needs stronger isolation than file tools

File tools are narrow: read this path, write this file, search this tree. Shell commands are broad: a single command can spawn processes, run interpreters, traverse the filesystem, open sockets, install packages, fork children, or encode data into output.

For that reason, `run_command` is treated as a privileged capability:

1. The shell tool is only registered when shell execution is enabled and the selected executor is acceptable for the current mode.
2. Destructive command patterns, or policies that require approval for all shell commands, trigger a human-in-the-loop approval gate before execution.
3. The command validator rejects malformed shell requests such as missing/invalid working directories, null bytes, or excessive command length.
4. `run_command` is for finite commands. Its default execution budget is 30 minutes and can be overridden with `AGENTWEAVER_RUN_COMMAND_DEFAULT_TIMEOUT_SECONDS` or a tool-call `timeout_ms`. If the budget expires, Agentweaver cancels the sandbox process and returns a visible `timed_out: true` failure that tells the model to use `start_preview_process` for long-lived preview/dev servers.
5. The command is packaged with the run workspace, timeout, filesystem policy, network flag, and optional run ID.
6. The selected executor runs it and returns only bounded, redacted stdout/stderr plus an exit code.

This design does not try to parse every shell command into safe and unsafe subcommands. That would be brittle. Instead, the system validates the shell envelope, requires approval for dangerous patterns, and relies on the executor boundary to contain whatever the shell actually does. The executor does use a narrow, non-security-critical package-manager hint to decide whether to prepare an optional writable system root; a miss only makes `/usr`, `/etc`, and `/var` read-only for that command.

Where this lives: `packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs`, `packages/Agentweaver.SandboxExec`

## Filesystem containment: make the workspace the only universe

The filesystem sandbox exists because path strings are adversarial input. An agent may ask for `../../secrets`, an absolute host path, a Windows device path, a UNC share, or a benign-looking path whose parent is a symlink to somewhere else. The containment rule is simple: **all file effects must resolve to the sandbox root or one of its children**.

A rebuild should implement containment in two phases.

### Phase 1: lexical rejection before touching the filesystem

Before opening anything, reject inputs that are obviously outside the contract:

- empty paths;
- absolute paths when the tool expects a relative workspace path;
- `..` path segments;
- Windows device paths such as `\\?\` or `\\.\`;
- UNC paths such as `\\server\share`;
- drive-relative paths such as `C:foo`;
- normalized paths whose prefix is not the normalized sandbox root.

This catches cheap escape attempts without giving the filesystem a chance to resolve links or special names.

### Phase 2: real-path verification at the point of use

Lexical checks are necessary but insufficient. A path can look safe and still escape through a symlink or junction. Agentweaver therefore treats symlink/junction ancestors as untrusted and verifies the final opened handle where possible.

The invariant is:

> The path must be inside the workspace both before opening and after the OS resolves the object that was opened.

That second check matters because it narrows time-of-check/time-of-use races. If an attacker swaps a path between validation and open, the final handle resolution can still detect that the opened object is outside the sandbox.

### Why tools return structured failures

Sandbox violations are not treated as fatal runtime crashes. File tools return clear, structured failures to the agent. This keeps the run alive while making the boundary visible: the agent can choose a safe path and continue, but it cannot pressure the runtime into ignoring the violation.

### Search is constrained enumeration

Search tools do not accept arbitrary host roots. They enumerate the sandbox root, avoid reparse points, skip high-noise/generated directories such as `.git`, `node_modules`, `bin`, `obj`, and `.vs`, and cap results. This is partly security and partly agent ergonomics: bounded search prevents accidental huge responses and reduces the chance of leaking irrelevant data.

Where this lives: `packages/Agentweaver.SandboxFs`

## Governance: fail closed before side effects

The governance layer is the first policy checkpoint for model-selected tools. Its conceptual contract is:

- default action is deny;
- unknown tool names are denied;
- known file tools must provide a recognized path argument;
- search tools are allowed only because they are implemented as sandbox-root enumeration;
- shell tools must provide a working directory inside the sandbox root;
- internal governance exceptions deny the call rather than allowing it.

Agentweaver also performs a direct sandbox-backend evaluation in addition to the governance kernel evaluation. That redundancy is deliberate: even if one policy integration changes behavior, the dedicated containment backend still has to approve the call.

For shell specifically, governance adds a capability check: if the selected executor does not represent acceptable isolation for shell mode, shell execution is denied. The model should not be able to obtain a host shell merely because a tool name exists.

Where this lives: `packages/Agentweaver.AgentRuntime/SandboxGovernance.cs`, `packages/Agentweaver.SandboxFs/SandboxPolicyBackend.cs`

## Executor abstraction: one command contract, many isolation backends

The executor abstraction separates “what the agent wants to run” from “where and how it runs.” The runtime passes a command object containing:

- command line;
- working directory;
- environment variables;
- filesystem policy;
- timeout;
- network-enabled flag;
- optional Agentweaver run ID.

Every executor returns the same shape: exit code, stdout, stderr, timeout flag, and output-truncated flag. This uniform contract lets the agent runtime stay stable while deployments choose different isolation implementations.

### Backend selection logic

Executor selection is environment-aware:

Selected Kubernetes initialization fails closed. The API router first chooses Kubernetes versus local using the explicit backend and cluster detection; `Sandbox:Backend=local` still chooses the local factory inside a cluster.

### Local backends and their trade-offs

Local execution exists for development, tests, and non-cluster deployments. It is intentionally best-effort and transparent about gaps:

| Backend family | Conceptual role | Trade-off |
| --- | --- | --- |
| Windows process container (`mxc`) | Use OS/container support to isolate a process from the host. | Network allowlisting is not equivalent to Kubernetes policy, so warnings are surfaced. |
| WSL2: `wsl-bwrap` / `wsl-unshare` | Bubblewrap reports real isolation; unshare does not. | Controlled shell is not registered for `wsl-unshare`; the two backends are not equivalent. |
| Native Linux bubblewrap | Bind the workspace as `/workspace`, mount only selected runtime paths read-only, create tmpfs homes/temp, and unshare PID/user/network namespaces unless network is enabled. | Useful local isolation, but still not the same operational boundary as a production Kata pod plus cluster policy. |
| Native Linux `lxc-exec` | Fallback Linux isolation when bubblewrap is unavailable. | Depends on host LXC availability and configuration. |
| Direct passthrough | Last-resort host shell. | **Not isolation.** Use only when the surrounding environment is already disposable or explicitly trusted. |

Where this lives: `packages/Agentweaver.SandboxExec`, `apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs`

## Kubernetes sandbox lifecycle: retained utility-command contract

The retained Kubernetes utility-command API uses claims and pod exec. It is not the deployed AgentHost turn loop: model-controlled commands there use pod-private PodExec and the executor sidecar, not a new claim for every shell invocation. The retained contract is:

- A **SandboxTemplate** defines the pod shape and hardening policy.
- A **SandboxWarmPool** keeps ready sandboxes available from that template.
- A **SandboxClaim** asks the sandbox controller for one sandbox instance for a bounded TTL.
- The executor waits until the claim is bound to a concrete pod, then uses Kubernetes pod exec to run the command.

### The agent-sandbox controller (and where MXC fits)

The "Sandbox controller" above is the upstream [`kubernetes-sigs/agent-sandbox`](https://github.com/kubernetes-sigs/agent-sandbox) controller — **not** MXC. The two are different runtimes for different tiers, and the names are easy to conflate:

- **MXC** (`Sabbour.Mxc.Sdk` / `wxc-exec.exe`) is the **local-host** command isolation runtime behind the Windows `processcontainer`, WSL, and Linux `lxc-exec` executors. It runs on a developer or non-cluster host and has no Kubernetes presence.
- The **agent-sandbox controller** is the **in-cluster** runtime that turns a `SandboxClaim` into a bound, Kata-isolated pod. `KubernetesSandboxExecutor` talks only to this controller's CRDs; no MXC binary exists in the cluster.

Agentweaver installs the controller and its three CRDs (API group `extensions.agents.x-k8s.io`) in `scripts/azure/steps/10-create-cluster.mjs` (install default `SANDBOX_CONTROLLER_VERSION=v0.5.3` — production clusters run **agent-sandbox v0.5.3**, #487). The installed controller serves both `v1beta1` (the **storage** version) and the deprecated-but-served `v1alpha1`; `KubernetesSandboxExecutor` targets **`v1beta1`** ([`SandboxClaimConventions.cs:23`](#source)):

- **`SandboxTemplate`** (`k8s/base/sandbox-template-agenthost.yaml`, `agentweaver-agent-host`) defines the live AgentHost pod shape: `kata-vm-isolation` runtime class, non-root UID/GID 1000, dropped capabilities, `/workspace` PVC, A2A listener port `8088`, workload identity, and the `agentweaver-exec` **executor sidecar** — a second container from the same image that owns every model-controlled process in its own PID namespace (see [sandbox pod execution](./sandbox-pod-execution.md#why-a-sidecar-and-not-a-nested-pid-namespace)).
- **`SandboxWarmPool`** keeps AgentHost pods pre-built from that template so claims bind without a cold pod start. The live pool is `agentweaver-agent-host` (`k8s/base/sandbox-warmpool-agenthost.yaml`, `replicas: 2`). AgentHost warm pods boot without `RunId`, enter standby, and are configured after binding by `POST /configure`, so the .NET process and Copilot SDK are pre-warmed without per-run env.
- **`SandboxClaim`** (created per run by `KubernetesSandboxExecutor`; shape in `k8s/reference/sandbox-claim-template.yaml`) carries `spec.warmPoolRef.name` (`agentweaver-agent-host` on the live path) and `spec.lifecycle.{ttlSecondsAfterFinished, shutdownPolicy: Delete}`. The AgentHost claim omits `spec.env`; static values belong to the template/config map. Per-run identity, workspace, credentials, turn authentication, purpose, and approval values arrive later via `/configure`. The controller adopts a warm pod, then signals readiness with a `Ready` **condition** (`status.conditions[type=Ready].status == "True"`) and writes the bound pod name into `status.sandbox.name`. There is **no** `status.phase` field.
- **Model-controlled `run_command` calls do not create a Kubernetes claim or pod exec session.**
  After the AgentHost pod is configured, the tool uses the pod-private `agentweaver-exec`
  sidecar over authenticated IPC. The sidecar only starts the optional per-run writable system
  root for package-manager commands (`apt`, `apt-get`, `dpkg`, etc.); ordinary commands run
  directly in the read-only system-root bubblewrap view so they do not pay package-manager setup
  latency on the happy path.

The executor's provisioning loop is the concrete contract with the controller:

1. `CreateClaimAsync` / `CreateAgentHostClaimAsync` POSTs the v1beta1 `SandboxClaim` custom object into the namespace ([`KubernetesSandboxExecutor.cs:354`, `:294`](#source)).
2. `WaitForBoundAsync` polls the claim every 2 s until its `Ready` condition is `True`, then returns `status.sandbox.name` ([`SandboxClaimConventions.cs:53`](#source)).
3. For AgentHost, resolve pod IP, wait for the HTTP 200 standby listener, configure once and complete setup, then register the effective workspace and A2A endpoint.
4. On claim deletion (ad-hoc command) or TTL expiry, the controller garbage-collects the pod and its service — Agentweaver never deletes pods directly.

### Why claims have TTLs

A shell command can hang, or a client can disconnect. The claim TTL gives the controller an independent cleanup clock. Agentweaver also clamps the command timeout below the claim TTL so the controller should not delete the sandbox while the executor is still expecting a result.

### Why run IDs map to pod names

Run-scoped utility commands derive a stable claim name and record the pod. The local `kubectl` fallback may use that mapping. Production preview resolves claims and HTTPRoute annotations from cluster state, not a replica-local pod registry; see [sandbox browser preview](./sandbox-browser-preview.md).

### Why ad-hoc and run-scoped cleanup differ

Ad-hoc commands have no reason to keep a sandbox alive after the command returns, so the claim is deleted immediately. Run-scoped commands may keep the claim until cleanup or TTL so preview remains available. That improves developer experience but consumes warm-pool/quota capacity for longer, so operators must size quotas and warm pools accordingly.

Where this lives: `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`, `apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs`, `apps/Agentweaver.Api/Sandbox/PortForwardService.cs`, `apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs`, `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs`, `k8s/base/sandbox-template-agenthost.yaml`, `k8s/base/sandbox-warmpool-agenthost.yaml`, `k8s/reference/sandbox-claim-template.yaml`

### AgentHost warm-pool configure contract

AgentHost uses two standby warm pods. Shipped claims omit `spec.env`; static configuration belongs to the template/config map. Setup waits for one-time `/configure`. See the claim/configure sequence.

Per-run values are delivered by `POST /configure` after the claim binds:

| `/configure` field | Purpose |
| --- | --- |
| `runId` | The Agentweaver run this pod executes; missing values return `400`. |
| `userId` | The submitting user; drives `RuntimeUserScopeProvider`. |
| `turnBearerToken` | Production supplies a 256-bit per-run bearer token; turn middleware enforces equality when the configured token is nonempty. |
| `copilotCredential` / `byokProviderConfiguration` | Alternative run-scoped model-provider payloads. |
| `workingDirectory` | Shared coordinate; local modes resolve a verified pod-local effective execution directory. |

`TryConfigure` is one-shot; repeat configuration returns 409. Setup resolves a valid Shared workspace, a verified local checkout, or a pod-private fallback when Shared has no supplied path. The effective directory is not universally `Run.WorktreePath`. `/healthz` is already 200 in standby. Production supplies a turn token, enforced when nonempty. `/configure` delivers that token and instead relies on configured transport controls and network policy; the additive preview range includes 8088, so policy alone is not API/worker-exclusive.

The executor does not create per-run `SecretProviderClass` objects, cloned `SandboxTemplate`s, or per-run warm pools for AgentHost. It sends the required run-scoped provider and repository capability data through `/configure`. The sandbox identity has no Key Vault access and cannot retrieve ambient user credentials.

Where this lives:

| Source | Role |
| --- | --- |
| `apps/Agentweaver.Api/Sandbox/IRunSubmittingUserResolver.cs` | Resolves the submitting user and the run's `WorktreePath`, stripping coordinator sub-run suffixes so child stages inherit the parent's shared worktree. |
| `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs` | Resolves `workingDirectory` without failing launch on lookup errors and includes it in the one-time `/configure` body. |
| `apps/Agentweaver.AgentHost/Program.cs` | Defines the `/configure` request body, including `workingDirectory`, and passes it to AgentHost startup. |
| `apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs` | Stores the one-time run/user/token configuration. |
| `apps/Agentweaver.AgentHost/AgentHostStartupService.cs` | Runs `SetupAsync` after `/configure`, using the per-run working directory when present and preserving env-var launch behavior otherwise. |
| `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs` | Receives the configured working directory as the agent setup/file-tool root. |

## Production pod isolation and hardening

The AgentHost sandbox pod contains the runtime needed for live agent turns, but it is not privileged. Because it executes untrusted shell/tool code, it runs as a dedicated managed identity with **no Key Vault role assignments**. It receives only run-scoped capability data through `/configure`.

The production template applies several important constraints:

- **Kata runtime:** the pod uses `kata-vm-isolation`, adding a VM boundary around the container workload.
- **Non-root identity:** the container runs as UID/GID 1000.
- **No privilege escalation and no Linux capabilities:** the process should not be able to acquire broader kernel privileges.
- **Separate platform and child views:** platform containers have writable roots and workspace/scratch/HOME mounts. The model-controlled child has a narrower allowlist without the PVC root, siblings or IPC secrets.
- **Never restart:** failed or completed sandbox pods are not automatically restarted as hidden long-lived state.

The image carries the AgentHost .NET/Copilot runtime. The security posture comes from pod/runtime policy, workload identity scoping, and network policy, not from making the image empty.

Important boundary: the shared workspace PVC is an execution workspace, not a secrecy boundary between every workload that can mount it. The sandbox limits process and network blast radius; it does not make shared storage private from other principals with access to the same volume.

Where this lives: `apps/Agentweaver.AgentHost/Dockerfile`, `k8s/base/sandbox-template-agenthost.yaml`

## Network isolation and egress allowlisting

Network access is an exfiltration and lateral-movement channel. A sandboxed command that can reach arbitrary addresses can probe cluster services, call metadata endpoints, or send data to the internet. Production network policy therefore follows an allowlist model.

Current policies select `app: agentweaver-agent-host`: default deny plus API/worker control ingress, same-namespace preview-Gateway ingress, DNS, API/MCP egress and public-IP HTTPS with explicit CIDR exclusions. They are not a GitHub-only or FQDN-only allowlist. AgentHost has a service-account token; the executor and its child view exclude that identity. See [actual rules and manifest gaps](./infra-deployment.md#network-policy-model).

The service-CIDR warning is important. If sandbox egress accidentally includes the cluster service CIDR, a sandbox pod may be able to reach internal Kubernetes services even if internet egress looks restricted. Agentweaver checks configured service CIDR exclusions and logs a warning when the cluster service CIDR is not excluded.

Local backends cannot all enforce the same network model. Where network allowlisting is unavailable or weaker, executors report warnings, and runners emit sandbox warning events. Treat those warnings as a deployment property, not as an agent-visible suggestion.

Where this lives: `k8s/base/networkpolicy-sandbox.yaml`, `k8s/base/cilium-network-policy-sandbox.yaml`, `apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs`, `packages/Agentweaver.SandboxExec`

## Rebuild blueprint

If rebuilding this subsystem from scratch, implement it in this order:

1. **Define the workspace invariant.** Pick one sandbox root per run. Every file tool, search tool, and shell working directory must resolve inside it.
2. **Create a path validator.** Reject obvious path escapes lexically, reject symlink/junction ancestors, and verify opened handles against the sandbox root.
3. **Wrap all file/search tools.** Do not expose raw host file APIs to the model. Return structured errors for violations.
4. **Add deny-by-default governance.** Allow only known tools and known argument shapes. Fail closed on unknown tools and internal policy errors.
5. **Define a command executor interface.** Keep command input/output stable so the runtime is independent of the isolation backend.
6. **Gate shell execution.** Require shell enablement, working-directory containment, destructive-command approval, timeout bounds, output caps, and redaction.
7. **Provide local executors.** Prefer real local isolation where available; mark direct passthrough as non-production and warn loudly.
8. **Provide a production Kubernetes executor.** Use claims, templates, warm pools, TTLs, pod exec, and fail-closed backend selection.
9. **Harden platform and child separately.** Preserve Kata, non-root execution, dropped capabilities, distinct container PID namespaces and the per-child mount allowlist. Current platform roots are writable; executor children must not inherit AgentHost identity or IPC secrets.
10. **Add network policy.** Default-deny with explicit control/preview ingress and the actual egress exceptions; assess the union rather than asserting domain-only access.
11. **Plan cleanup.** Delete ad-hoc claims, retain run-scoped claims only while previews need them, and enforce TTL/quota as independent backstops.
12. **Surface warnings.** If a backend cannot enforce a promised boundary, emit an explicit warning rather than silently weakening isolation.

## Security invariants and gotchas

- **Default deny is an invariant.** A new tool should do nothing until governance and tool-level validation know how to constrain it.
- **Path containment is checked more than once.** This is intentional defense in depth, not duplication to remove.
- **Shell parsing is not the security boundary.** The executor and OS/container boundary must contain arbitrary shell behavior.
- **Kubernetes fallback must fail closed.** In production, silently downgrading to local or direct execution is worse than failing the run.
- **Direct passthrough is not a sandbox.** It is useful only when the host environment is already disposable/trusted.
- **Run-scoped sandboxes consume capacity while retained.** Preview support trades resource usage for debuggability.
- **Shared `/workspace` is shared storage.** Do not treat the PVC as a per-tenant secrecy boundary unless the surrounding storage model enforces that.
- **Output can leak data.** Keep caps and redaction even when process isolation is strong.
- **Directory listing has a narrow residual race.** The code documents a filename-only TOCTOU residual risk for listing; file reads/writes use stronger open-and-verify handling.
- **Output caps apply to command and tool results.** The command/tool output cap in this subsystem is 4 MiB; keep that limit in place so a single large result cannot exhaust memory or flood the event stream. Image or attachment upload paths belong to other components and are out of scope for this repo-owned sandbox subsystem.


<!-- flagship-diagrams:start -->
## Visual model

### Sandbox boundary

[![Deployment and trust-boundary view showing project sandbox declarations, Kubernetes SandboxClaim admission, the Kata AgentHost pod, point-of-use tool and filesystem checks, authenticated executor IPC, workspace mounts, network enforcement, and separate Key Vault authority.](../diagrams/flagship/canonical-sandbox-boundary.png)](../diagrams/drawio/generated/flagship/canonical-sandbox-boundary.drawio)

[Structured source](../diagrams/src/flagship/canonical-sandbox-boundary.json) · [Editable draw.io](../diagrams/drawio/generated/flagship/canonical-sandbox-boundary.drawio)
<!-- flagship-diagrams:end -->

## See also

- [Sandbox pod execution](./sandbox-pod-execution.md) - pod-local scratch workspaces, Git write-back,
  nested-repository flattening, and the HOME/XDG cache contract.
- [Sandbox browser preview](./sandbox-browser-preview.md) - exposing a server running inside a run's sandbox pod to the user over a public HTTPS reverse proxy (per-preview HTTPRoute -> per-run ClusterIP Service -> pod).

<details id="diagram-context-canonical-sandbox-boundary" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Several checks contain each action</td></tr>
<tr><td>takeaway</td><td>Native shell is denied; governed tools combine AGT policy, direct containment and execution isolation.</td></tr>
<tr><td>group-title0</td><td>TOOL SELECTION / POLICY</td></tr>
<tr><td>group-title1</td><td>POINT-OF-USE CONTAINMENT</td></tr>
<tr><td>Model tool request</td><td>Model tool request</td></tr>
<tr><td>Model tool request</td><td>Permission dispatch</td></tr>
<tr><td>Model tool request</td><td>Native shell: always denied</td></tr>
<tr><td>Model tool request</td><td>URL approvals handled apart</td></tr>
<tr><td>Model tool request</td><td>Custom reporting bypass</td></tr>
<tr><td>Governance</td><td>Governance</td></tr>
<tr><td>Governance</td><td>Deny-by-default policy</td></tr>
<tr><td>Governance</td><td>AGT policy must allow</td></tr>
<tr><td>Governance</td><td>Direct backend must allow</td></tr>
<tr><td>Governance</td><td>Both checks, not either</td></tr>
<tr><td>Registered tools</td><td>Registered tools</td></tr>
<tr><td>Registered tools</td><td>Explicit capability surface</td></tr>
<tr><td>Registered tools</td><td>Files revalidate at use</td></tr>
<tr><td>Registered tools</td><td>run_command gates shell</td></tr>
<tr><td>Registered tools</td><td>Unknown tools denied</td></tr>
<tr><td>Workspace boundary</td><td>Workspace boundary</td></tr>
<tr><td>Workspace boundary</td><td>Sandbox filesystem</td></tr>
<tr><td>Workspace boundary</td><td>Lexical + real-path checks</td></tr>
<tr><td>Workspace boundary</td><td>Reject symlink escapes</td></tr>
<tr><td>Workspace boundary</td><td>Bounded / redacted output</td></tr>
<tr><td>Execution boundary</td><td>Execution boundary</td></tr>
<tr><td>Execution boundary</td><td>Selected isolation backend</td></tr>
<tr><td>Execution boundary</td><td>Shell policy + approval</td></tr>
<tr><td>Execution boundary</td><td>Kata pod in AKS</td></tr>
<tr><td>Execution boundary</td><td>Direct mode is opt-in</td></tr>
<tr><td>Credential handling</td><td>Credential handling</td></tr>
<tr><td>Credential handling</td><td>Current implementation</td></tr>
<tr><td>Credential handling</td><td>Host + tool options hold token</td></tr>
<tr><td>Credential handling</td><td>Direct git status / allowed gh</td></tr>
<tr><td>Credential handling</td><td>No blanket shell injection</td></tr>
<tr><td>relation-0</td><td>1 governed calls</td></tr>
<tr><td>relation-1</td><td>2 both allow</td></tr>
<tr><td>relation-2</td><td>3 file operation</td></tr>
<tr><td>relation-3</td><td>4 run_command</td></tr>
<tr><td>relation-4</td><td>5 eligible git / gh</td></tr>
<tr><td>assurance</td><td>Current code delivers repository credentials into Host/tool options; the normative no-credential contract is NOT met.</td></tr>
<tr><td>assurance-0-label</td><td>Dispatch exceptions</td></tr>
<tr><td>assurance-0-fact</td><td>Native shell denied; URL path separate.</td></tr>
<tr><td>assurance-0-source</td><td>CopilotAIAgent.cs</td></tr>
<tr><td>assurance-1-label</td><td>Execution isolation</td></tr>
<tr><td>assurance-1-fact</td><td>Sidecar: separate PID namespace.</td></tr>
<tr><td>assurance-1-source</td><td>sandbox-template-agenthost.yaml</td></tr>
<tr><td>assurance-2-label</td><td>Credential reality</td></tr>
<tr><td>assurance-2-fact</td><td>No blanket shell credential inheritance.</td></tr>
<tr><td>assurance-2-source</td><td>RunCommandTool.cs</td></tr>
<tr><td>n0</td><td>Native shell: always denied; URL approvals handled apart</td></tr>
<tr><td>n1</td><td>AGT policy must allow; Direct backend must allow</td></tr>
<tr><td>n2</td><td>Files revalidate at use; run_command gates shell</td></tr>
<tr><td>n3</td><td>Lexical + real-path checks; Reject symlink escapes</td></tr>
<tr><td>n4</td><td>Shell policy + approval; Kata pod in AKS</td></tr>
<tr><td>n5</td><td>Host + tool options hold token; Direct git status / allowed gh</td></tr>
<tr><td>groups</td><td>TOOL SELECTION / POLICY; POINT-OF-USE CONTAINMENT</td></tr>
</tbody></table>
</details>

<details id="diagram-context-sandbox-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Choose Kubernetes before local probing</td></tr>
<tr><td>takeaway</td><td>The API router owns cluster selection; the local factory can fall back to direct.</td></tr>
<tr><td>API executor router</td><td>API executor router</td></tr>
<tr><td>API executor router</td><td>Backend override / cluster detect</td></tr>
<tr><td>API executor router</td><td>Explicit local bypasses cluster</td></tr>
<tr><td>Kubernetes selected</td><td>Kubernetes selected</td></tr>
<tr><td>Kubernetes selected</td><td>Initialize claim executor</td></tr>
<tr><td>Kubernetes selected</td><td>Failure throws; no local fallback</td></tr>
<tr><td>Claim backend</td><td>Claim backend</td></tr>
<tr><td>Claim backend</td><td>Bound pod command contract</td></tr>
<tr><td>Claim backend</td><td>Not the in-pod PodExec client</td></tr>
<tr><td>Local factory</td><td>Local factory</td></tr>
<tr><td>Local factory</td><td>Only when router selects local</td></tr>
<tr><td>Local factory</td><td>Probe host-supported backends</td></tr>
<tr><td>Windows ladder</td><td>Windows ladder</td></tr>
<tr><td>Windows ladder</td><td>processcontainer -&gt; WSL</td></tr>
<tr><td>Windows ladder</td><td>wsl-bwrap real; unshare not real</td></tr>
<tr><td>Linux ladder</td><td>Linux ladder</td></tr>
<tr><td>Linux ladder</td><td>bubblewrap -&gt; LXC</td></tr>
<tr><td>Linux ladder</td><td>Host tools must be available</td></tr>
<tr><td>No usable isolation</td><td>No usable isolation</td></tr>
<tr><td>No usable isolation</td><td>Automatic local fallback</td></tr>
<tr><td>No usable isolation</td><td>Also explicit direct option</td></tr>
<tr><td>Direct passthrough</td><td>Direct passthrough</td></tr>
<tr><td>Direct passthrough</td><td>IsRealIsolation = false</td></tr>
<tr><td>Direct passthrough</td><td>Trusted/disposable host only</td></tr>
<tr><td>Command registration</td><td>Command registration</td></tr>
<tr><td>Command registration</td><td>ShellEnabled AND real or direct</td></tr>
<tr><td>Command registration</td><td>wsl-unshare: no controlled shell</td></tr>
<tr><td>arrow-1</td><td>select</td></tr>
<tr><td>arrow-2</td><td>ready</td></tr>
<tr><td>arrow-3</td><td>local</td></tr>
<tr><td>arrow-4</td><td>Windows</td></tr>
<tr><td>arrow-5</td><td>Linux</td></tr>
<tr><td>arrow-6</td><td>unavailable</td></tr>
<tr><td>arrow-8</td><td>warn</td></tr>
<tr><td>arrow-9</td><td>gate</td></tr>
<tr><td>note-0</td><td>The local platform ladders are alternatives, not a Windows-to-Linux chain.</td></tr>
<tr><td>note-1</td><td>Kubernetes failure never silently descends into the local ladder.</td></tr>
<tr><td>note-2</td><td>Runtime emits sandbox.selected; factory choice is not an isolation guarantee.</td></tr>
<tr><td>notes</td><td>The local platform ladders are alternatives, not a Windows-to-Linux chain.; Kubernetes failure never silently descends into the local ladder.; Runtime emits sandbox.selected; factory choice is not an isolation guarantee.</td></tr>
</tbody></table>
</details>

<details id="diagram-context-sandbox-fig3" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Retained utility-command claim lifecycle</td></tr>
<tr><td>takeaway</td><td>This API path is not the AgentHost model-controlled command loop.</td></tr>
<tr><td>Utility command</td><td>Utility command</td></tr>
<tr><td>Utility command</td><td>Workspace and bounded timeout</td></tr>
<tr><td>Utility command</td><td>Optional Agentweaver run ID</td></tr>
<tr><td>Kubernetes executor</td><td>Kubernetes executor</td></tr>
<tr><td>Kubernetes executor</td><td>Create or reuse claim</td></tr>
<tr><td>Kubernetes executor</td><td>Stable name for run-scoped work</td></tr>
<tr><td>Sandbox controller</td><td>Sandbox controller</td></tr>
<tr><td>Sandbox controller</td><td>Bind warm-pool pod</td></tr>
<tr><td>Sandbox controller</td><td>Ready condition + sandbox name</td></tr>
<tr><td>Command result</td><td>Command result</td></tr>
<tr><td>Command result</td><td>stdout / stderr / exit status</td></tr>
<tr><td>Command result</td><td>Timeout and output flags</td></tr>
<tr><td>Kubernetes pod exec</td><td>Kubernetes pod exec</td></tr>
<tr><td>Kubernetes pod exec</td><td>Execute retained API command</td></tr>
<tr><td>Kubernetes pod exec</td><td>No generic sleep pool implied</td></tr>
<tr><td>Bound pod</td><td>Bound pod</td></tr>
<tr><td>Bound pod</td><td>Current template is AgentHost</td></tr>
<tr><td>Bound pod</td><td>Not a separate generic deployment</td></tr>
<tr><td>New ad-hoc claim</td><td>New ad-hoc claim</td></tr>
<tr><td>New ad-hoc claim</td><td>Delete in command finally</td></tr>
<tr><td>New ad-hoc claim</td><td>No run-scoped retention needed</td></tr>
<tr><td>Run-scoped claim</td><td>Run-scoped claim</td></tr>
<tr><td>Run-scoped claim</td><td>Keep for cleanup / TTL</td></tr>
<tr><td>Run-scoped claim</td><td>Not deleted after every command</td></tr>
<tr><td>Production preview lookup</td><td>Production preview lookup</td></tr>
<tr><td>Production preview lookup</td><td>Uses cluster claim/route state</td></tr>
<tr><td>Production preview lookup</td><td>Not replica-local registry</td></tr>
<tr><td>arrow-1</td><td>submit</td></tr>
<tr><td>arrow-2</td><td>claim</td></tr>
<tr><td>arrow-3</td><td>bind</td></tr>
<tr><td>arrow-4</td><td>exec</td></tr>
<tr><td>arrow-5</td><td>return</td></tr>
<tr><td>note-0</td><td>Cleanup cards distinguish newly created ad-hoc and run-scoped claims.</td></tr>
<tr><td>note-1</td><td>AgentHost controlled tools instead use authenticated pod-private PodExec.</td></tr>
<tr><td>note-2</td><td>SQL run ownership leases are separate from this Kubernetes claim lifecycle.</td></tr>
<tr><td>notes</td><td>Cleanup cards distinguish newly created ad-hoc and run-scoped claims.; AgentHost controlled tools instead use authenticated pod-private PodExec.; SQL run ownership leases are separate from this Kubernetes claim lifecycle.</td></tr>
</tbody></table>
</details>

<details id="diagram-context-sandbox-pod-execution-fig6" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Bind, reach standby, configure once</td></tr>
<tr><td>takeaway</td><td>Liveness precedes configuration; production delivers run credentials out of pod specs.</td></tr>
<tr><td>Prepare launch</td><td>Prepare launch</td></tr>
<tr><td>Prepare launch</td><td>Resolve provider and run context</td></tr>
<tr><td>Prepare launch</td><td>Mint fresh turn token</td></tr>
<tr><td>Claim warm pod</td><td>Claim warm pod</td></tr>
<tr><td>Claim warm pod</td><td>Create/adopt; omit spec.env</td></tr>
<tr><td>Claim warm pod</td><td>Wait Ready + bound pod name</td></tr>
<tr><td>Resolve and register</td><td>Resolve and register</td></tr>
<tr><td>Resolve and register</td><td>Pod mapping / token registry</td></tr>
<tr><td>Resolve and register</td><td>Resolve actual pod IP</td></tr>
<tr><td>GET /healthz</td><td>GET /healthz</td></tr>
<tr><td>GET /healthz</td><td>HTTP 200 standby</td></tr>
<tr><td>GET /healthz</td><td>Listener liveness, not turn ready</td></tr>
<tr><td>POST /configure</td><td>POST /configure</td></tr>
<tr><td>POST /configure</td><td>Identity / workspace / approvals</td></tr>
<tr><td>POST /configure</td><td>Copilot capability OR BYOK</td></tr>
<tr><td>AgentHost setup</td><td>AgentHost setup</td></tr>
<tr><td>AgentHost setup</td><td>One-time configuration</td></tr>
<tr><td>AgentHost setup</td><td>Effective workspace + HOME</td></tr>
<tr><td>Configuration guards</td><td>Configuration guards</td></tr>
<tr><td>Configuration guards</td><td>Second configure: 409</td></tr>
<tr><td>Configuration guards</td><td>Other routes: 503 before ready</td></tr>
<tr><td>Register effective endpoint</td><td>Register effective endpoint</td></tr>
<tr><td>Register effective endpoint</td><td>Return effective working directory</td></tr>
<tr><td>Register effective endpoint</td><td>Shared/local/private fallback</td></tr>
<tr><td>First A2A turn</td><td>First A2A turn</td></tr>
<tr><td>First A2A turn</td><td>Production sends turn bearer</td></tr>
<tr><td>First A2A turn</td><td>Equality guard when nonempty</td></tr>
<tr><td>arrow-1</td><td>launch</td></tr>
<tr><td>arrow-2</td><td>bound</td></tr>
<tr><td>arrow-3</td><td>poll</td></tr>
<tr><td>arrow-4</td><td>reachable</td></tr>
<tr><td>arrow-5</td><td>setup</td></tr>
<tr><td>arrow-6</td><td>ready</td></tr>
<tr><td>arrow-7</td><td>invoke</td></tr>
<tr><td>note-0</td><td>Top, middle and bottom rows are successive launch stages.</td></tr>
<tr><td>note-1</td><td>Repository / preview / broker credentials have separate purposes.</td></tr>
<tr><td>note-2</td><td>Optional schema fields do not imply unconditional endpoint enforcement.</td></tr>
<tr><td>notes</td><td>Top, middle and bottom rows are successive launch stages.; Repository / preview / broker credentials have separate purposes.; Optional schema fields do not imply unconditional endpoint enforcement.</td></tr>
</tbody></table>
</details>
