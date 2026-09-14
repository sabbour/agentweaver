"""Auditable, fail-on-mismatch bulk corrections for plan-owned consumer pages."""
import json
from pathlib import Path
import re

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
PLAN = json.loads((REPO / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-execution.json").read_text())
RULES = {
    "sandbox.md": [
        (r"The key production invariant is .*?production\.",
         "Selected Kubernetes initialization fails closed. The API router first chooses Kubernetes versus local using the explicit backend and cluster detection; `Sandbox:Backend=local` still chooses the local factory inside a cluster."),
        (r"\| WSL2 with bubblewrap/unshare .*?\|",
         "| WSL2: `wsl-bwrap` / `wsl-unshare` |"),
        (r"## Kubernetes sandbox lifecycle: claims over pods",
         "## Kubernetes sandbox lifecycle: retained utility-command contract"),
        (r"Production command execution is built around .*?lifecycle control:",
         "The retained Kubernetes utility-command API uses claims and pod exec. It is not the deployed AgentHost turn loop: model-controlled commands there use pod-private PodExec and the executor sidecar, not a new claim for every shell invocation. The retained contract is:"),
        (r"3\. For pod-per-run AgentHost pods, .*?endpoint .*?\.",
         "3. For AgentHost, resolve pod IP, wait for the HTTP 200 standby listener, configure once and complete setup, then register the effective workspace and A2A endpoint."),
        (r"Run-scoped commands may need preview/debug support\..*?(?=\n\n)",
         "Run-scoped utility commands derive a stable claim name and record the pod. The local `kubectl` fallback may use that mapping. Production preview resolves claims and HTTPRoute annotations from cluster state, not a replica-local pod registry; see [sandbox browser preview](./sandbox-browser-preview.md)."),
        (r"AgentHost claims now bind .*?`AgentHost__KeyVaultUri`\.",
         "AgentHost uses two standby warm pods. Shipped claims omit `spec.env`; static configuration belongs to the template/config map. Setup waits for one-time `/configure`. See the [claim/configure sequence](../diagrams/sandbox-pod-execution-fig6.png)."),
        (r"`AgentHostRuntimeState\.TryConfigure\(\.\.\.\)`.*?(?=\n\n)",
         "`TryConfigure` is one-shot; repeat configuration returns 409. Setup resolves a valid Shared workspace, a verified local checkout, or a pod-private fallback when Shared has no supplied path. The effective directory is not universally `Run.WorktreePath`. `/healthz` is already 200 in standby. Production supplies a turn token, enforced when nonempty. `/configure` delivers that token and instead relies on configured transport controls and network policy; the additive preview range includes 8088, so policy alone is not API/worker-exclusive."),
        (r"\| `workingDirectory` \|.*?\n",
         "| `workingDirectory` | Shared coordinate; local modes resolve a verified pod-local effective execution directory. |\n"),
        (r"- \*\*Writable workspace and temp only:\*\*.*?\n",
         "- **Separate platform and child views:** platform containers have writable roots and workspace/scratch/HOME mounts. The model-controlled child has a narrower allowlist without the PVC root, siblings or IPC secrets.\n"),
        (r"The standard Kubernetes policies select.*?(?=\n\nThe service-CIDR warning)",
         "Current policies select `app: agentweaver-agent-host`: default deny plus API/worker control ingress, same-namespace preview-Gateway ingress, DNS, API/MCP egress and public-IP HTTPS with explicit CIDR exclusions. They are not a GitHub-only or FQDN-only allowlist. AgentHost has a service-account token; the executor and its child view exclude that identity. See [actual rules and manifest gaps](./infra-deployment.md#network-policy-model)."),
        (r"9\. \*\*Harden the pod\.\*\*.*?\n",
         "9. **Harden platform and child separately.** Preserve Kata, non-root execution, dropped capabilities, distinct container PID namespaces and the per-child mount allowlist. Current platform roots are writable; executor children must not inherit AgentHost identity or IPC secrets.\n"),
        (r"10\. \*\*Add network policy\.\*\*.*?\n",
         "10. **Add network policy.** Default-deny with explicit control/preview ingress and the actual egress exceptions; assess the union rather than asserting domain-only access.\n"),
    ],
    "sandboxed-execution.md": [
        (r"The general principle:.*?(?=\n\n)",
         "Live `run_command` registration requires `ShellEnabled` and either real isolation or `direct`. The local factory may fall back to direct automatically, not only through explicit opt-in. Direct is not isolation and requires a trusted/disposable surrounding environment. Native shell stays denied."),
        (r"When the API runs \*\*inside a Kubernetes cluster\*\*.*?(?=\n\n)",
         "The API router first chooses Kubernetes versus local from `Sandbox:Backend` and cluster detection. Explicit `local` bypasses cluster selection; selected Kubernetes initialization fails closed. Only then does the local path invoke the factory. See the [host selector](../diagrams/sandbox-fig2.png)."),
        (r"The GitHub Copilot SDK runner registers custom.*?15 or 16 available tools\.",
         "This table is the canonical tool catalog, not a fixed live count. The runner selects intent/outcome, optional question and controlled-command tools; native file tools avoid duplicate custom exposure. AgentHost adds purpose-appropriate preview tools."),
        (r"Yes — only when `IsRealIsolation && ShellEnabled`",
         "`ShellEnabled && (IsRealIsolation || BackendName == \"direct\")`"),
        (r"If either check fails, the call is denied.*?(?=\n\n)",
         "These governance checks deny execution except for direct, which bypasses them. Direct can be explicit or an automatic local fallback. Live registration still requires `ShellEnabled`, including direct; approval cannot override policy denial."),
        (r"Approvals are scoped to the run\..*?(?=\n\n)",
         "Approvals are scoped by `(runId, commandHash)`. Local runtime defaults may use an in-memory store; API-hosted runs register durable approval implementations. Not every live approval is process-local or universally cleared at completion."),
        (r"The same `SandboxExecutorFactory` runs on all targets\..*?:",
         "The API router chooses Kubernetes or local first. Local tiers differ; `wsl-unshare` and direct do not report real isolation:"),
        (r"PATH is never consulted for `lxc-exec`\..*?(?=\n\n)",
         "PATH is never consulted for `lxc-exec`. If neither isolation backend exists, the factory automatically falls back to direct. Live registration still requires `ShellEnabled`; explicit `direct: true` is not required for fallback."),
        (r"which is an explicit opt-out", "which can be an explicit choice or automatic fallback"),
    ],
    "sandbox-pod-execution.md": [
        (r"`SandboxExecutorFactory` selects exactly.*?(?=\n\n)",
         "The API router chooses Kubernetes versus local first; the local factory then probes supported local backends. Runtime observability emits `sandbox.selected` with backend, isolation status and reason. The in-pod PodExec client is a separate executor seam."),
        (r"!\[One executor per host, chosen at run start:.*?\]\(\.\./diagrams/sandbox-pod-execution-fig3\.png\)",
         "![API router selects Kubernetes or the local factory; local backends may fall back to direct](../diagrams/sandbox-fig2.png)"),
        (r"\.\./diagrams/src/sandbox-pod-execution-fig3\.json", "../diagrams/src/sandbox-fig2.json"),
        (r"`IsRealIsolation` is `true` for every real backend and `false` for `direct`\.",
         "`IsRealIsolation` is false for both `wsl-unshare` and direct. Live `run_command` additionally requires `ShellEnabled`."),
        (r"namespace \(so preview ports stay reachable\) and the two workspace volumes\.",
         "namespace (so preview ports stay reachable), workspace volumes and a pod-private authenticated IPC volume."),
        (r"After `/configure`, `AgentHostStartupService\.ConfigureAsync`.*?(?=\n\n)",
         "Normal order: **claim bound -> pod IP -> HTTP 200 standby listener -> one-time configuration/SetupAsync -> effective workspace and endpoint registration -> first turn**. `/healthz` is 200 before configuration (`standby`) and afterward (`ready`); other nonexempt routes return 503 before setup. Claims omit `spec.env`. Shared mode uses a valid shared worktree, local modes use verified ephemeral checkouts, and missing Shared coordinates fall back to pod-private storage. Production sends a token; middleware equality is conditional on a nonempty token. The additive preview range includes port 8088, so network policy alone is not API/worker-exclusive."),
        (r"The run's existing GitHub token remains available through the\nAgentHost token store.*?mechanism\.",
         "Purpose-bound credentials live in configured runtime state, not an ambient AgentHost token store. Filesystem-origin writeback mints no second GitHub credential."),
        (r"cluster-autoscaler scales `katapool` when demand grows — pods are \*\*never stranded\*\*\.",
         "autoscaling can add capacity, but preferred affinity does not guarantee scheduling, quota or capacity."),
        (r"```mermaid\nstateDiagram-v2.*?```",
         "| Transition | Current contract |\n| --- | --- |\n| Claiming -> standby | Controller binding and listener liveness, not configured readiness. |\n| Standby -> active | One-time configuration prepares the effective workspace and leaf. |\n| Active -> active | Consecutive turns retain the pod. |\n| External suspension -> release attempt | With release enabled, checkpoint workflow state and best-effort release/delete the claim. |\n| Active preview -> retained | Positive unexpired route evidence may defer release; keepalive reconciles retention. |\n| Released -> resume | Adopt a replacement warm pod, configure fresh context, resume workflow state. |\n| Terminal / expiry | Cleanup follows claim and preview retention rules. |"),
        (r"back to the warm pool\.", "by deleting/releasing the claim when retention permits, not by returning the same configured pod to the pool."),
        (r"For this to be correct, the checkpoint must carry enough.*?Two facts make rehydration cheap and safe:",
         "Workflow checkpoint state preserves the external-request correlation. Local provider serialization does not prove perfect restoration in a replacement AgentHost pod; the remote proxy creates its own A2A session. Durable worktree and fresh configuration are separate:"),
        (r"Egress is \*\*default-deny\*\* with a narrow allowlist:.*?database\.",
         "Egress is default-deny plus public-IP HTTPS with CIDR exclusions, DNS and API/MCP exceptions, not only named model/git endpoints. AgentHost has no application database connection. See [actual policy limits](./infra-deployment.md#network-policy-model)."),
        (r"stored in `AgentHostRuntimeState`, and persisted under the\nreplica-safe key",
         "kept in AgentHost memory, and separately persisted best-effort by the server-side secret store under the\nreplica-safe key"),
        (r"back through the API on demand", "through an API-provisioned and HTTPS-validated Gateway-direct route"),
        (r"admits TCP 3000–9000 exclusively from\n`agentweaver-preview-gateway` pods — no other source can reach those ports\.",
         "admits TCP 3000-9000 from same-namespace preview Gateway pods. TCP 8088 also has API/worker control allows; the additive rules are not exclusive for every port."),
        (r"6\. \*\*Give the pod run-scoped context\*\*.*?(?=\n7\.)",
         "6. **Give the pod run-scoped context** through one-time `/configure`: identity, workspace descriptors, turn authentication, redeemed Copilot capability **or BYOK**, and purpose-scoped repository/preview/broker credentials. The pod does not fetch ambient user tokens from Key Vault."),
        (r"7\. \*\*Default deny egress\*\*.*?\n",
         "7. **Use the actual network contract:** explicit control/preview ingress and egress exceptions, not a model/worker/git-only allowlist.\n"),
    ],
    "infra-deployment.md": [
        (r"The AKS scripts do not include this retag-unchanged optimization\..*?(?=\n\n)",
         "Retag/import is implemented in `20-build-push-images.mjs` using `az acr import`. The four image identities are API, frontend, MCP and AgentHost; worker uses the API image."),
        (r"Services, Gateway, HTTPRoutes, and backup job\.", "Services, runtime configuration, Gateway and HTTPRoutes."),
        (r"7\. Deployments last\.", "7. API/frontend/MCP deployments, then worker with HPA/PDB."),
        (r"Re-applying the static SecretProviderClass is safe because.*?per run\.",
         "The static SecretProviderClass is reused; the server brokers per-run capability data through `/configure`. AgentHost never fetches user tokens directly from Key Vault."),
        (r"5\. Build or retag all required images.*?\n",
         "5. Build or retag API, frontend, MCP and AgentHost to their desired tags; worker shares the API image.\n"),
        (r"7\. Apply prerequisites before deployments:.*?\n",
         "7. Apply identity, CSI, RBAC, storage, network policies, services and routes before workloads.\n"),
        (r"10\. Close the backup gap.*?\n",
         "10. Protect PostgreSQL, shared workspace files and vault secrets as separate persistence domains.\n"),
        (r"- \*\*API rollout hangs on volume attach:\*\*.*?\n",
         "- **Workspace mount fails:** inspect RWX Azure Files mount options and permissions; current API/worker RollingUpdate does not use the old RWO/Recreate workspace model.\n"),
        (r" The sandbox base image has a narrower context because it is self-contained\.", ""),
    ],
}
changes = []
pending = {}
for filename, rules in RULES.items():
    path = REPO / "docs/deep-dive" / filename
    assert path.relative_to(REPO).as_posix() in PLAN["document_paths"]
    text = path.read_text(encoding="utf-8")
    for pattern, replacement in rules:
        text, count = re.subn(pattern, lambda _: replacement, text, count=1, flags=re.DOTALL)
        if count != 1:
            raise RuntimeError(f"{filename}: expected one match for {pattern!r}; wrote nothing")
        changes.append({"document": filename, "pattern": pattern, "matches": count})
    pending[path] = text
for path, text in pending.items():
    path.write_text(text, encoding="utf-8", newline="\n")
(HERE / "prose-corrections.json").write_text(json.dumps(changes, indent=2) + "\n")
