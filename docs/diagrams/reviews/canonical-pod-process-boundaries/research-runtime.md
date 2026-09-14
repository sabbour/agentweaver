## Summary and scope

Read-only research completed against **`C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`**, repository **`sabbour/agentweaver`**. All repository citations below refer to that checkout, not the main working directory. No agents spawned, repository changes made, tests executed, or network requests performed. GitHub `get_me` is unavailable in the exposed toolset; local checkout identity was supplied in the task.

The audit’s principal corrections are supported by implementation and regression assertions: native shell is always rejected; controlled `run_command` has distinct registration, governance, and conditional approval gates; claim binding, HTTP liveness, and configured turn readiness are different states; and remote writable turns publish a temporary Git ref that the worker validates before changing the authoritative worktree. The new process-boundary visual should show **two containers/PID namespaces plus a narrower child mount view**, not a nested bubblewrap PID namespace.

## 1. Bounded diagram content models

### `agent-runtime-fig2` — redesign, retain name

**Smallest useful model:** one request/classification node, four clearly separated capability lanes, execution/result terminals.

1. **Native shell → reject + tool error.** No approval or “allowed” edge. Production sets `denyNativeShell = true`; the handler returns immediately for `PermissionRequestShell`. Regression test verifies rejection even with the direct executor.
   Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:548-571,1854-1869`; `sabbour/agentweaver:tests/Agentweaver.Tests/Sandbox/NativeShellSandboxRoutingTests.cs:54-74`.

2. **Native file request → permission/governance → allow native operation OR reject.** Keep this separate from the custom shell executor; permission checks do not turn native shell into sandboxed execution.
   Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:2097-2108`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/SandboxGovernance.cs:125-147`.

3. **Custom `run_command` → governance → command validation/conditional approval → selected executor → result.**
   - Registration prerequisite: **`ShellEnabled && (IsRealIsolation || BackendName == "direct")`**.
   - Policy denial ends the path; approval cannot override it.
   - Destructive commands or `RequireApprovalForAllShell` trigger approval handling; previously approved or ordinary permitted commands proceed without a universal human gate.
   - Shell approval emits a request and returns to the model when not approved; do not depict every command as a blocking URL-style wait.
   Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentTools/SandboxToolRegistry.cs:31-37`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:2063-2093`; `sabbour/agentweaver:packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs:37-75`; `sabbour/agentweaver:docs/deep-dive/sandboxed-execution.md:169-174`.

4. **URL fetch → existing policy/auto-approval OR human approval wait → approve/reject → result.** This is a separate permission-handler branch, not filesystem governance.
   Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1872-1919,1952-1965`.

5. **Agentweaver API tool → runtime permission approval → HTTP API authentication + run/project/agent scope → API operation OR HTTP rejection → result.** Explicitly complete both authorization-result arrows. Runtime permission approval is **not** API authorization or operator approval.
   Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:2015-2033`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/AgentweaverApiTools.cs:434-446,474-478`; `sabbour/agentweaver:apps/Agentweaver.Api/Security/RunAuthorship.cs:66-88`.

Keep intent/outcome semantic shortcuts as a small annotation, not extra full lanes. The live runner selects intent/outcome, optional question, and optional controlled command from the catalog rather than registering all custom file tools twice.
Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:2384-2411`.

### `agent-runtime-fig3` — redesign, retain name

**Nodes:** API admission/orchestrator; context/workflow; turn executor; logical turn agent/provider; tools; worktree operations; terminal/watch result. Avoid assigning every logical node to the same physical process.

**Arrows:**

- API/orchestrator → workspace/context → workflow → turn executor → local agent **or remote leaf proxy**.
- Turn agent ↔ provider/tools → completed turn.
- **Local/shared result → worker commit bookkeeping.**
- **Remote `LocalWritable` result → prepared-writeback envelope → worker validation/application → commit/diff bookkeeping.**
- Missing/invalid required envelope or publication error → typed failed terminal, **not** no-change success.

The implementation applies a valid prepared envelope before the normal commit bookkeeping; this is not an entirely separate replacement for all worker post-processing.
Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:183-212,225-245`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:469-503`.

For remote publication, AgentHost emits a typed DataPart only for `LocalWritable`; worker-side validation performs the authoritative fast-forward.
Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-368`; `sabbour/agentweaver:apps/Agentweaver.Api/Git/WorktreeManager.cs:512-530,622-672`.

Do **not** draw provider-session serialization as guaranteed cross-pod reconstruction. `IWorkflowTurnAgent` exposes setup/run/disposal, not serialization methods; the remote proxy creates an A2A session locally.
Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/IWorkflowTurnAgent.cs:18-42`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:200-204`.

### `sandbox-fig2` — retain as canonical selection ladder

**Minimal router/factory nesting:**

`Need host executor → Sandbox:Backend override / cluster detection`

- `kubernetes`, or in cluster without `local` override → Kubernetes initialization → claim executor.
- Kubernetes initialization failure → **throw / fail closed**, never local fallback.
- Otherwise → **local factory**:
  - Windows → processcontainer → WSL fallback;
  - Linux → bubblewrap → LXC fallback;
  - unavailable → direct passthrough warning.

Mark **`wsl-unshare: IsRealIsolation=false`** and **`direct: IsRealIsolation=false; automatically reachable fallback`**. Add one shared shell-registration note rather than another full governance diagram.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs:89-109,162-172`; `sabbour/agentweaver:packages/Agentweaver.SandboxExec/SandboxExecutorFactory.cs:20-71`; `sabbour/agentweaver:packages/Agentweaver.SandboxExec/WslMxcSandboxExecutor.cs:28-34`.

Reuse from the pod page in place of `sandbox-pod-execution-fig3`. Keep the in-pod `PodExecSandboxClient → KataBwrapExecutor` selection **outside** this host ladder.

### `sandbox-fig3` — redesign as retained utility-command API

**Scope label:** “Retained Kubernetes command-executor path — not the deployed AgentHost turn loop.”

**Nodes:** command caller; Kubernetes executor; claim/controller/warm-pool binding; bound pod; optional run→pod mapping.

**Arrows:**

`command → validate workspace + bound timeout → create claim → wait bound → pod exec → stdout/stderr/result`

Side branches:

- run-scoped command → register run/pod mapping;
- ad-hoc claim → delete on completion;
- run-scoped claim → retain until cleanup/TTL.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:308-361`.

Do not depict a separately deployed generic `sleep infinity` pool. The shipped baseline is the AgentHost template with `agentweaver-agent-host` and `agentweaver-exec` containers. Do not give PodNameRegistry ownership of replica-safe preview routing; remove that legacy arrow and leave preview ownership to its assigned researcher.
Evidence: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:104-122,252-280`; audit disposition explicitly scopes this diagram to the retained command API.

### `canonical-sandbox-pod-evolution` — retain

**Two small panels are sufficient:**

- **Before:** worker contains workflow + live leaf/SDK; shell calls leave for command sandbox.
- **Now:** worker contains workflow + remote leaf proxy; claim/configure prepares AgentHost; A2A requests/results cross to pod containing live leaf/SDK and controlled tools.

Do not attach a mandatory SQLite→Postgres migration. Remoting is selected at the workflow-agent factory seam: worker, RAI, rubberduck, build/test, and Scribe all receive proxies.
Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs:52-65`; `sabbour/agentweaver:docs/deep-dive/sandbox-pod-execution.md:31-36,48-58`.

**Qualification:** “workflow/HITL stays worker-side” means graph-level gates. Pod-local tool approval waits do exist and need fig7’s return path.
Evidence: `sabbour/agentweaver:docs/deep-dive/sandbox-pod-execution.md:646-657`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1917-1965`.

### `sandbox-pod-execution-fig4` — redesign

**Nodes:** authoritative repository/worktree; materializer; verified local checkout; local read-only/writable choice; publication preparation; temporary ref; worker validator/application. HOME is a small separate sibling storage node.

**Arrows:**

1. Source ref/base/tree descriptor → initialize + shallow fetch → verify **both** commit/tree → detached local checkout.
2. Checkout → `LocalReadOnly` build/test → **no publication**.
3. Checkout → `LocalWritable` edits → alternate-index final-tree preparation.
4. Nested repositories → flatten contents, restore metadata, reject residual gitlinks.
5. Equal result tree → **no-change descriptor, no push**.
6. Changed tree → single-parent commit → **temporary writeback ref** in source repository.
7. Descriptor → worker validates run/ref/base/tree/clean branch → fast-forward authoritative worktree.
8. Runtime HOME/XDG outside checkout → registered child environment/mount view.

Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-148,285-318,328-378,524-550`; `sabbour/agentweaver:apps/Agentweaver.Api/Git/WorktreeManager.cs:521-595,598-672`.

Regression assertions independently verify read-only publication rejection, unchanged descriptor, no temporary ref, and zero changed paths.
Evidence: `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/PodLocalWorkspaceManagerTests.cs:224-256`.

**Important label:** `LocalReadOnly` means publication refusal, not a promise that build/test cannot write files in its ephemeral checkout.

### `sandbox-pod-execution-fig5` — redesign

**Nodes:** coordinator observer; child launch/executor; claim/controller; child progress stream; AgentHost configure step.

**Sequence:**

`coordinator dispatches child → child launches claim → repeated binding polls`

While unbound:

`executor → child stream: sandbox.provisioning_pending (~20s) → coordinator observes legitimate provisioning wait`

Then:

`Ready condition + bound pod name → resolve IP → standby health → configure/Setup → leaf execution`

Use a collapsed “continue with fig6” box for the last sequence. **Claim Ready is not configured AgentHost readiness.**

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:183-189,635-677,691-731,1448-1482`; `sabbour/agentweaver:docs/deep-dive/sandbox-pod-execution.md:1138-1144`.

### `sandbox-pod-execution-fig6` — redesign

**Recommended participants:** executor; claim/controller/pool; AgentHost; pod/endpoint mapping; turn-token registry. Provider-data resolution can be an executor annotation rather than another participant.

**Required order:**

1. Resolve provider and mint run turn token.
2. Create/adopt claim **without `spec.env`**.
3. Wait for bound pod; register pod mapping and, for newly created claim, turn token.
4. Resolve pod IP.
5. `GET /healthz → 200 "standby"`: listener/liveness only.
6. `POST /configure` with identity, workspace descriptors, approval settings, **Copilot credential OR BYOK configuration**, and purpose-scoped optional credentials.
7. AgentHost one-time configuration → prepare effective Shared/local workspace + HOME → SetupAsync → configured response/effective directory.
8. Register effective directory and A2A endpoint → first turn.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:633-677,691-737`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:259-290,405-411`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostStartupService.cs:166-201,222-241`.

Distinct readiness facts:

- Before configuration, nonexempt requests receive 503.
- `/healthz` and `/configure` are exempt.
- `/healthz` returns 200 in standby and ready states.
- Configuration is one-shot; subsequent attempts return 409.
- A2A bearer comparison is implemented only when a token is nonempty; production launch supplies it.

Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:188-202,283-290,386-411`.

Tests assert no claim `spec.env` and persistence of the returned local effective directory. Some **test comments still mention runtime Key Vault fetch**; their actual assertions do not establish that outdated behavior.
Evidence: `sabbour/agentweaver:tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:292-313,933-967`.

### `sandbox-pod-execution-fig7` — retain bounded approval-return model

**Participants:** operator; API endpoint/owning-run resolver; durable gate; authenticated approval client; pod-local gate.

**Arrows:**

`operator grant → resolve owning child + authorize caller → durable GrantAsync`

- known terminal state → return existing result;
- **Unknown only**, pod-per-run → resolve pod origin/credential → authenticated pod endpoint → local gate resolves → response back.

Show **explicit return arrows** pod→API→operator. Keep 404/409/503 and scoped provisional-grant rollback/finalize protocol in prose, not the successful sequence.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1956-1981,2002-2047,2064-2084`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/AgentHostApprovalHttpClient.cs:74-108`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:804-815`.

Auth nuance: the pod approval handler uses `PreviewRunnerEndpointAuth`, which accepts either configured run control credential and allows the no-credential local/dev case. Avoid “all endpoint configurations unconditionally require turn token.”
Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:810-815,962-987`.

### Proposed `canonical-pod-process-boundaries` — add

**Smallest useful nested boundaries:**

- **Kata pod/VM — shared pod network**
  - **AgentHost container / PID namespace A:** A2A + provider session + runtime/control state.
  - **Executor container / PID namespace B:** PodExec daemon → `KataBwrapExecutor` → model-controlled process.
  - **Pod-private IPC volume:** authenticated UDS + token, `emptyDir medium: Memory`, mounted into those two containers only.
  - Workspace volumes accessible to platform containers.
- Inside executor, a **child mount view**, not a third PID container:
  - registered run workspace;
  - registered HOME/XDG outside checkout;
  - authorized scratch/tmp;
  - exact Git metadata, read-only;
  - system roots, with optional per-run writable overlays;
  - executor’s runtime-provided `/proc`.

**Directed arrows:**

`AgentHost → authenticated UDS → executor daemon → bwrap child → results back`

`platform registration → workspace/HOME allowlist → child mount plan`

`exact workspace/Git metadata/HOME → child view`

Use absence annotations, not arrows implying access:

**No shared PVC root; no sibling worktrees; no IPC socket/token mount; no AgentHost PID tree; no executor Kubernetes/AAD identity.**

Evidence:
- Separate containers and identity exclusion: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:58-61,151-162,269-292,341-357`.
- Memory-backed IPC: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:391-399`.
- Startup fails closed: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:77-109`.
- Same-PID-namespace rejection: `sabbour/agentweaver:packages/Agentweaver.SandboxExec/PodExec/PodExecServer.cs:212-242`.
- No nested PID namespace; bind existing procfs; conditional network unshare; rebuilt environment: `sabbour/agentweaver:packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs:609-655`.
- Exact registered workspace and HOME required: `sabbour/agentweaver:packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs:892-931`.
- Regression assertions: `sabbour/agentweaver:tests/Agentweaver.Tests/KataBwrapExecutorTests.cs:29-47,110-127,145-164`.

Do not mark `/usr` universally read-only: the current implementation can bind a run-private writable overlay for package installation. The image itself is not thereby made writable to other runs.
Evidence: `sabbour/agentweaver:packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs:600-606,625-637`.

## 2. Exact documentation corrections

| Location / inaccurate text | Source-backed correction |
|---|---|
| `agent-runtime.md:97-99`, provider serialization discussion | Retain for local Copilot implementation, but add: **“These provider hooks do not by themselves establish provider-session restoration into a replacement AgentHost pod.”** The remote interface and proxy do not expose the same serialization contract. `IWorkflowTurnAgent.cs:18-42`; `RemoteAgentProxy.cs:200-204`. |
| `agent-runtime.md:220`, API scope described only as project/name/base URL/API key | Add **run ID and run capability token validated by the API**; runtime tool permission approval does not authorize cross-project identity. `AgentweaverApiTools.cs:434-446`; `RunAuthorship.cs:66-81`. |
| `agent-runtime.md:54` lifecycle figure | Add prepared remote writeback before worker commit/diff bookkeeping. Existing native-shell and durable-approval prose at `:193-201,230-237` is already correct and should be preserved. |
| `sandbox.md:146`, absolute “fail closed in cluster” | Qualify: **“When Kubernetes is selected, initialization fails closed. Explicit `Sandbox:Backend=local` selects the local factory even in cluster.”** `SandboxExecutorRouter.cs:91-109,167-171`. |
| `sandbox.md:155`, grouped “WSL2 with bubblewrap/unshare” | Explicitly distinguish **wsl-bwrap real isolation** from **wsl-unshare non-real isolation; controlled shell unavailable under that backend**. `WslMxcSandboxExecutor.cs:28-34`; `SandboxToolRegistry.cs:31-35`. |
| `sandbox.md:164-169`, generic command-exec story described as production default | Retitle/reframe as **retained utility-command contract**; current AgentHost shell execution is in-pod PodExec, not a fresh claim for every shell invocation. `Program.cs:77-109`; `KubernetesSandboxExecutor.cs:330-346`. |
| `sandbox.md:195`, “calls configure, waits for healthz” | Reverse to **pod IP → healthz reachability → configure/Setup → endpoint registration**. `KubernetesSandboxExecutor.cs:654-731`. |
| `sandbox.md:214`, “SandboxClaim environment carries only static config” | **“Static configuration lives in the template/config map; shipped warm-pool claims omit `spec.env` entirely.”** Template `:45-53`; claim test `:304-308`. |
| `sandbox.md:224-226`, all Setup roots identical to `Run.WorktreePath` | Limit identity to valid Shared mode. Local modes materialize under execution scratch; missing Shared path falls back to **pod-private workspace**, not shared `/workspace`. `AgentHostStartupService.cs:173-201`. |
| `sandbox.md:222,226`, unconditional turn-token endpoint enforcement | Say production launch supplies the per-run token, and middleware validates it **when configured**. `Program.cs:386-399`. |
| `sandbox.md:250`, “Writable workspace and temp only” | Include execution scratch and HOME; root filesystem is currently writable in both baseline containers. Distinguish platform-container mounts from child mount confinement. Template `:112-114,228-242,281-288,341-357`. |
| `sandbox.md:263-268`, legacy selector/no inbound | The policy targets **`app: agentweaver-agent-host`**, not only legacy sandbox pods; “no inbound” is not an accurate blanket statement. Keep boundary wording as **default-deny plus explicit permitted ingress**, with detailed policy ownership handed to infrastructure researcher. `networkpolicy-sandbox.yaml:12-15,44-60`. |
| `sandboxed-execution.md:11,39,157,272`, direct requires explicit opt-in / never unsandboxed by default | **“The local factory may automatically fall back to direct. Direct bypasses governance’s isolation/shell checks, but live `run_command` registration still requires ShellEnabled.”** `SandboxExecutorFactory.cs:60-71`; `SandboxGovernance.cs:138-147`; `SandboxToolRegistry.cs:31-35`. |
| `sandboxed-execution.md:24,34,180`, same factory on every target / API overrides completed result | Explain **API router chooses Kubernetes versus local first; local factory probes only local backends**. `SandboxExecutorRouter.cs:89-109,162-172`. |
| `sandboxed-execution.md:60-67`, catalog implies fixed 15/16 live tools; conditional omits direct | Rename table **canonical catalog**, remove fixed live count, describe selected live subset; condition becomes `(real isolation OR direct) AND ShellEnabled`. `CopilotAIAgent.cs:2384-2411`; `SandboxToolRegistry.cs:31-35`. |
| `sandboxed-execution.md:176`, live approvals only in memory and cleared at completion | Distinguish local runtime default from API-hosted durable implementations. `apps/Agentweaver.Api/Program.cs:541-550`. |
| `sandbox-pod-execution.md:38`, before-only alt | Describe **before/after leaf relocation via claim/configure and A2A**, not only old in-memory worker history. |
| `sandbox-pod-execution.md:111-114,116-120` | Attribute host choice to router/local factory and `sandbox.selected` to runtime observability; replace duplicate fig3 embed/provenance with **sandbox-fig2**. |
| `sandbox-pod-execution.md:142-146` | Explicitly list **wsl-unshare and direct as non-real isolation**; add ShellEnabled registration prerequisite. |
| `sandbox-pod-execution.md:188-189`, sidecar “shares only … network … and two workspace volumes” | Add **pod-private authenticated IPC volume**; the existing next section describes it, so “only” contradicts the page/template. Template `:240-242,341-347`. |
| `sandbox-pod-execution.md:404`, “only then healthz returns 200”, shared-root invariant, env-root fallback, wrong sequence | Replace paragraph with the fig6 sequence above and Shared/local/private-fallback distinction. `Program.cs:405-411`; `AgentHostStartupService.cs:173-201`; `KubernetesSandboxExecutor.cs:654-731`. |
| `sandbox-pod-execution.md:454-460`, GitHub token “available through AgentHost token store” | Say **purpose-bound credentials reside in configured runtime state; this writeback push targets the shared filesystem repository and does not require minting a separate GitHub credential**. `PodLocalWorkspaceManager.cs:98-100,342-348`; `AgentHostRuntimeState.cs:83-95,165-174`. |
| `sandbox-pod-execution.md:602-617`, release “back to warm pool” and perfect rehydration | **Delete/release the claimed pod; a later launch adopts a replacement warm pod.** Release is best-effort and may be deferred for a retained active claim. Do not promise perfect remote provider-session reconstruction without separate evidence. `RunWatchLoopService.cs:518-533`; `KubernetesSandboxExecutor.cs:956-985`; `RemoteAgentProxy.cs:200-204`. |
| `sandbox-pod-execution.md:739`, Key Vault secret name + pod fetch “with … no broker” | Replace with **one-time delivery of redeemed run-bound Copilot credential or BYOK configuration, plus purpose-scoped credentials; no ambient user-token lookup by pod**. `Program.cs:59-64,263-268`; `AgentHostRuntimeState.cs:83-95`. |
| `sandbox-pod-execution.md:1144`, execution starts after unspecified “pod becomes ready” | Specify **claim bound → standby listener reachable → configured leaf ready**. |

## 3. Owned concept dispositions confirmed

Prefix below is **`deep-dive-execution-`**.

### `agent-runtime.md`

- **run-lifecycle — redesign** → `agent-runtime-fig3`.
- **tool-governance — redesign** → `agent-runtime-fig2`.
- **runtime-tools-context — retain** textual tables/subset explanation.
- **runtime-events — reuse** `distributed-execution-scaling-fig4`; no separate runtime persistence diagram. Event ownership left to assigned agent.
- **runtime-reviewers — retain** prose. RAI has explicit verdict parsing contract and configurable fail-closed behavior; Scribe reports failure without throwing away the workflow result.
  Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RaiTurnExecutor.cs:94-105,129-145`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/ScribeTurnExecutor.cs:210-230`.

### `sandbox.md`

- **sandbox-layered-boundary — reuse** shared-owned `canonical-sandbox-boundary`.
- **host-backend-router — retain** `sandbox-fig2`.
- **command-claim-lifecycle — redesign** `sandbox-fig3`.
- **filesystem-controls — retain** precise prose. Opened-handle verification is a custom filesystem control, not a substitute for process isolation.
  Evidence: `sabbour/agentweaver:packages/Agentweaver.SandboxFs/SandboxedFileTools.cs:21-27,43-64`.
- **sandbox-configure-hardening — reuse** corrected `sandbox-pod-execution-fig6`.

### `sandboxed-execution.md`

- **local-sandbox-overview — reuse** shared-owned `canonical-sandbox-boundary`; correct stale alt/direct guarantees.
- **local-backend-tool-policy — retain** corrected prose/tables.
- **local-approvals-and-credentials — retain** corrected durable/default distinction.
- **local-platform-runbooks — retain**, not another diagram. Preserve `wsl-unshare` and Windows-network limitations; historical distribution/version claims were not independently live-verified.

### `sandbox-pod-execution.md`

- **pod-evolution — retain** `canonical-sandbox-pod-evolution`.
- **pod-backend-ladder — merge** duplicate `sandbox-pod-execution-fig3` into `sandbox-fig2`.
- **pod-process-boundaries — redesign/add missing visual** → proposed `canonical-pod-process-boundaries`.
- **pod-claim-configure — redesign** fig6.
- **local-workspace-publication — redesign** fig4.
- **hybrid-pod-state — redesign** inline state model; do not silently treat it as an existing catalog PNG.
- **pod-approval-return — retain** fig7.
- **pod-admission-heartbeat — redesign** fig5.
- **pod-capability-contract — retain** table/prose. Preserve distinction between unavailable dependency and unsupported platform; optional builder is not baseline.
  Evidence: `sabbour/agentweaver:packages/Agentweaver.SandboxExec/SandboxCapabilities.cs:17-35,60-65`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:298-305`.
- **kata-pool-topology — retain** table/runbook; “preferred affinity” does not guarantee capacity or “never stranded.”
  Evidence: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:79-93`.
- **pod-credential-and-preview-references — reuse** purpose-bound credential material and existing preview reference; correct Key Vault reconstruction instruction, no additional preview research/diagram.

## Remaining bounded uncertainties

- **Do not assert perfect cross-pod provider-session rehydration.** Local serialization hooks and workflow checkpoints are insufficient evidence of the remote transport contract.
- Tests cited above were **read, not run**; historical AKS observations and benchmark tables are not newly verified measurements.
- Several source/test **comments** retain older KV/identity or read-only-system-root descriptions. Prefer executable branches, current YAML fields, and assertions over those comments.
- Preview routing, deployment infrastructure, and durable event ownership remain with the other agents; only their interfaces necessary to explain these boundaries are identified here.
