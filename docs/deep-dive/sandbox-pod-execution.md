# Sandbox Pod Execution (pod-per-run) — Conceptual Deep Dive

## Why this exists: one fix for two problems

Before pod-per-run shipped, production agent subtasks ran **in-process inside the shared Worker
pod**. That pod held the live GitHub Copilot SDK session for each active run, the in-process
workflow runner, the streaming buffers, the tool/shell execution, and an in-memory history of
recent runs. Because all of it lived in one process, memory scaled with *concurrent +
recently-completed runs × (SDK session + graph + history)*, and the pod ran out of memory.

Sandbox pod execution starts from a single insight: **memory relief and security isolation are the
same fix**. Moving each run's agent execution out of the shared Worker process and into its own
per-run, Kata-isolated pod simultaneously:

1. **relieves the OOM** — the heavy SDK session, the runner, and tool execution leave the shared
   process, so no single process holds more than the runs it actively owns; and
2. **isolates execution** — each run's tool, shell, file, and model I/O happens inside its own VM-backed
   pod with a restricted egress allowlist, instead of sharing one blast radius.

After the move, the API/worker tier becomes a **thin orchestrator**: HTTP, SSE relay, database, and the
orchestration graph. The heavy, untrusted work runs elsewhere, per run, and dies with the run.

> This page explains the *logic* of pod-per-run. For the existing isolation model (filesystem
> containment, governance, executor selection, claim lifecycle, hardening) see
> [Sandbox](./sandbox.md); for the cluster topology see [Infrastructure](./infra-deployment.md). The
> exhaustive flag/identity/token reference is [Sandbox pods reference](../reference/sandbox-pods.md), and
> the operator/user-facing view is [Sandbox pod execution experience](../experience/sandbox-pod-execution.md).

## Before and after: where agent execution runs

### Before pod-per-run: single-Worker-pod execution

Before this rollout, the leaf agent — the object that wraps a live Copilot SDK session — was
created and driven **inside the Worker process**. The workflow graph ran in-process there too, and
the sandbox pod was used only to **exec one shell command at a time** through a warm-pool claim.
The pod was a place to run `run_command`; it was *not* where the agent lived.

![Before pod-per-run: single-Worker-pod execution: Workflow graph, Agent + live Copilot SDK session, In-memory run-event history, Sandbox pod, SSE to clients](../diagrams/sandbox-pod-execution-fig1.png)

<!-- Rendered from ../diagrams/src/sandbox-pod-execution-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

Every box inside the Worker pod multiplies by concurrent and recent runs. That is why it OOMed, and
why production had to keep subtasks on one shared in-process owner.

### Now: per-run sandbox pod

Under pod-per-run, the **leaf agent turn relocates into the pod**. The orchestration graph and its
human-in-the-loop (HITL) gates stay in the worker tier; only the agent *turn* — the part that holds the
SDK session and runs tools — moves out.

![Now: per-run sandbox pod: Orchestration graph, Remote agent proxy, In-pod AgentHost, Agent + live Copilot SDK session, tool / shell / file exec, Brokered checkpoint store](../diagrams/sandbox-pod-execution-fig2.png)

<!-- Rendered from ../diagrams/src/sandbox-pod-execution-fig2.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

The decisive architectural fact: **the coordinator's orchestration loop stays in the API/worker tier.
Only agent turns are sandboxed.** Remoting happens at the **AIAgent leaf seam** — the workflow graph,
the `RequestPort`/HITL/review logic, and the suspend/resume machinery never cross the wire. This keeps
all the state that decides *what happens next* in the durable, observable worker, and ships only the
expensive *doing* into the disposable pod.

> The worker↔pod transport is the **A2A bridge** (Agent Framework's Agent2Agent), used in message/stream
> mode. A2A ships in the .NET Agent Framework on a `-preview` package line and is therefore
> **experimental**; it is adopted deliberately, pinned by version, behind the same execution-mode flag
> that provides rollback. See [A2A bridge deep dive](./a2a-bridge.md), the
> [A2A reference](../reference/a2a.md), and the
> [A2A distributed agents experience](../experience/a2a-distributed-agents.md).

## The in-pod AgentHost

The pod runs a minimal host process — **AgentHost** — baked into the sandbox image. Conceptually it is
the pod-side counterpart of the leaf seam:

- it **receives the forwarded turn** (setup + run) from the worker;
- it **requires the run's bearer token** on `POST /a2a/agent/v1/message:stream`;
- it **hosts the real leaf agent** (the Copilot SDK session) directly, and runs the turn locally;
- it **executes tools, shell, and file operations in-pod**, inside the Kata VM boundary; and
- it **streams agent updates and token deltas back** to the worker, which re-injects them into the
  existing run-event stream so the SSE contract to clients is unchanged.

A key simplification: because remoting is at the leaf seam, AgentHost does **not** run its own workflow
graph. The graph lives only in the worker. AgentHost hosts one `AIAgent` and serves its turns. The
worktree commit/diff stays on the worker side, over the **shared workspace volume** both tiers mount, so
the database-write logic stays central and the pod stays stateless beyond the live turn.

With the Worker deployment now set to `Sandbox:AgentExecutionMode=pod-per-run`, the dedicated
AgentHost warm pool is on the live execution path: its standby pods are claimed, configured, and
serve real child-run turns instead of sitting idle.

The AgentHost claim path binds to `AgentHostWarmPoolRef` (default `agentweaver-agent-host`) and calls `/configure` after the warm pod is bound; it does not create an AgentHost-specific per-run `SandboxTemplate` or per-run warm pool. Source: `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:40`, `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:332`, `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:480`, `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:497`.

The existing per-command exec path is **retained for its current utility purpose** (ad-hoc
`run_command`); it is simply never the agent-turn transport. Nothing about pod-per-run deletes that
capability — see [Sandbox](./sandbox.md#kubernetes-sandbox-lifecycle-claims-over-pods).

## The executor seam: how commands are actually isolated

Pod-per-run answers *where the agent turn runs*. It does not, by itself, answer *how a single
`run_command` is isolated from the host* — that is the job of a second, older seam: the **executor
abstraction**. Both seams matter, and they are easy to conflate, so this section pins down the
relationship.

Whenever an agent runs a shell command, the runtime hands a uniform command object (command line,
working directory, environment, filesystem policy, timeout, network-enabled flag, run id) to an
**`ISandboxExecutor`**, and gets back a uniform result (exit code, stdout, stderr, timeout flag,
truncation flag). The executor decides *how and where* the process actually runs. That uniform contract
is what lets the same agent code run unchanged whether isolation comes from a Windows process container, a
Linux namespace sandbox, or a Kata-isolated Kubernetes pod. The contract itself is described in
[Sandbox › Executor abstraction](./sandbox.md#executor-abstraction-one-command-contract-many-isolation-backends).

### One executor per host, chosen at run start

`SandboxExecutorFactory` selects exactly **one** executor for the host at run start, walking a fixed
ladder and stopping at the first backend that is actually available. It emits a **`sandbox.selected`**
event carrying `backend`, `isRealIsolation`, and `reason`, so the chosen backend — and *why* it was
chosen — is observable for every run.

![One executor per host, chosen at run start: Run start:, In Kubernetes?, kubernetes-sandbox-claim, Windows?, processcontainer, wsl-bwrap / wsl-unshare, linux-bwrap, lxc-native-linux, direct, sandbox.selected:](../diagrams/sandbox-pod-execution-fig3.png)

<!-- Rendered from ../diagrams/src/sandbox-pod-execution-fig3.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

The ladder, top to bottom:

- **`processcontainer` (Mxc, Windows)** — the first choice on Windows. `mxc` is Microsoft's open-source
  sandbox isolation tool; its Windows `processcontainer` backend (BackendName `Mxc`) is driven by binaries
  in `MXC_BIN_DIR` (for example `wxc-exec.exe --probe` returns a `tier` such as `base-container`). Setup is
  in [Sandbox setup › Windows](../reference/sandbox-setup.md#windows-arm64).
- **`wsl-bwrap` / `wsl-unshare` (WslMxc, Windows)** — when `processcontainer` is unavailable but WSL2
  offers a usable Linux backend.
- **`linux-bwrap` (bubblewrap)** — the preferred Linux backend, a selective-mount namespace sandbox.
- **`lxc-native-linux`** — the Linux fallback when bubblewrap is absent but `lxc-exec` is present.
- **`kubernetes-sandbox-claim` (K8s)** — selected automatically in-cluster. Each command runs in a warm
  pod obtained through a `SandboxClaim` custom resource (`extensions.agents.x-k8s.io/v1beta1`), with Kata
  VM isolation and a NetworkPolicy egress allowlist. This is provisioned by the upstream **agent-sandbox
  controller**, a distinct runtime from MXC — see [The agent-sandbox controller (MXC vs. the
  controller)](#the-agent-sandbox-controller-mxc-vs-the-controller) below.
- **`direct` (Passthrough)** — the last resort, chosen **only when nothing else is available**. It runs
  the command directly on the host with **no isolation layer**; the shell still runs, relying on whatever
  isolation the surrounding deployment already provides.

`IsRealIsolation` is `true` for every real backend and `false` for `direct`. The governance gate enforces
a hard rule: a shell command is allowed only when the selected executor reports `IsRealIsolation == true`
**or** is the `direct` backend. Any *other* non-isolating executor **denies `run_command`** outright — so
an agent never silently runs a shell command under a half-isolated backend. The exact rows and selection
conditions live in the [Sandbox backends table](../reference/sandbox-setup.md#sandbox-backends).

### Two seams, one contract, three tiers

The connective idea for pod-per-run is that **the executor abstraction and pod-per-run agent execution are
the same seam at different deployment tiers — and the A2A agent-turn remoting is orthogonal to both.**
Three things are deliberately distinct:

- **The executor seam (`ISandboxExecutor`)** isolates an individual *command*. Local dev gets MXC
  (`processcontainer`) or bubblewrap; in-cluster gets the Kubernetes claim backend. Same command contract,
  different backend.
- **The Kubernetes claim backend** is the in-cluster *implementation* of that same executor contract:
  `KubernetesSandboxExecutor` runs each command in a warm pod obtained via a `SandboxClaim` CR, with Kata
  VM isolation and NetworkPolicy egress. There is no special second contract for the cluster — it is the
  same `ISandboxExecutor` seam, fulfilled by a pod instead of a local process.
- **A2A agent-turn remoting** (the [A2A bridge](./a2a-bridge.md)) moves an entire *agent turn* — the SDK
  session and its tool loop — out to the per-run pod. That is the pod-per-run story above. It is
  **orthogonal** to backend selection: the in-pod AgentHost still runs each `run_command` through *its
  own* `ISandboxExecutor`. When `AgentHost:SandboxMode=kata`, that executor is
  `PodExecSandboxClient`: it forwards every model-controlled command to the **executor sidecar
  container** of the same pod, where `KataBwrapExecutor` builds the per-run mount namespace. The Kata
  VM isolates the pod from the node, the sidecar container isolates model-controlled processes from
  AgentHost, and bubblewrap removes the shared PVC root from each shell/preview child process.

Read the whole isolation stack top-down: pod-per-run decides *which process hosts the agent turn* (worker
vs. per-run pod, via A2A); the executor abstraction decides *how each command inside that turn is
isolated* (MXC / bwrap / Kata-pod claim plus an in-pod mount namespace); and the governance gate decides *whether the command may run at
all* given the selected backend's `IsRealIsolation`. On a laptop these collapse onto one host — the agent
turn runs in-process and commands isolate via MXC or bubblewrap; in-cluster they fan out — the agent turn
runs in its own Kata VM and commands execute in a run-scoped mount view within that VM. The contract a reader has to remember is
single: *one command in, one uniform result out, isolation chosen per host and announced by
`sandbox.selected`.*

Every Kata AgentHost pod still mounts the shared RWX `/workspace` PVC because linked git worktrees
refer to git metadata outside the worktree directory and the generic warm pool cannot vary pod
mounts after adoption. The pod therefore applies the per-run policy at the **process/mount
boundary**, not by parsing shell syntax. Two containers share the pod:

- `agentweaver-agent-host` hosts the agent turn and the run's brokered GitHub token. It never
  executes a model-controlled command.
- `agentweaver-exec` (same image, `--exec-agent`) executes every model-controlled command inside
  `KataBwrapExecutor`'s per-run mount namespace. It is a real, runtime-created PID namespace with a
  runtime-provided procfs, holds no Kubernetes or AAD identity, and shares only the pod network
  namespace (so preview ports stay reachable) and the two workspace volumes.

Within that structure:

- only the current run worktree, its explicitly registered run HOME, authorized run-scoped scratch,
  pod-private tmp, and the exact linked
  worktree git metadata are bind-mounted; git control metadata is read-only, while the platform
  performs the durable commit after the turn;
- the PVC root and sibling worktrees are absent, so absolute paths, variable indirection, `..`,
  symlinks, and direct Python/.NET file APIs cannot resolve them;
- `/proc` is the executor container's own procfs, bound into the run's mount namespace. It contains
  only that container's processes, so CoreCLR process discovery works while AgentHost's process tree
  is not merely hidden but **absent from the namespace**;
- the child environment is cleared and rebuilt from a minimal baseline plus explicitly supplied
  values; and
- the sidecar runs a real bwrap capability probe **before it binds its socket** — if bubblewrap
  cannot build the mount namespace the daemon exits and the container never serves, so there is no
  window in which a command could run unisolated; and
- AgentHost runs a **one-shot startup probe** against that socket and refuses to start unless the
  sidecar answers, proving it is reachable, isolated (the probe re-runs the bwrap capability check
  server-side on every request), and in a *different* PID namespace than AgentHost. It is a startup
  gate, not a periodic health check: afterwards each individual command still fails closed with exit
  126 if the socket ever stops answering. There is no passthrough fallback in Kata mode.

#### Why a sidecar and not a nested PID namespace

The original design asked bubblewrap for `--unshare-pid --proc /proc`. That cannot work in **any**
Kubernetes container: the kernel's `mount_too_revealing()` check refuses a fresh procfs inside an
unprivileged user namespace whenever the visible procfs has masked or covered submounts, and every
container runtime — Kata's guest agent included — masks `/proc/kcore`, `/proc/keys`,
`/proc/timer_list`, `/proc/interrupts` and read-only-binds `/proc/bus`, `/proc/fs`, `/proc/irq`,
`/proc/sys`, `/proc/sysrq-trigger`. In production this surfaced as
`bwrap: Can't mount proc on /newroot/proc: Operation not permitted` and, because the isolation probe
is fail-closed, a 0/2 warm pool. CI never caught it because GitHub runners are VMs with an unmasked
`/proc`.

Every way to make the nested mount succeed weakens the boundary — `procMount: Unmasked`,
`CAP_SYS_ADMIN`, a privileged container, or a synthetic `/proc`. Dropping `--unshare-pid` while
keeping `--proc` is worse than useless: bubblewrap then silently binds the outer `/proc`, so the
sandbox *looks* isolated and is not. A second container gets the same guarantee for free, because the
runtime creates the PID namespace and the matching procfs itself — no capability, no privilege, no
host namespace. Bubblewrap keeps doing the per-run mount scoping it *can* still do, with
`--bind /proc /proc` and no PID claim it cannot back.

The AgentHost↔sidecar channel is a Unix domain socket on a pod-private `emptyDir` mounted into only
those two containers, never bound into a sandboxed child's mount namespace, and guarded by a 32-byte
token written mode-0600 next to it and compared in constant time. Long-lived preview processes are
supervised by a relay child of AgentHost that holds the connection: if the relay or AgentHost dies,
the sidecar sees the disconnect and terminates the sandboxed process group, preserving
die-with-parent semantics across the container boundary.

##### The IPC volume must be a guest-owned tmpfs (#1008)

That `emptyDir` carries `medium: Memory`. It was a default `emptyDir` from the sidecar's
introduction until **2026-08-27T17:41:48Z**, when the AKS `katapool` node image was upgraded
(`AKSAzureLinux-V3katagen2-202608.14.0`) and brought **Kata Containers 3.32.0**. Upstream had
flipped `disable_guest_empty_dir` from `false` to `true`, so a default `emptyDir` stopped being a
directory the guest agent creates and became a host directory re-exported over virtio-fs with a
**per-container** share path. Measured on the node:

```
shared_fs = "virtio-fs"
disable_guest_empty_dir = true
emptydir_mode = "shared-fs"
```

A pathname `AF_UNIX` socket is matched by inode identity in the connecting task's own kernel, so the
AgentHost container saw the socket file with `S_ISSOCK` set and got `ECONNREFUSED` on every
connect — and failed closed with `AgentHost Kata filesystem isolation is unavailable; refusing to
start`. Nothing in the application changed: the `v0.20.0..v0.21.2` diff of this template touches only
resource requests, limits, and comments.

`medium: Memory` restores the previous behaviour for this one volume. Kata takes the
`IsTmpFSEmptyDir` branch *before* and independently of `disable_guest_empty_dir` and
`emptydir_mode`, creating one guest tmpfs at `/run/kata-containers/sandbox/ephemeral/<volume>` and
bind-mounting it into every container of the sandbox — a single real inode, so the rendezvous works.
It also survives the knob's removal, which upstream has already done for the Rust runtime. Measured
on the same pod, same node: default medium `errno=111 Connection refused`; `medium: Memory`
`CONNECT-OK`.

The sidecar reads `/proc/self/mountinfo` at startup and refuses to bind on a shared filesystem, so a
future node-image change that reintroduces this names itself instead of crash-looping silently.

`ShellCommandValidator` and `SharedWorkspacePathGuard` remain compounding controls, but the security
claim for #476 no longer depends on command text.

#### Process lifetime without a nested PID namespace

Because the sandbox deliberately does not claim a PID namespace of its own, nothing implicitly reaps
what a command leaves behind, and two kernel details of the Kata guest matter:

- `/proc/<pid>/task/<pid>/children` does **not** exist there (`CONFIG_PROC_CHILDREN` is off), so
  neither the executor nor .NET's `Process.Kill(entireProcessTree: true)` may depend on it. The
  executor therefore discovers the sandboxed process by scanning `/proc/*/stat` for the single entry
  whose PPID is the bubblewrap process it just started, and reads that entry's process-group id from
  the same line. Because `--new-session` already made that child a session and process-group leader
  and the workload is exec'd directly (no extra `setsid` indirection), the pid it finds *is* the
  run's process-group id. The scan polls until bubblewrap has forked (bounded by a deadline) and
  fails closed if bubblewrap exits first or if the resolved group is the executor's own.
  Bubblewrap's `--info-fd` is deliberately **not** used: pointing it at fd 1 closes the workload's
  stdout (every write then fails with `EBADF`), and .NET's `ProcessStartInfo` cannot hand an
  arbitrary extra pipe to the child, so there is no spare descriptor to point it at instead.
- `--die-with-parent` only SIGKILLs bubblewrap's immediate child. Anything the command daemonised
  (a Roslyn build server, a watcher) would survive. So every command path — one-shot `exec` as well
  as supervised preview processes — ends by terminating that process group (`TERM`, then `KILL`),
  and the executor refuses to signal a group that is its own. For one-shot `exec` that reap happens
  **before** the executor drains stdout/stderr: a daemonised grandchild inherits the command's
  output pipes, so waiting for end-of-file first would stall the whole command until that leftover
  process happened to exit, and report a spurious timeout.

Commands in one pod share the executor container's PID namespace with each other. That is the
correct boundary rather than a gap: a sandbox pod is claimed by exactly one run, and the boundary
that carries credentials — AgentHost, its brokered GitHub token, and the pod's workload identity —
is a *different* container in a different PID namespace, which the sidecar re-verifies on every
probe.

##### Disclosure: intra-run argv visibility

One consequence of that shared PID namespace is worth stating explicitly rather than leaving to be
rediscovered. `/proc/<pid>/cmdline`, `/proc/<pid>/stat` and `/proc/<pid>/status` are world-readable
and are **not** gated by procfs' ptrace check, so a sandboxed command can read the *command line* of
other processes running in the same executor container — that is, other commands of the **same run**,
and the sidecar daemon's own argv. It cannot read their environment, memory, cwd or mount root:
`/proc/<pid>/environ`, `/proc/<pid>/mem`, `/proc/<pid>/cwd` and `/proc/<pid>/root` are gated by
`ptrace_may_access`, and every command runs in its own user namespace (`--unshare-user`), which is
neither an ancestor nor a descendant of the sidecar's or of a sibling command's, so those accesses
are refused.

The blast radius is therefore one run's own commands, and the contract that follows is: **secrets
belong in the environment (or a file inside the run's own mount view), never in a command line.**
That is how the runtime already passes the brokered token — the executor clears the child
environment and rebuilds it from explicitly supplied values, and those values are unreadable across
the user-namespace boundary. Cross-run and AgentHost-facing exposure is unaffected: sibling runs are
in different pods, and AgentHost is in a different PID namespace in this one.

### Contract with #481

#481 remains the pod-wide storage redesign: relocate authoritative worktrees off the API HOME tree,
empirically prove `volumeClaimTemplates` warm-pool behavior on the pinned controller, provision a
private volume/source snapshot for every run, define authenticated write-back/resume semantics, and
securely sanitize/delete the volume. It may then remove the shared PVC mount from the AgentHost pod
entirely. #476 does **not** create dynamic PVCs, templates, or pools and does not alter controller
adoption; it guarantees that model-triggerable shell and preview child processes cannot see the
shared root while preserving existing linked-worktree operation. Until #481 lands, linked worktrees
still receive read-only access to their repository's shared common git metadata, so commits and refs
already present in that same repository are not a per-run confidentiality boundary; other projects'
worktrees and git metadata remain absent.

## The agent-sandbox controller (MXC vs. the controller)

In-cluster, the sandbox pod a run executes in is not created by Agentweaver directly and is **not** MXC.
It is provisioned by the upstream **agent-sandbox controller** ([`kubernetes-sigs/agent-sandbox`](https://github.com/kubernetes-sigs/agent-sandbox)),
which Agentweaver installs in `scripts/azure/steps/10-create-cluster.mjs` (default `v0.5.3`). The naming overlap
trips people up, so pin it down:

- **MXC** = `Sabbour.Mxc.Sdk` / `wxc-exec.exe`, the **local-host** command-isolation runtime behind the
  `processcontainer`, WSL, and `lxc-exec` executors. It runs on a laptop or non-cluster host and has no
  controller and no CRDs.
- **agent-sandbox controller** = the **in-cluster** operator that turns a `SandboxClaim` into a bound,
  Kata-isolated pod. The in-cluster `ISandboxExecutor` (`KubernetesSandboxExecutor`) talks only to this
  controller's CRDs; there is no MXC binary in the cluster.

So "how is the sandbox implemented with MXC?" has two honest answers depending on tier: **locally**, a
command is isolated by MXC spawning a `wxc-exec.exe` sandbox process; **in-cluster**, a command (or a
whole agent turn) runs in a pod the agent-sandbox controller bound for the run. Both present the identical
`ISandboxExecutor` contract.

### How the controller provisions a run's pod

The three CRDs (API group `extensions.agents.x-k8s.io`; `KubernetesSandboxExecutor` targets the `v1beta1` storage version — [`SandboxClaimConventions.cs:23`](#source)) Agentweaver applies are:

- **`SandboxTemplate`** (`k8s/base/sandbox-template-agenthost.yaml`, `agentweaver-agent-host`) — the live AgentHost pod shape and hardening:
  `kata-vm-isolation` runtime class, non-root UID/GID 1000, dropped capabilities, `/workspace` PVC, and the A2A listener on container port `8088`.
- **`SandboxWarmPool`** — keeps AgentHost pods pre-built from a template so a claim binds without a cold start. The live pool is `agentweaver-agent-host` (`k8s/base/sandbox-warmpool-agenthost.yaml`, `replicas: 2`). It pre-warms the .NET process and Copilot SDK; per-run context arrives later via `/configure`.
- **`SandboxClaim`** — created per run by `KubernetesSandboxExecutor` with `spec.warmPoolRef.name`
  (the pool to bind), `spec.lifecycle.{ttlSecondsAfterFinished, shutdownPolicy: Delete}`, and
  `spec.env[]` for static values only on the AgentHost path (paths, port, and mTLS settings). `RunId`, `TurnBearerToken`, and the immutable `CopilotCredential` are delivered after binding by `POST /configure`. The controller adopts a warm pod and signals readiness via a `Ready` condition.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    participant Exec as KubernetesSandboxExecutor
    participant Claim as SandboxClaim (CR)
    participant Ctrl as agent-sandbox controller
    participant Pool as SandboxWarmPool
    participant Pod as Kata sandbox pod
    participant Reg as PodNameRegistry
    Exec->>Claim: CreateClaimAsync(warmPoolRef, lifecycle)
    Claim->>Ctrl: reconcile new claim
    Ctrl->>Pool: adopt a warm pod
    Pool-->>Pod: pod assigned to claim
    Ctrl-->>Claim: Ready condition = True,<br/>status.sandbox.name = pod
    loop poll every 2s
        Exec->>Claim: WaitForBoundAsync (read status)
    end
    Claim-->>Exec: Ready True → pod name
    Exec->>Reg: Register(runId, podName)
    opt AgentHost pod-per-run
        Exec->>Exec: Generate 256-bit turn bearer token
        Exec->>Reg: RegisterTurnToken(runId, token)
        Exec->>Pod: GetPodIpAsync → status.podIP
        Exec->>Pod: POST /configure(runId, token, copilotCredential, workingDirectory)
        Pod->>Pod: TryConfigure once + SetupAsync in workingDirectory
        loop poll /healthz until 200 (≤90s)
            Exec->>Pod: GET http[s]://podIP:8088/healthz
        end
        Exec->>Reg: RegisterAgentEndpoint(http[s]://podIP:8088/a2a/agent)
    end
    Note over Exec,Ctrl: claim delete (ad-hoc) or TTL →<br/>controller GCs pod + service
```

The executor reads the pod name from `status.sandbox.name` (the agent-sandbox controller's shape) once the
claim's `Ready` condition is `True`. For pod-per-run AgentHost pods it then polls the pod's `status.podIP` to
build the A2A endpoint. Agentweaver never deletes pods itself — it deletes the *claim* (or lets the TTL
expire) and the controller garbage-collects the pod and its service. Anything not visible in these
manifests or the executor code (controller replica count, leader election, image, RBAC of the controller
itself) is **operationally configured by the agent-sandbox release**, not specified by Agentweaver.

### Transient Kubernetes API resilience

Pod-claim creation and the bind/IP polls that follow it are guarded against transient Kubernetes API faults (issue #230). A single mid-flight connection reset during `CreateClaimAsync` used to fail the subtask's first agent turn outright, cascading into `assembly_blocked` for every dependent subtask. The executor now wraps the **idempotent** k8s calls — the mutating claim create plus the read-only `WaitForBoundAsync` and `GetPodIpAsync` polls — in a bounded retry (`ExecuteK8sWithRetryAsync<T>`, `MaxK8sAttempts = 3` total tries) with exponential backoff (~250 ms · 2^(n−1), capped ~2 s) plus 0–250 ms jitter to de-sync concurrent launches retrying the same API server after a blip.

`IsTransientK8sFault` decides what is worth retrying:

- **Retried** — a socket/IO connection reset (`SocketException 104` → `IOException` → `HttpRequestException`, directly or nested in an inner exception), a `429` or `5xx` from the API server, and an `HttpClient` timeout (`OperationCanceledException`/`TaskCanceledException` with no caller cancellation).
- **Never retried** — caller cancellation short-circuits to `false`, so a genuine cancel aborts immediately (the backoff `Task.Delay` also honors the token).
- **`409 Conflict` is not a transient fault.** It is handled attempt-awarely to preserve idempotency: a first-attempt `409` is a genuinely pre-existing claim owned by an earlier launch (wait for it); a `409` on a **retry** means our own create committed server-side before a reset hid the response, so the claim is treated as created and configured — never reused un-configured and token-less.

The retry wrapper must only wrap idempotent calls: the non-idempotent AgentHost `POST /configure` stays outside it, because a second delivery hard-fails `409`.

### Warm-pool configure and readiness gate (AgentHost)

A bound claim means the controller assigned a pod; it does **not** mean the run-specific AgentHost
is ready to serve turns. AgentHost warm pods start with no `RunId`, enter standby, and log that
they are waiting for `/configure`. This lets
`k8s/base/sandbox-warmpool-agenthost.yaml` run at `replicas: 2` without CrashLooping: the .NET process
and Copilot SDK host are already warm, but no run context is required until a claim binds. With the
Worker now in `pod-per-run`, those two standby pods are the hot path for coordinator child turns.

At run launch, `KubernetesSandboxExecutor` generates a 256-bit turn bearer token, resolves the shared orchestration worktree, and reads `AutoApproveTools` from `IRunOptionsStore`. It calls `POST {scheme}://{podIP}:8088/configure` with run identity, workspace descriptors, approval settings, and provider data. The provider data is `copilotCredential` or `byokProviderConfiguration`; `copilotCredential` is required only without BYOK. Repository, preview, and MCP broker credentials are optional and purpose-scoped. `/configure` is one-time, excluded from the readiness gate, and not protected by the turn token because it delivers that token. The NetworkPolicy limiting AgentHost ingress to API and worker pods is the guard.

After `/configure`, `AgentHostStartupService.ConfigureAsync` runs `SetupAsync` with that per-run working directory overriding the static `AgentHost__WorkingDirectory` env default; only then does `/healthz` return `200` and the executor registers the A2A endpoint. This establishes the invariant `SetupAsync` working directory == `Run.WorktreePath` == the path named in the run's system prompt, so files written by one sibling agent are visible to later synthesis or assembly stages. If working-directory resolution fails, launch continues and the pod falls back to the env default. The wait is bounded (default `90 s`, `1 s` interval, `5 s` per-attempt timeout) and honors the launch cancellation token. The `a2a-sandbox-pod` client still carries the connection-refused retry handler as defense-in-depth, but the normal path is: **claim warm pod → configure → health ready → first turn**.

#### Pod-local execution workspaces

`PodLocalWorkspaceManager` is the materialization and publication seam for work that should not run
directly on Azure Files SMB. `ExecutionWorkspaceMode` separates location from mutation policy:

- `Shared` runs against the existing PVC-backed worktree.
- `LocalReadOnly` creates a verified pod-local checkout and refuses publication.
- `LocalWritable` creates the same verified checkout for an implementation turn, then publishes its
  resulting commit back to the authoritative repository.

The API-visible worktree remains on the shared `/workspace` PVC for orchestration, review, and durable
branch state. Dependency installation, compilation, tests, preview artifacts, and implementation edits
happen in a **complete checkout** under `/local-workspace/{run-hash}/{tree-hash}`. That root is an
8 GiB, disk-backed `emptyDir`: it is local to the claimed pod, is not synchronized to Azure Files, and
disappears when the pod is released. The shared repository receives changes only through the explicit
write-back path described below.

![Pod-local execution workspaces: Authoritative repository + worktree, PodLocalWorkspaceManager, Ephemeral checkout, Workspace mode, Build / test / preview, Implementation turn, Cancellable nested-repo scan, Flatten nested repos, Platform alternate index](../diagrams/sandbox-pod-execution-fig4.png)

<!-- Rendered from ../diagrams/src/sandbox-pod-execution-fig4.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

##### Materialize and verify before execution

The `/configure` payload carries the shared/source coordinates rather than pretending that a
pod-local directory already exists: `sourceRepositoryPath`, `sourceRef`, `baseCommitSha`,
`expectedTreeHash`, and `scratchRoot`. AgentHost derives the scratch path, checks free ephemeral
capacity, initializes a repository, shallow-fetches the requested ref, and verifies both the fetched
commit and its tree before checking out the immutable base detached. A mismatch is fatal; execution
never proceeds on a nearby revision.

Assembly Build/Test selects `LocalReadOnly`. Implementation child turns select `LocalWritable` and
must fetch their authoritative `agentweaver/{childRunId}` branch. Both modes therefore execute away
from SMB while retaining a precise coordinate back to the durable branch.

##### Write-back is ordinary Git, not a custom transport

At the end of a successful writable turn, AgentHost prepares a platform-owned alternate index from
the immutable base, stages the final filesystem state, writes a tree, and creates a single-parent
commit with the configured platform author identity. It then invokes Git directly with the literal
shape:

```text
git push --no-force origin {resultCommit}:{writebackRef}
```

There is no bespoke file-copy protocol, patch transport, or force push. The command inherits the
pod's existing Git environment and credential context; write-back does not mint, serialize, or deliver
a second credential. In the current pod-local flow, `origin` is the shared source repository path
delivered in `/configure`, so this publication is a normal Git repository-to-repository push rather
than a custom network transport. The run's existing GitHub token remains available through the
AgentHost token store for GitHub operations; the write-back path does not create a parallel token
mechanism.

The pushed ref is a unique temporary ref under the Agentweaver write-back namespace. The API side
then validates the descriptor, commit parent, tree, and authoritative branch state before applying a
fast-forward. If the local filesystem produced no tree change, AgentHost returns the immutable base
and does not push a temporary ref.

##### Nested repositories are flattened into content

A nested checkout or submodule cannot be staged naively. Git normally records it in the parent tree
as mode `160000` — a **gitlink** pointing at another commit — rather than recording the nested files.
That pointer is unusable when the nested repository is only ephemeral pod state or when the receiving
branch must contain the generated content.

Before writing the result tree, AgentHost discovers nested `.git` directories and `.git` files,
deepest first. It temporarily moves their metadata outside the workspace, removes any cached gitlink,
stages the nested directory as ordinary files, and restores the metadata even on failure. A final
`git ls-tree` check rejects any remaining mode-`160000` entries.

The discovery walk is deliberately bounded operationally:

- it checks cancellation while popping and enumerating directories;
- it prunes `.git`, `.next`, `bin`, `build`, `dist`, `node_modules`, and `obj`
  unless that directory is itself a nested repository root;
- it does not traverse reparse points; and
- filesystem access failures become a typed `writeback_invalid` failure instead of silently producing
  an incomplete tree.

This keeps write-back responsive on dependency-heavy workspaces while still detecting a repository
that an implementation turn intentionally created inside an otherwise ignored path.

##### One HOME/XDG cache contract for every toolchain

Every non-operator AgentHost run gets a HOME outside the checkout at
`<execution-scratch>/runtime-home/<run-hash>`. After resolving the final Shared or pod-local
working directory, `PodLocalWorkspaceManager` creates the HOME and its XDG children, then
registers that exact path through `IRunWorkspaceRegistrar` — in Kata mode that registration travels
over the executor socket and is applied by the sidecar's `KataBwrapExecutor`:

| Variable | Registered value |
| --- | --- |
| `HOME` | `<runtime-home>` |
| `XDG_CACHE_HOME` | `<runtime-home>/.cache` |
| `XDG_DATA_HOME` | `<runtime-home>/.local/share` |
| `XDG_CONFIG_HOME` | `<runtime-home>/.config` |

This tech-agnostic contract replaced the former matrix of npm-, Yarn-, and pnpm-specific variables.
Tools that follow HOME/XDG conventions now place caches and state on the same fast scratch disk
without every new ecosystem requiring another environment-variable exception. Because the runtime
HOME is outside the checkout, it cannot enter write-back. Kata shell and preview children fail closed
until the workspace and runtime HOME are both registered, bind only the registered HOME read-write,
and rebuild HOME/XDG from that immutable registration. Inherited or command-supplied HOME/XDG values
cannot select another mount or override the registered values.

Preview command discovery still reads the API-visible tree, but `PreviewStep` maps the resolved
relative cwd into the verified local checkout. The preview therefore sees the exact dependencies and
build artifacts produced by the gate without copying those artifacts to Azure Files.

### Node topology: the dedicated kata user pool

Sandbox/AgentHost pods require **Kata VM isolation**, which on AKS is the `workloadRuntime:
KataVmIsolation` node-pool property (nodes get the `kubernetes.azure.com/kata-vm-isolation=true`
label and a Kata-capable gen2 image). The cluster uses **cluster-autoscaler** (not NAP):

- **NAP and cluster-autoscaler are mutually exclusive.** This cluster uses cluster-autoscaler
  on all three pools; `--node-provisioning-mode Auto` is not set.
- **Kata capacity lives in a fixed user pool.** `katapool` is created with
  `--workload-runtime KataVmIsolation --enable-cluster-autoscaler --min-count 1 --max-count 5`
  so it scales automatically under load without requiring manual resize.
- **System pool is reserved.** `nodepool1` carries `CriticalAddonsOnly=true:NoSchedule`; only
  kube-system / critical-addon pods land there. App workloads go to `apppool` (no taint,
  no tolerations required in app deployment YAMLs).

The three-pool layout:

```bash
# apppool — app workloads (api, worker, mcp, frontend, jobs); no taint
az aks nodepool add \
  --resource-group "${RESOURCE_GROUP}" --cluster-name "${CLUSTER_NAME}" \
  --name apppool --mode User --os-sku AzureLinux \
  --node-vm-size Standard_D4s_v6 \
  --enable-cluster-autoscaler --min-count 1 --max-count 5 \
  --ssh-access disabled

# katapool — sandbox/AgentHost pods; taint keeps non-sandbox pods out
az aks nodepool add \
  --resource-group "${RESOURCE_GROUP}" --cluster-name "${CLUSTER_NAME}" \
  --name katapool --mode User --os-sku AzureLinux \
  --workload-runtime KataVmIsolation --node-vm-size Standard_D4s_v6 \
  --enable-cluster-autoscaler --min-count 1 --max-count 5 \
  --node-taints sandbox=kata:NoSchedule --labels agentweaver.io/kata=true \
  --ssh-access disabled
```

| Pool        | Mode   | workloadRuntime | Autoscaler  | Taint                             | Label                       | Receives                       |
|-------------|--------|-----------------|-------------|-----------------------------------|-----------------------------|-------------------------------|
| `nodepool1` | System | *(standard)*    | 1–3 nodes   | `CriticalAddonsOnly=true:NoSchedule` | —                        | kube-system / critical addons  |
| `apppool`   | User   | *(standard)*    | 1–5 nodes   | *(none)*                          | —                           | api, worker, mcp, frontend, jobs |
| `katapool`  | User   | KataVmIsolation | 1–5 nodes   | `sandbox=kata:NoSchedule`         | `agentweaver.io/kata=true`  | Sandbox / AgentHost pods       |

The Kata `SandboxTemplate` pod spec (`k8s/base/sandbox-template-agenthost.yaml`) wires pods to `katapool` — the CRD `podTemplate.spec` is a full PodSpec,
so `tolerations`/`affinity` pass straight through to the rendered pod:

- a **toleration** for `sandbox=kata:NoSchedule` admits pods onto the tainted `katapool`; and
- a **preferred** (not required) `nodeAffinity` for `agentweaver.io/kata=true` *biases* pods onto
  `katapool`; cluster-autoscaler scales `katapool` when demand grows — pods are **never stranded**.

The `CriticalAddonsOnly` taint lives only on `nodepool1`; app workloads schedule onto `apppool`
without any toleration changes.

## The hybrid pod-granularity model

How long should a run hold a pod? Two naive answers both fail:

- **Pod-per-turn** (claim a pod for each turn, release between turns) pays a warm-pool claim, an agent
  setup, and a session deserialize *on every turn*. That is unaffordable latency and token cost, and it
  risks session round-trip drift.
- **Pod-per-run, held continuously** (one pod for the whole run, never released) keeps a pod — with its
  live SDK session — alive through unbounded human-review waits and through a coordinator's long idle
  life while it awaits child runs. That recreates a softer, *distributed* OOM and wastes capacity.

Agentweaver therefore uses a **hybrid**: pod-per-run **with checkpoint-and-release on suspend**.

```mermaid
stateDiagram-v2
    [*] --> Claiming
    Claiming --> Standby: warm pod bound
    Standby --> Warm: /configure + SetupAsync<br/>session live
    Warm --> Warm: consecutive agent turns<br/>(reasoning burst stays warm)
    Warm --> Released: graph suspends on RequestPort<br/>(HITL/review) or coordinator idles
    Released --> Reclaiming: resume signal<br/>(decision / child completion)
    Reclaiming --> Standby: re-claim warm pod
    Standby --> Warm: /configure + rehydrate from checkpoint
    Warm --> [*]: run completes → release pod
    Released --> [*]: run cancelled / TTL
```

The rules of the model:

- **A pod is warm for an active reasoning burst.** One pod, one live session, serves all the
  *consecutive* agent turns of an active burst. Inter-turn boundaries do **not** release the pod —
  releasing and re-setting up between every turn is exactly the cost pod-per-turn would pay.
- **The pod is checkpoint-and-released when the graph *suspends on an external gate*.** The release
  boundary is graph suspension on a `RequestPort` — a HITL/review gate, or the coordinator loop idling
  while it awaits child runs — **not** a mere inter-turn boundary. While a human is deciding, or while
  the coordinator waits on children, there is nothing for the SDK session to do, so the pod is released
  back to the warm pool.
- **Resume re-claims a warm pod and rehydrates.** On the resume signal (a HITL decision arrives, or a
  child run completes), the worker re-claims a warm pod and rehydrates the run from the brokered
  checkpoint.

For this to be correct, the checkpoint must carry enough to perfectly reconstruct the suspended run: the
**serialized agent session blob** plus the **workflow superstep state**, including the correlation id of
the suspended external request. Two facts make rehydration cheap and safe:

- the **worktree is already durable** on the shared workspace volume, so no file state needs to travel in
  the checkpoint; and
- the **run-scoped context is re-delivered at re-claim via `/configure`**, so a resumed pod gets fresh credentials and a turn token rather than inheriting stale state (see the credential model below).

A tuning sub-flag, `Sandbox:ReleasePodOnSuspend` (default **true**), disables the release for
low-latency-resume or debugging — the pod then stays warm across a suspension at the cost of holding
capacity. The release is **internal behavior of `pod-per-run`**; it does not change the execution-mode
flag value.

## Credential model

The API brokers purpose-bound capability data to each pod through `/configure`. The model-provider payload is `copilotCredential` or `byokProviderConfiguration`. Repository, preview, and MCP broker credentials are separate optional fields. Each value is run-scoped and is never baked into the image.

The sandbox identity cannot retrieve Key Vault secrets or ambient user credentials. There is no per-user token CSI mount or shared token store. See [AgentHost capability credential delivery](./agent-token-delivery.md).

The A2A turn path has its own run-scoped secret. At AgentHost launch, `KubernetesSandboxExecutor`
generates a 256-bit bearer token, sends it in `POST /configure`, and registers it in
`IAgentHostTurnTokenRegistry`. `RemoteAgentProxy` sends that value as `Authorization: Bearer ...` on
every `message:stream` call, and AgentHost accepts only its own token. This means NetworkPolicy/mTLS are
not the only gates on the turn endpoint, and a token stolen from one run cannot reach another run's pod.

Egress is **default-deny** with a narrow allowlist: the model endpoint, the API/worker bridge endpoint,
and the git remote(s) the run legitimately needs. Everything else — especially arbitrary in-cluster
services and the database — is denied. Sandbox pods talk to the worker tier, never directly to the
database.

The pod-root control endpoints use the separately minted per-run preview-runner credential. It is
delivered only in the `/configure` body, stored in `AgentHostRuntimeState`, and persisted under the
replica-safe key returned by `PreviewRunnerCredential.SecretKey(runId)`. The API re-fetches this
credential when it must call back into the pod for preview control or tool-approval resolution.

## Returning tool-approval decisions to AgentHost

Pod-per-run moves the approval wait into AgentHost: its in-memory `IToolApprovalGate` owns the pending
request while the public approval endpoint runs in the API process with a `DurableToolApprovalGate`.
Issue #196 closed the missing API-to-pod return leg.

When an operator posts either the child run id or its coordinator run id, the API resolves the owning
child. If the durable gate reports `Unknown` and `Sandbox:AgentExecutionMode` is `pod-per-run`, the API
resolves the pod origin, loads the per-run credential, and forwards the decision over the existing
`a2a-sandbox-pod` HTTP client. AgentHost authenticates the request and resolves its local gate. A
terminal result is mapped to HTTP 200 and the API emits `tool.approval_resolved`; `unknown`, `pending`,
and unreachable results map to 404, 409, and 503 respectively.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    participant User as Operator
    participant API as Run approval endpoint
    participant Events as Persisted run events
    participant Durable as DurableToolApprovalGate
    participant Client as AgentHostApprovalHttpClient
    participant Host as AgentHost pod
    participant Local as In-memory IToolApprovalGate
    User->>API: POST coordinator or child run decision
    API->>Durable: resolve posted run + request
    opt coordinator does not own request
        API->>Events: find coordinator.child_approval_required
        Events-->>API: owning childRunId
    end
    API->>Durable: grant/deny owning child
    alt durable gate resolves
        Durable-->>API: terminal state
    else Unknown and pod-per-run
        API->>Client: childRunId + per-run bearer
        Client->>Host: POST /tool-approvals or /tool-denials
        Host->>Local: grant/deny request
        Local-->>Host: approved / denied / expired
        Host-->>Client: terminal response
        Client-->>API: resolved state
        API->>API: emit tool.approval_resolved
    end
    API-->>User: 200 terminal result
```

| Source | Role |
| --- | --- |
| `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:43-98` | Resolves a coordinator post to the owning child, including persisted approval-required events. |
| `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1559-1705` | Tries the durable gate, invokes the pod fallback, and exposes the public status contract. |
| `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:2590-2718` | Loads the per-run credential, maps pod outcomes, and emits `tool.approval_resolved`. |
| `apps/Agentweaver.Api/Sandbox/AgentHostApprovalHttpClient.cs:28-112` | Resolves the pod origin and sends authenticated decisions through `a2a-sandbox-pod`. |
| `apps/Agentweaver.AgentHost/Program.cs:287-288,486-588` | Hosts and authenticates the pod-local approval routes and resolves the in-memory gate. |

## Reaching into the pod: browser preview

Default-deny egress governs traffic *out* of the pod. A separate, deliberate path lets an operator (or a
running agent) reach *into* a run's sandbox pod: the **sandbox browser preview**. When an agent starts a
server inside its sandbox (a dev server, a built app, a debug endpoint), the run's pod can expose that port
back through the API on demand, so a human can open a live preview scoped to exactly that run's pod.

In **AKS deployments** (where `Sandbox:Preview:Enabled=true`) this is a **Gateway-direct reverse proxy**: the
API creates a per-preview `ClusterIP Service` + `HTTPRoute` that attaches to the shared
`agentweaver-preview-gateway` and routes `{token}-preview.{ZoneSuffix}` directly to the sandbox pod. The
response includes a public `preview_url`; no loopback port or `kubectl` process is involved. The agent or the
operator UI can also initiate this via the `start_preview` MCP tool, which first routes through a
human-in-the-loop approval gate.

In **local dev** (where `Sandbox:Preview:Enabled=false`) the fallback path still exists: the
implementation (`PortForwardService`) shells out to
`kubectl port-forward --address 127.0.0.1 pod/{podName} :{targetPort} -n {namespace}`, parses the chosen
loopback port from kubectl's `Forwarding from 127.0.0.1:<port> ->` line, and probes TCP until ready. It
returns a **`local_port` on the API host — not a public URL**; the `preview_url`/`previewUrl` fields are not
populated and the UI says so honestly when no proxied URL is returned. Sessions are tracked in memory, capped
at 3 per run and 20 globally, and cleaned up explicitly.

Neither path widens the pod's own egress allowlist; both are inbound tunnels the operator/agent opens, not
capabilities the sandboxed code can grant itself. The AKS NetworkPolicy
`sandbox-allow-preview-ingress` (`k8s/base/networkpolicy-sandbox.yaml`) admits TCP 3000–9000 exclusively from
`agentweaver-preview-gateway` pods — no other source can reach those ports.

> **Dedicated pages:** the browser preview has its own first-class docs —
> [Deep Dive](./sandbox-browser-preview.md), [Reference](../reference/sandbox-browser-preview.md), and
> [User Guide](../experience/sandbox-browser-preview.md).
> For the AKS-specific setup see [Sandbox browser preview — Deploy to AKS](../guide/deployment-aks.md#sandbox-browser-preview).

## The execution-mode flag and rollback

Everything above is gated behind a single flag so the change is reversible at any moment:

`Sandbox:AgentExecutionMode` ∈ { **`in-api`**, **`pod-per-run`** }.

- **`pod-per-run`** is now the production Worker setting. It activates the bridge and the per-run
  AgentHost pod, with the hybrid release behavior tuned by `Sandbox:ReleasePodOnSuspend` (default
  `true`).
- **`in-api`** remains available as the **fallback / rollback path**. If pod-per-run misbehaves —
  including any instability in the `-preview` A2A transport — flipping back to `in-api` restores
  the old in-process execution model without deploying a second transport.

This "default to today's behavior, flip per environment, roll back by flag" discipline is the same
posture used across the distributed-execution rollout. Pod-per-run is the first, independently shippable
phase — it stops the OOM on its own, before the later data-store and web/worker-split phases. See
[Distributed execution & scaling](./infra-deployment.md) for the surrounding phasing.

## Rebuild blueprint

To rebuild pod-per-run from these ideas:

1. **Keep the orchestration graph and HITL gates in the worker.** Relocate only the leaf agent turn.
   Remote at the `AIAgent` seam so the graph never crosses the wire.
2. **Introduce a remote leaf proxy** on the worker that forwards setup/run to the pod and re-emits the
   pod's update stream locally, so the rest of the graph and the SSE relay are unchanged.
3. **Bake a minimal AgentHost** into the sandbox image that hosts the real leaf agent, requires the
   per-run bearer on `message:stream`, and runs tools in-pod; stream updates back to the worker.
4. **Make checkpoints brokered/durable** so any worker (and a re-claimed pod) can read them — the
   serialized session blob plus superstep state, including the suspended external-request correlation id.
5. **Implement the hybrid lifecycle:** warm across consecutive turns; checkpoint-and-release on
   `RequestPort`/coordinator-idle suspension; re-claim + rehydrate on resume. Gate the release with
   `Sandbox:ReleasePodOnSuspend`.
6. **Give the pod run-scoped context** via one-time `/configure`: RunId, UserId, the A2A turn bearer token, and the Key Vault user-secret name. Fetch the user token with workload identity and no broker.
7. **Default deny egress** to model + worker + git only; never let the pod reach the database.
8. **Gate the whole thing behind `Sandbox:AgentExecutionMode`** so production can run `pod-per-run`
   while retaining `in-api` as an instant rollback path.

## Capability contract: what a run can actually do

An agent that discovers halfway through a task that the sandbox cannot perform an operation is
indistinguishable from a bug. The executor therefore publishes a **capability contract** — a
`capabilities` op on the pod-private executor protocol
(`packages/Agentweaver.SandboxExec/SandboxCapabilities.cs`) — that states, for every developer
workload we support, whether it works here, why not if it doesn't, and what would change that. An
operation the platform genuinely cannot perform is declared explicitly; it is never silently
omitted from the list, and never reported as supported.

| Capability | State on the Linux Kata executor | Why |
| --- | --- | --- |
| `npm_install` | **Supported** | Installs into the run's own workspace over the sandbox's egress allowlist; no system-root write is needed. |
| `nuget_restore` | **Supported** | `dotnet restore`/`run` write to the run's workspace and per-run NuGet cache. |
| `preview_port_binding` | **Supported** | The app binds loopback inside the sandbox; AgentHost forwards it to the preview Gateway, because both containers of the run's pod share one network namespace. |
| `apt_install` | **Supported** (via the per-run writable system root, below) | `apt-get install` needs a writable `/usr`, `/var` and `/etc`; the run gets a private overlay of them. |
| `image_build` | **RequiresExternalService** until a builder sidecar is present | BuildKit cannot run in the sandbox container at all — see below. An opt-in builder sidecar in the run's own Kata VM provides it; availability is probed from the socket, not assumed. |
| `winget_install` | **UnsupportedOnPlatform** | winget is Windows-only; see "winget and the Windows executor". |

`Unavailable` and `RequiresExternalService` mean "a deployment change would fix this".
`UnsupportedOnPlatform` means "no configuration of this executor will ever do it — move the work to
another executor". Callers branch on that distinction; both carry a remediation string.

`npm_install`, `nuget_restore` and `apt_install` all depend on the sandbox reaching public package
registries, so it is worth being precise about what egress is *actually* enforced today rather than
what the surrounding sections describe. Kubernetes NetworkPolicies are additive, and the
controller-generated per-template policy permits all ports to public CIDRs; unioned with the narrow
base policy, the effective rule is broader than the "HTTPS only" description implies, and the Azure
IMDS address is reachable from a sandbox run. That is a confidentiality gap rather than an isolation
break — the run still has no host namespaces, no hostPath, no service-account token, and no
cross-run workspace access — and it is **pre-existing**, tracked in issue #759 with the measurement
that found it. It is recorded here because a reader comparing this document against the cluster
should not have to discover the discrepancy themselves.

### The per-run writable system root

Package managers that install into the system root cannot work against a read-only image, so each
run that needs one gets a **private, disposable system root**:

- `apps/Agentweaver.AgentHost/sandbox/awx-run-root` creates an unprivileged **user + mount
  namespace**, mounts a size-bounded **tmpfs**, layers `/usr` and `/var` as **overlays** whose upper
  directories live on that tmpfs, and copies `/etc` onto it. It then holds those namespaces open for
  the lifetime of the run.
- The executor re-enters them per command with `nsenter --target <pid> --user --mount
  --preserve-credentials`, and runs the *same* bubblewrap command line inside — same namespace
  flags, `--cap-drop ALL`, `--die-with-parent`, same mount plan. The only difference is that `/usr`,
  `/etc` and `/var` are bound from the run's private overlay instead of read-only from the image.

Properties that make this safe:

- **No pod-level privilege is added.** The pod spec is unchanged: non-root, `allowPrivilegeEscalation:
  false`, all capabilities dropped, `RuntimeDefault` seccomp, no host namespaces, no hostPath.
  "Root" inside the run's user namespace is the sandbox's own unprivileged uid everywhere else.
- **Nothing escapes the run.** The upper layer is a tmpfs private to one user namespace: another
  run, the AgentHost container, and the node never see what was installed.
- **Nothing persists.** When the holder exits, the namespaces and the tmpfs die with it, so a run
  cannot leave anything behind in the image or on the node. Installed packages last for the run,
  not beyond it.
- **Failure is strictly more restrictive.** If the helper is missing or any mount fails, the
  executor logs it, reports `apt_install` as `Unavailable`, and runs the command against the
  read-only image system root exactly as before. There is no path where a failure grants more.
- **It is RAM.** The overlay upper must be tmpfs — under Kata every persistent pod volume is
  virtiofs, which cannot back an overlay upper — so the writable system root counts against the
  container's memory limit. It defaults to 1 GiB
  (`AGENTWEAVER_EXEC_WRITABLE_ROOT_SIZE`) and can be disabled entirely with
  `AGENTWEAVER_EXEC_WRITABLE_ROOT=0`.

One image requirement follows from the kernel, and is worth stating because it looks surprising in a
diff: **directories under `/usr` and `/var` are owned by uid 1000 in the image**. The run's user
namespace can map exactly one id (mapping a *range* needs an effective `CAP_SETUID`, which a
`runAsNonRoot` + `allowPrivilegeEscalation=false` container cannot hold), so root-owned directories
are unmapped inside it — and overlayfs must copy a directory up before anything can be created in
it, which fails with `EOVERFLOW` when the directory's owner has no representation in the namespace.
This grants model-controlled code nothing: it never sees the image's `/usr` and `/var` directly,
only the read-only bind or the per-run overlay.

A second kernel limitation needs a small apt hook. dpkg installs a package's directories by
extracting each under a temporary `.dpkg-new` name and renaming it into place; renaming a directory
across overlay layers requires overlayfs's `redirect_dir` feature, and the kernel refuses
`redirect_dir=on` for a mount created in a non-initial user namespace. Without help, `apt-get
install` downloads and unpacks correctly and then fails with `Invalid cross-device link`. dpkg only
performs that rename when the directory does not already exist, so
`apps/Agentweaver.AgentHost/sandbox/awx-apt-predirs` — registered as apt's `DPkg::Pre-Install-Pkgs`
hook — pre-creates the directories of the packages apt is about to install, which is an ordinary
`mkdir` in the overlay's upper layer. It creates directories and nothing else.

### Image builds need a separate builder, in this pod's own VM

Building container images **in the sandbox container** is not a policy choice we can relax, and
every step below was measured on the live cluster rather than assumed:

1. `buildkitd` must create a cgroup and perform mounts for every build step, which needs
   `CAP_SYS_ADMIN` and `CAP_NET_ADMIN`. The sandbox container holds **no** capabilities
   (measured `CapEff: 0000000000000000`).
2. `CAP_SYS_ADMIN` is **rejected by PodSecurity `baseline`**, which the `agentweaver` namespace
   enforces. Measured verbatim:
   `pods "…" is forbidden: violates PodSecurity "baseline:latest": non-default capabilities
   (container "…" must not include "NET_ADMIN", "SYS_ADMIN" in securityContext.capabilities.add)`.
3. Rootless `buildkitd` is not an escape hatch: it needs a sub-UID *range*, and mapping a range
   requires `newuidmap` to carry file capabilities. The Kata guest filesystem cannot store
   `security.capability` xattrs at all — `setcap` returns **`Not supported`** — so `newuidmap`
   fails with "Could not set caps". `rootlesskit` has no single-id fallback either; it exits with
   `No subuid ranges found`.

So the builder is a **separate container**. The question is *where*.

#### Why not a shared broker

The first implementation was one shared BuildKit broker reached over mTLS. It was rejected in
review, correctly. The sandbox must hold a client certificate and the `buildctl` binary for
`docker build` to work at all, so a run can ignore the `awx-docker` shim and call the daemon
directly — and BuildKit exposes `debug histories`, `debug logs <ref>` and `debug get <digest>` to
any authenticated client. One run could therefore enumerate another run's build references and
download its logs and content blobs. **A daemon shared between mutually untrusted runs cannot be
the boundary between them**, and no amount of shim hardening fixes that, because the shim is not
in the trust path.

#### The builder sidecar that does work

`k8s/optional/sandbox-buildkit-sidecar.yaml` is opt-in and off by default. It puts `buildkitd` in
the sandbox pod itself, reached over a pod-local unix socket at `/run/buildkit/buildkitd.sock`.
Because a sandbox pod **is** a Kata VM, the builder, its cache, its history and its content store
live and die inside that one run's VM. There is no shared daemon, no network endpoint, and no
credential to hand to untrusted code.

Eight findings shaped it, each of which breaks builds — or breaks isolation — if reverted:

- **Rootful, not rootless**, for the reason above. Rootless *does* work on ordinary AKS nodes, but
  only with seccomp **and** AppArmor `Unconfined` — `baseline` forbids both, and an escape would
  land beside the API and worker pods on a shared kernel. Rootful inside a per-run Kata VM is the
  stronger position: the VM is the boundary.
- **`CAP_NET_ADMIN` is required, and `CAP_BPF` does not substitute.** `runc` attaches a
  `BPF_CGROUP_DEVICE` program for the cgroup v2 device controller, and the kernel gates
  `BPF_PROG_QUERY` on `CAP_NET_ADMIN`. Without it every `RUN` fails with
  `bpf_prog_query(BPF_CGROUP_DEVICE) failed: operation not permitted`. Adding `CAP_BPF` instead was
  tested and does not help.
- **The 14 default container capabilities must be present.** `runc` cannot raise a build step's
  capabilities above its own bounding set, so dropping them produces
  `unable to apply caps: operation not permitted` on every `RUN`.
- **The container must override the pod's `runAsNonRoot` *and* `runAsGroup`.** The base template
  sets `runAsNonRoot: true`, `runAsUser: 1000`, `runAsGroup: 1000` at pod level and each line is
  inherited unless overridden. Without `runAsNonRoot: false` the kubelet refuses to start the
  container at all (`CreateContainerConfigError: container's runAsUser breaks non-root policy`);
  with it but without `runAsGroup: 0` the daemon runs as uid 0 / gid 1000 and every build step dies
  in `runc` with `open container mntns: open /proc/N/ns/mnt: permission denied`. Neither shows up in
  a standalone probe pod that omits the pod-level `securityContext` — both were found by patching
  the real template.
- **`CAP_SYS_PTRACE` is required in CNI network mode.** `runc` opens `/proc/<pid>/ns/mnt` of its own
  init process to join the prepared namespace; without it, build steps fail with the same
  `open container mntns` error. It does not appear under host networking. It is scoped to the
  builder's own PID namespace — the pod does not set `shareProcessNamespace`, so the builder cannot
  see, let alone trace, a process in the sandbox container, and build steps do not inherit it
  (measured `CapEff: 00000000a80425fb`, identical with and without it on the daemon).
- **BuildKit state must be tmpfs.** A default `emptyDir` in a Kata pod is **virtiofs**, which does
  not implement xattrs, so the OCI exporter fails at the final step with
  `failed to get xattr …: operation not supported` *after* every layer has already built. `tmpfs`
  lives in the guest and supports xattrs. The cost is that build state is RAM, charged against the
  container's memory limit.
- **`/sys/fs/cgroup` must be remounted read-write.** Kubernetes mounts it read-only for
  unprivileged containers (`EROFS` otherwise). Inside a Kata guest that tree is the VM's own, so
  the remount cannot reach the node's cgroups — which is why this is acceptable here and would not
  be on a shared-kernel node.

The socket is published under `/run`, deliberately **not** under `/mnt`: the per-run writable
system root mounts a tmpfs over `/mnt`, which would mask anything published there and break every
sandboxed command, not just builds.

#### Build steps do not run in the pod's network namespace

This is the one place where the first working version of this design was not safe, so it is worth
being explicit about what changed and why.

Under `--oci-worker-net host` a `RUN` step joins the **pod's** network namespace. `runc` grants
build steps the default capability set, and `CAP_NET_RAW` is in it — and it cannot be dropped,
because `runc` refuses to start any step whose capabilities exceed the daemon's bounding set. So a
Dockerfile could open an `AF_PACKET` socket on the pod's interfaces. Demonstrated on the live
cluster before this change:

```
#6 CapEff:  00000000a80425fb
#6 2: eth0    inet 10.244.2.183/32 ... scope global eth0
#6 tcpdump: listening on any, link-type LINUX_SLL2 ...
```

That is a real escalation rather than a theoretical one, because not all in-pod traffic is
encrypted: `AgentHost__McpEndpoint` is plain `http://`, and the run owner's GitHub token and turn
bearer token arrive over the in-pod `POST /configure` call. Model-controlled code is specifically
designed **not** to hold those credentials, so a build step that can sniff them would obtain
something the sandbox is built to withhold.

The builder therefore runs with `--oci-worker-net cni` and a CNI configuration that attaches
nothing: each build step gets a fresh network namespace containing only a `lo` interface. Measured
after the change, on the same cluster, from the unprivileged uid-1000 container:

| Property | Evidence |
| --- | --- |
| Build steps are in a different netns from the pod | `RUN_NETNS=net:[4026532186]` vs caller/pod `net:[4026531840]`. |
| Nothing but loopback is attached | `IFACES=lo`, and `/sys/class/net/lo/flags` is `0x9` (`IFF_UP｜IFF_LOOPBACK`). |
| No name resolution in a build step | `DNS_BLOCKED_GOOD`. |
| No network egress in a build step | `NET_IN_RUN_BLOCKED_GOOD`, `IMDS_BLOCKED_GOOD`. |
| Builds still work end to end | `BUILD_EXIT=0` and a 28,738,560-byte OCI tar containing `blobs/sha256/…`. |

Two consequences, stated plainly rather than buried:

- **A `RUN` step has no network.** `RUN apt-get install …`, `RUN npm install` and any other
  network-dependent build step do **not** work. Package installation from the run's allowed egress
  is unaffected *in the sandbox shell itself*, which keeps normal pod networking; only in-build
  networking is removed.
- **Base images still pull.** The daemon resolves and fetches them itself from the pod's netns,
  under the pod's NetworkPolicy, which is why the build above succeeded at all.

`--oci-worker-net bridge` was tried as a middle ground and does not work here. The CNI bridge plugin
must write `/proc/sys/net/ipv4/ip_forward`; `/proc/sys` is read-only
(`failed to enable forwarding: … read-only file system`), a `mount -o remount,rw /proc/sys` makes
every build step fail with `open container mntns: permission denied`, and pod-level `sysctls` are
rejected (`SysctlForbidden: net.ipv4.ip_forward not allowlisted`), which would require a kubelet
change on the node. Re-enabling in-build networking by returning to host networking would put
attacker-controlled `RUN` steps back on the pod's netns with `NET_RAW`, so it is deliberately not
offered as a flag.

#### The builder holds no identity

`buildkitd` is added to `azure.workload.identity/skip-containers` and mounts an empty `emptyDir`
over `/var/run/secrets/kubernetes.io/serviceaccount`, exactly as the executor sidecar already does.
The annotation value must be **semicolon**-separated (`agentweaver-exec;buildkitd`) — the deployed
azure-workload-identity webhook (measured at image tag `v1.5.1-17`) parses `skip-containers` as one
semicolon-delimited list, so a comma-joined value matches neither container name and the webhook
silently injects the federated token into both anyway. This was caught live on AKS Kata (SHA
`04b3fdc6`): with the comma-joined value in place, `kubectl exec -c buildkitd -- env | grep -c
^AZURE_` returned `4` and `/var/run/secrets/azure/tokens/azure-identity-token` existed and was
readable — a real credential leak into the builder, and into `agentweaver-exec` as well. Measured
in the builder container after the fix: the service-account directory contains only `.` and `..`,
no `token` file exists, `/var/run/secrets/azure/tokens/` does not exist, and `env | grep -c
^AZURE_` returns `0`. The socket it publishes is `srw-rw---- root 1000`, which is what lets the
uid-1000 sandbox container connect without granting it anything else.

#### What was measured, and the trade-off

On the live AKS Kata cluster, from a sandbox-shaped pod (agent container `runAsUser: 1000`,
`drop: ["ALL"]`, measured `CapEff: 0000000000000000`, no service-account token):

| Property | Evidence |
| --- | --- |
| Build runs **inside the run's VM**, not on the node | The `RUN` step reports kernel `6.6.137.mshv1-1.azl3`; the node runs `6.6.137.mshv2-1.azl3`. |
| Full build incl. `RUN` and OCI export | `BUILD_EXIT=0`; `RUN` output recovered from the exported layers, and a 28,738,560-byte OCI tar whose entries begin `blobs/sha256/…`. |
| Cross-run isolation | A second run's `buildctl debug histories` and `du` are **empty** while the first run shows 2 histories and 617 MB of cache. The shared-broker leak is structurally gone. |
| No host/node access | No docker or containerd socket; no service-account token; kubelet `10250` unreachable. |
| Strict NetworkPolicy holds | IMDS blocked from **both** containers. Adding a route to `169.254.169.254` from inside the guest *succeeds* and IMDS stays blocked — Cilium enforces on the host side of the VM boundary, so in-guest `NET_ADMIN` cannot defeat it. Builds still succeed through the DNS + 443 allowance. |
| Build steps are not privileged | `RUN` steps get the runc default set (`CapEff: 00000000a80425fb`), and the daemon refuses the escape hatch: `granting entitlement security.insecure is not allowed by build daemon configuration`. |
| The builder cannot reach the shared workspace | With `CAP_SYS_ADMIN`, the builder still cannot see the RWX workspace: it is not in its mount table, sibling PIDs are invisible (the pod does **not** set `shareProcessNamespace`), `/proc/<pid>/root` traversal finds nothing, and no guest block devices are exposed. |
| Deterministic cleanup | Deleting the pod destroys the VM and its RAM-backed build state in 8s; the sibling run is unaffected. |

**The trade-off, stated plainly for review.** A run that can reach this socket can drive a
root-capable build daemon inside its own VM. `security.insecure` is refused and build steps are
runc-confined, so this is not a direct privilege grant — but a `buildkitd` vulnerability would be a
root-in-guest compromise. It is bounded by the Kata VM: no node access, no other run's data, no
credential. Compared with a sandbox that has no builder at all, this is a real reduction in
defence-in-depth, which is exactly why it is **opt-in** and not part of the base template.

**Two invariants this design depends on.** Both are properties of the manifest, so a future edit
could silently remove them:

1. The builder container must **never** mount the workspace volume.
2. The pod must **never** set `shareProcessNamespace: true`, which would merge the PID namespaces
   and expose `/proc/<pid>/root` of the container holding the workspace.

**Pod Security Admission.** `SYS_ADMIN`/`NET_ADMIN`/`SYS_PTRACE` are outside the `baseline`
allow-list, so build-enabled sandbox pods need a namespace whose PSA level admits them for that one
container. Do **not** relax `agentweaver` itself — it hosts the control plane. On a stock cluster
the sidecar is absent, the socket does not exist, and the capability contract reports `image_build`
as `RequiresExternalService`; availability is probed from the socket, never inferred from an
environment variable. The probe is protocol-level, not existence-level: it connects and sends the
HTTP/2 client preface, and only reports `Supported` when the peer answers with a SETTINGS frame. A
crashed daemon leaves its socket inode behind, and a sidecar that is still starting will accept
without speaking gRPC — both would otherwise be advertised as a working builder to a caller whose
every build then fails at connect time.

**Registry publishing: what actually stops it, and what merely helps.** The real reason a build
cannot publish is that **there is no credential to publish with** — the builder holds no registry
credentials, no service-account token and no workload identity (measured above), so an authenticated
push has nothing to authenticate with.

`awx-docker` refuses `--push` and validates `--output` by parsing it as CSV field by field —
rejecting `--output type=image,name=…,push=true`, `type=registry`, a quoted `"push=true"` field, a
second `type=` anywhere in the value, and any quoted field at all — with exit 2, while allowing
`type=oci|docker|tar|local`; with no socket present it exits 3 with the contract's explanation
instead of a connection error. (A substring scan was not enough: `buildctl` reads this value as CSV,
so `type=image,name=x,"push=true",k=type=oci` would have been read as a pushing image build while a
last-match scan saw a permitted `oci`.) That is **ergonomics and defence-in-depth, not a boundary** —
`buildctl` ships in the AgentHost image and `BUILDKIT_HOST` is exported, so a run can call the
daemon directly and skip the shim entirely. This is the same reasoning that ruled out the shared
broker above: the shim is not in the trust path, so no property may rest on it. Stated as an
invariant: *every* guarantee in this section must hold when the shim is bypassed, and each one
listed here does — they rest on the absent credential, the empty build netns, and the per-run VM.

Per-run builders were tracked as issue #761; this design closes it.

### Reproducing the capability evidence

`scripts/validation/collect-kata-evidence.mjs` collects the transcript. It renders the shipped
`k8s/base/sandbox-template-agenthost.yaml` under a validation name, and — whenever the requested
cases include `buildkit` — applies the shipped `k8s/optional/sandbox-buildkit-sidecar.yaml` patch
with `kubectl patch --local`, so what gets deployed is exactly *patch(reviewed manifest)* rather
than a hand-written copy. The warm pool must then reach 3/3 ready containers, not 2/2.

```bash
node scripts/validation/collect-kata-evidence.mjs \
  --image <registry>/agentweaver-agent-host:<tag> \
  --phase capability --cases npm,nuget,apt,buildkit,preview
```

Two properties make the transcript trustworthy rather than merely plausible. Every `kubectl exec`
that runs a probe is checked, so a probe that died produces a failed collection instead of a
truncated transcript; and inside the probe, each positive proof is marked `required`, so a build
that failed, a registry that was unreachable or a missing builder socket ends the run loudly. The
build case drives the shipped `docker`/`awx-docker` shim over the pod-private socket — the artifact
a run actually gets on its PATH, which is how a shim gap in `--progress plain` was found and fixed —
and captures the build's own exit status directly rather than
through a pipeline, because `docker build … | tail` reports `tail`'s status and would turn a failed
build into a passing proof.

### How the preview hop was verified

The preview chain has two hops, and they were proven separately rather than as one run, so it is
worth being precise about which evidence covers which:

- **App → forwarder.** The app binds `127.0.0.1` only; a fetch of that loopback address from inside
  the sandbox returns the app's own payload. The forwarder reaches it because both containers of the
  run's pod share one network namespace — the same property the exec sidecar relies on.
- **Gateway → pod.** A request to `https://<name>.<zone>/` through `agentweaver-preview-gateway`
  returns **HTTP 200** with the expected body: real DNS-free SNI to the gateway's public address,
  real TLS termination against the zone wildcard certificate, a matched `HTTPRoute`, and a routed
  backend.

What has *not* been captured as a single end-to-end transcript is one run doing both at once. The
forwarder itself is unchanged by this work, so nothing here alters that hop; it is called out so the
evidence is not read as more than it is.

### winget

`winget` is the Windows Package Manager, and the sandbox is a Linux container inside a **Linux** Kata
VM, so it cannot execute there — not with an added capability, a mount, or a policy change. The
contract reports `winget_install` as `UnsupportedOnPlatform` with the remediation "use `apt-get` for
Linux runs, or a Windows executor backend", so callers get a machine-readable *no with a reason*
rather than a silent omission or a mysterious failure. A Windows executor backend is **out of scope
here and deferred** to issue #760. The state is deliberately distinct from `Unavailable`: no
redeployment of the Linux executor will ever change it.

## Security invariants

- The orchestration graph, HITL decisions, and run record live in the **worker**, never in the pod — a
  compromised pod cannot rewrite *what happens next*.
- Each run's heavy execution is confined to its **own Kata-isolated pod** with a **default-deny egress
  allowlist**; the pod cannot reach the database or arbitrary in-cluster services.
- The pod holds only run-scoped provider and repository credentials received through `/configure`. It cannot retrieve ambient user credentials.
- The A2A turn endpoint is application-layer authenticated with a per-run bearer token; a token from one
  pod is not valid against any other pod.
- The pod is **disposable and re-creatable**: durable state lives in the shared workspace volume and the
  brokered checkpoint, so killing a pod loses no run.
- The whole capability is **reversible by a single flag** (`Sandbox:AgentExecutionMode=in-api`).
- A run that installs system packages does so into a **per-run, RAM-backed, disposable** system
  root; the pod's privilege envelope is unchanged, and nothing installed is visible to another run,
  to the AgentHost container, or to the node.
- `image_build` requires an external service by default. The optional
  `k8s/optional/sandbox-buildkit-sidecar.yaml` enables an opt-in BuildKit sidecar
  with its required elevated container capability.

## Orphaned-pod reaper and Kubernetes-owned admission

### Why orphaned pods happen

Pod-per-run expects every run lifecycle path (normal completion, stall-fail, cancellation) to call `ReleaseAgentHostPodAsync` before exiting. Any path that fails to do so leaves a pod running without an active run — an **orphaned pod** that consumes cluster CPU quota but does no work. Over time these accumulate and exhaust the `katapool` capacity.

### The reaper

`AgentHostReaperService` (`IAgentHostReaper`, singleton) sweeps all agent-host pods in the namespace and identifies pods that have no matching active run record. It is driven by the coordinator heartbeat's 3rd tick phase on a tunable cadence:

```
Coordinator:ReaperIntervalTicks   (default 12)
```

With the default heartbeat interval the reaper fires roughly **every 2 minutes** (12 ticks × ~10 s). It terminates orphaned pods and emits a telemetry event for each one reaped.

Fresh claims are protected by the creation-grace policy documented in the
[sandbox pods reference](../reference/sandbox-pods.md#orphan-reaper-creation-grace).

All stall-fail and cancellation paths in `CoordinatorDispatchService` call `ReleaseAgentHostPodAsync` explicitly to minimize the reaper's workload. The reaper is the belt to that suspender.

### Kubernetes owns admission — there is no app-side capacity gate

Agentweaver does **not** pre-flight namespace quota before it launches a pod (issue #217). Earlier builds ran a `CheckAgentHostCapacityAsync` headroom check and, if the namespace had less than ~2 CPU of headroom, threw `AgentHostCapacityPendingException`, parked the subtask in `PendingCapacity`, and retried on a fixed interval before hard-failing with `capacity_unavailable`. That made the application second-guess the scheduler and discard runs while nodes sat idle.

The model is now simpler: the executor submits the `SandboxClaim` and **waits for Kubernetes to bind it** (`WaitForBoundWithProvisioningHeartbeatAsync` → `WaitForBoundAsync`). Kubernetes owns pod admission, scheduling, queueing, and — through the cluster autoscaler — headroom. A pod that sits **Pending** while a node frees up or `katapool` autoscales is a **legitimate wait, not a failure**. The namespace `ResourceQuota` no longer caps CPU/memory; it bounds only object counts (see the [sandbox pods reference](../reference/sandbox-pods.md#pod-identity-and-quota)).

### Provisioning heartbeat and the coordinator stall exemption

A claim can stay unbound longer than the coordinator's subtask-stall timeout (`Coordinator:SubtaskStallTimeoutMinutes`, default 5 min). To keep that legitimate wait from being misread as a hung child, the executor emits a `sandbox.provisioning_pending` heartbeat (`EventTypes.SandboxProvisioningPending`) into the **child run's** event stream about every **20 s** (`SandboxProvisioningHeartbeatInterval`) while the claim is unbound. This mirrors the #212 `tool.approval_pending` heartbeat pattern.

The coordinator's child-observation loop exempts a subtask whose most recent event is `sandbox.provisioning_pending`: it resets the stall window and keeps observing instead of firing `agent_stall_timeout`. The guard self-heals and cannot latch — any other real event (the pod binding, agent output, a terminal event) clears the flag, so a pod that genuinely hangs after provisioning is still caught. The heartbeat is best-effort: if the run-event stream is unavailable the wait degrades to a plain bind poll and never fails the launch.

![Provisioning heartbeat and the coordinator stall exemption: CoordinatorDispatchService, KubernetesSandboxExecutor, claim Bound /, emit sandbox.provisioning_pending, coordinator resets stall, child run executes](../diagrams/sandbox-pod-execution-fig5.png)

<!-- Rendered from ../diagrams/src/sandbox-pod-execution-fig5.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

::: info Legacy states
The `SubtaskStatus.PendingCapacity` enum, the `subtask.pending_capacity` event, and the amber **⏳ Waiting for capacity** badge are **retained for back-compat only**. New runs never enter `PendingCapacity`; a pre-upgrade subtask stranded in that status is recovered to `pending` and re-attempted. The terminal `capacity_unavailable` detail code is likewise legacy — Kubernetes now absorbs the wait instead of hard-failing.
:::

`OutcomeSpecPanel.tsx` still surfaces human-readable messages for terminal detail codes such as `agent_stall_timeout` and `agent_pod_reconciler_error`; `capacity_unavailable` and `agent_quota_exceeded` remain mapped for older records but are no longer produced.

Where this lives:

- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs` — claim submit, bind wait, `sandbox.provisioning_pending` heartbeat.
- `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs` — child-observation stall exemption and the `PendingCapacity → pending` back-compat recovery.
- `packages/Agentweaver.Domain/EventTypes.cs` — `SandboxProvisioningPending`.
- `apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs`
- `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs`

## Source

| Concern | Source |
| --- | --- |
| Scratch checkout creation, immutable commit/tree verification | `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:38-139` |
| Alternate index, commit creation, literal non-force push | `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:152-326` |
| HOME/XDG directory creation and Kata registration | `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs` |
| Cancellable, pruned nested-repository discovery | `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:575-628` |
| Nested metadata removal, content staging, gitlink rejection | `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:631-735` |
| Writable-turn finalization after the agent response | `apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:198-250` |
| Existing in-pod GitHub token store | `apps/Agentweaver.AgentHost/PodGitHubTokenStore.cs:6-49` |
| Local-writable launch coordinates | `apps/Agentweaver.Api/Sandbox/IRunAgentHostContextResolver.cs:65-99` |
| API-side descriptor validation and authoritative fast-forward | `apps/Agentweaver.Api/Git/WorktreeManager.cs:482-644` |
| Immutable Kata HOME mount and child environment | `packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs` |
| Executor-sidecar wire protocol, daemon, client, and relay | `packages/Agentweaver.SandboxExec/PodExec/` |
| Fail-closed sidecar probe at AgentHost startup | `apps/Agentweaver.AgentHost/Program.cs` |
| Executor sidecar container and its volumes | `k8s/base/sandbox-template-agenthost.yaml` |
| HOME propagation through WSL/bubblewrap | `packages/Agentweaver.SandboxExec/WslMxcSandboxExecutor.cs:130-158` |
| Disk-backed 8 GiB `execution-scratch` emptyDir | `k8s/base/sandbox-template-agenthost.yaml:139-175` |

## Related reading

- [Sandbox](./sandbox.md) — the underlying isolation model, claim lifecycle, and hardening.
- [Sandbox setup](../reference/sandbox-setup.md) — the backend ladder, the `sandbox.selected` event, and
  the MXC binary / `MXC_BIN_DIR` install.
- [Infrastructure & deployment](./infra-deployment.md) — cluster topology, PVCs, and network policy.
- [Sandbox pods reference](../reference/sandbox-pods.md) — flags, pod identity/quota, token injection,
  pod naming, and security properties.
- [Sandbox pod execution experience](../experience/sandbox-pod-execution.md) — what users and operators
  see and feel.
- [A2A bridge](./a2a-bridge.md) and [A2A reference](../reference/a2a.md) — the `-preview` transport that
  carries agent turns to the pod.
- [Sandbox browser preview](./sandbox-browser-preview.md) — exposing a server running inside the run's pod
  to the user over a public HTTPS reverse proxy.
- [Tool Approval SSE Contract](../tool-approval-sse-contract.md) — public approval outcomes and
  `tool.approval_resolved` behavior.
