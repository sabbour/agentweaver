"""One-shot, fail-closed application of the three saved reference research reports."""
from pathlib import Path
import argparse
import difflib
import json
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[4]
PLAN = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/plan-reference.json").read_text())
FILES = {Path(p).name: p for p in PLAN["document_paths"]}
TEXT = {n: (ROOT / p).read_text(encoding="utf-8") for n, p in FILES.items()}
NEWLINES = {n: "\r\n" if b"\r\n" in (ROOT / p).read_bytes() else "\n" for n, p in FILES.items()}
ORIGINAL = dict(TEXT)
CHANGES, ERRORS = [], []


def change(name, needle, replacement, mode="literal", many=False):
    text = TEXT[name]
    positions = [m.start() for m in re.finditer(re.escape(needle), text)]
    if not positions or (len(positions) > 1 and not many):
        candidates = difflib.get_close_matches(needle, text.splitlines(), n=2, cutoff=.35)
        ERRORS.append({"file": name, "needle": needle, "matches": len(positions), "near": candidates,
                       "matching_lines": [s for s in text.splitlines() if needle in s]})
        return
    if mode == "literal":
        updated = text.replace(needle, replacement)
    elif mode == "paragraph":
        start = text.rfind("\n\n", 0, positions[0]) + 2
        end = text.find("\n\n", positions[0])
        if end < 0:
            end = len(text)
        block = text[start:end]
        if block.startswith("|"):
            ERRORS.append({"file": name, "table_requires_row_edit": needle})
            return
        items = list(re.finditer(r"^(?:-|\d+\.) ", block, re.M))
        containing = [i for i in items if start+i.start() <= positions[0]]
        if containing:
            item = containing[-1]
            following = [i for i in items if i.start() > item.start()]
            end = start + following[0].start() if following else end
            start += item.start()
            replacement = item.group() + replacement
            if following:
                replacement += "\n"
        elif block.startswith(":::"):
            replacement = block.split("\n", 1)[0] + "\n" + replacement.lstrip("> ")
        updated = text[:start] + replacement + text[end:]
    elif mode == "line":
        start = text.rfind("\n", 0, positions[0]) + 1
        end = text.find("\n", positions[0])
        if end < 0:
            end = len(text)
        updated = text[:start] + replacement + text[end:]
    else:
        raise ValueError(mode)
    TEXT[name] = updated
    CHANGES.append({"file": FILES[name], "needle": needle, "mode": mode})


def para(name, needle, replacement):
    change(name, needle, replacement, "paragraph")


def line(name, needle, replacement):
    change(name, needle, replacement, "line")


def between(name, start, end, replacement):
    text = TEXT[name]
    if text.count(start) != 1 or text.count(end) != 1 or text.index(end) <= text.index(start):
        ERRORS.append({"file": name, "between": [start, end]})
        return
    a, b = text.index(start), text.index(end)
    TEXT[name] = text[:a] + replacement.rstrip() + "\n\n" + text[b:]
    CHANGES.append({"file": FILES[name], "between": [start, end]})


def section(name, start, replacement):
    text = TEXT[name]
    if text.count(start) != 1:
        ERRORS.append({"file": name, "section": start})
        return
    a = text.index(start)
    level = len(start) - len(start.lstrip("#")) if start.startswith("#") else 2
    following = re.search(rf"\n#{{1,{level}}} ", text[a + len(start):])
    b = a + len(start) + following.start() if following else len(text)
    TEXT[name] = text[:a] + replacement.rstrip() + "\n" + text[b:]
    CHANGES.append({"file": FILES[name], "section": start})


def matching_section(name, pattern, replacement):
    headings = re.findall(pattern, TEXT[name], re.M | re.I)
    if len(headings) != 1:
        ERRORS.append({"file": name, "heading_pattern": pattern, "matches": headings,
                       "headings": re.findall(r"^#{1,3} .*$", TEXT[name], re.M)})
    else:
        section(name, headings[0], replacement)


def rx(name, pattern, replacement, expected=1):
    updated, count = re.subn(pattern, replacement, TEXT[name], flags=re.M | re.S)
    if count != expected:
        ERRORS.append({"file": name, "regex": pattern, "expected": expected, "matches": count})
        return
    TEXT[name] = updated
    CHANGES.append({"file": FILES[name], "regex": pattern})


def link(name, target, label):
    literal = f"({target})"
    if literal in TEXT[name]:
        return
    heading = re.search(r"^# .*$", TEXT[name], re.M)
    if not heading:
        raise ValueError(f"No document title in {name}")
    first_heading_end = heading.end()
    TEXT[name] = (TEXT[name][:first_heading_end+1]
                  + f"\nSee [{label}]({target}) for the shared visual model.\n"
                  + TEXT[name][first_heading_end+1:])
    CHANGES.append({"file": FILES[name], "shared_target": target})


def a2a_and_context():
    n = "a2a.md"
    para(n, "**Every published version of that line is `-preview`**",
         "> The checked-in A2A dependencies remain preview packages on the remote-turn path. The client `Microsoft.Agents.AI.A2A` is pinned to `1.19.0-preview.260822.1`; host packages `Microsoft.Agents.AI.Hosting.A2A` and `.AspNetCore` use `1.11.1-preview.260625.1`. Workflow and Copilot integration packages use stable `1.19.0`. These are repository pins, not a statement about every upstream release.")
    para(n, "Pin a single A2A build",
         "Keep declared package versions and committed lock-file content hashes together. Client and host currently have different version stamps; validate the complete client/host combination before changing either side.")
    change(n, "wrapping the pod's singleton `CopilotAIAgent`",
           "wrapping the pod's singleton `CopilotAIAgent` through a purpose-routing runner. Workflow purposes use the Copilot runner; Operator Assistant uses its MCP chat runner. Provider configuration selects a run-bound Copilot capability or BYOK")
    para(n, "reads only the per-turn `IsRevision`",
         "The bridge reads revision state and applies per-turn system-prompt context, project/agent identity, API address, and API credential before execution. Prompt context is layered over the pod's startup environment and includes the worker-assembled charter, memory, and assigned skills. One-time run provisioning still occurs through `/configure`.")
    para(n, "no anonymous discovery",
         "The card endpoint is `GET {A2APath}/v1/card`. A non-empty `AgentHost:CardBearerToken` requires a matching bearer token; an empty value disables that gate. The options default is empty. `AgentHost:Security:GateCardEndpoint` is not the value the middleware checks. Turn submission instead checks the runtime `TurnBearerToken` delivered through configure. Neither bearer is a TLS identity, and the reviewed implementation does not establish OAuth2 discovery or SPIFFE identity.")
    line(n, "Worktree commit and diff",
         "- Pod-local writable implementation turns prepare Git writeback and return a `PreparedWriteback` DataPart. The worker validates and applies that receipt; not all commit/diff work runs on the shared PVC.")
    para(n, "only when all of H1",
         "H1-H7 describe intended security and operational controls. Their implementation and configuration status differ; distinguish code defaults, Kubernetes base configuration, production-overlay configuration, and requirements not established by a repository-only review.")
    rows = {
        "H1": "Code defaults enable mTLS. Kubernetes base disables it on AgentHost and API/worker callers; the production overlay enables HTTPS and client certificates on both ends. Mounted certificates and pinned CAs are not a claim of SPIFFE identity; the client ignores pod-IP hostname mismatch.",
        "H2": "The A2A NetworkPolicy permits same-namespace API and worker pods to TCP/8088. Policies are additive: preview-gateway ingress permits TCP/3000-9000, including 8088. NetworkPolicy is not a gateway hop.",
        "H3": "Turn submission checks the configured run bearer; card discovery checks the separate CardBearerToken option. Either middleware gate is disabled when its corresponding value is empty.",
        "H4": "Kestrel has declared connection/body/header/keepalive limits. Control calls use a finite timeout; streaming uses an infinite HTTP timeout with worker total/read-idle deadlines. A general transport heartbeat is not established by the configuration field alone.",
        "H5": "Checkpoint management and durable run events remain in the worker/orchestration tier. PostgreSQL checkpoints support cross-replica reads; event persistence deduplicates by run and sequence. This is not A2A replay or a blanket exactly-once guarantee for model/tool effects.",
        "H6": "Ingress does not grant egress. Current sandbox egress includes DNS, explicit platform-service rules, and public HTTPS excluding configured private/link-local ranges; it is not a per-run Git-host-only allowlist.",
        "H7": "Client/host preview versions and lock hashes are pinned separately. in-api is the code fallback; Kubernetes base selects pod-per-run. Startup configuration rollback is not hot reload or a second wire protocol.",
    }
    for key, value in rows.items():
        match = re.search(rf"^\| \*\*{key}[^\n]*", TEXT[n], re.M)
        if not match:
            ERRORS.append({"file": n, "gate": key})
        else:
            old = match.group()
            heading = old.split("|")[1].strip()
            change(n, old, f"| {heading} | {value} |")
    para(n, "`in-api` mode remains the **default**",
         "`in-api` is the code fallback. Kubernetes base API and worker deployments explicitly select `pod-per-run`; production additionally enables mTLS. Configuration files do not establish live deployment or soak status.")
    change(n, "This is the instant, fully-tested rollback for any A2A defect or outage.",
           "This is the in-process workflow execution mode and configuration rollback from remote workflow turns.")
    para(n, "redeploy of a different protocol",
         "Rollback changes `Sandbox:AgentExecutionMode` to `in-api` and deploys/restarts the applicable process configuration. Execution-mode selection is registered during startup; hot switching is not established and no second turn wire protocol is introduced.")
    line(n, "| `AgentHost:KeyVaultUri`", "| `/configure.copilotCredential` | snapshot reference, access token, expiry | Live run-bound Copilot capability, delivered once. Another run or expired capability fails closed; no ambient token-store fallback. |")
    line(n, "KvTokenMountPath", "| `/configure.byokProviderConfiguration` | provider configuration / null | Separate BYOK boundary; BYOK launch does not transmit a Copilot capability. |")
    line(n, "UseSharedTokenStore", "| `/configure.repositoryAccessToken` / `mcpBrokerToken` | optional, purpose-scoped | Separate repository/MCP authorities, not the card bearer or model credential. |")
    para(n, "waits for binding, reads the pod IP",
         "The executor waits for binding, records the claimed pod and turn token, resolves its IP, and probes `/healthz` until the listener is reachable. A warm pod reports `standby`. It then posts one-time `/configure` and awaits setup completion; a ready configured pod reports `ready`.")

    n = "agent-communication.md"
    for old, new in [("inbox_submit", "decision_inbox_submit"), ("inbox_list", "decision_inbox_list"),
                     ("inbox_merge", "decision_inbox_merge"), ("inbox_reject", "decision_inbox_reject"),
                     ("memory_add", "memory_record")]:
        change(n, old, new, many=True)
    para(n, "Active `architectural` and `scope` decisions",
         "Active, approved `architectural` and `scope` decisions are eligible for team-wide context compilation. They remain untrusted historical data, not executable prompt instructions. Coordinator children receive approved-decisions context without the full memory/session selection.")
    para(n, "reads **across all agents**",
         "`memory_search` queries across project agents. Search visibility is not prompt eligibility: another agent's learning or pattern must be high importance, approved, and tagged `cross-team` to enter context selection. Eligible records remain subject to the compiler's item/token budget.")
    change(n, "No subagent work is dispatched until the OutcomeSpec is confirmed.",
           "Interactive `defineOutcome` waits for confirmation. `direct` skips that drafted-outcome gate, while launch Autopilot can confirm `defineOutcome` unattended; dispatch/review/merge boundaries remain.")
    para(n, "`A2ATurnBridgeAgent`",
         "A2A is a leaf-turn transport below team coordination. AgentHost exposes `A2ATurnBridgeAgent` through a purpose-routing runner for workflow execution or the Operator Assistant MCP loop. Its provider boundary may use a run-bound Copilot capability or BYOK; orchestration and checkpoint persistence stay outside the pod.")
    change(n, "../../specs/018-distributed-agent-execution-scaling/spec.md",
           "../deep-dive/a2a-bridge.md#turn-and-event-transport")
    change(n, "Distributed execution spec §4", "A2A turn and event transport")

    n = "memory.md"
    para(n, "four layers, applied in strict priority order",
         "`MemoryContextCompiler.CompileAsync(projectId, agentName)` gathers approved decisions, eligible memory, and the current session. Decisions and session are selected separately. Core memories and eligible learnings/patterns share one importance/recency-ranked item/token budget; source categories are not an unconditional inclusion order.")
    para(n, "They are always included regardless of importance level.",
         "Core memories are eligible regardless of importance, not guaranteed inclusion. They share the ranked memory budget with eligible learnings/patterns. Defaults are 20 items and about 4,000 tokens at four characters per token. Positive call-site overrides precede `MemoryContext:MaxItems` / `MaxTokens`, then legacy `Memory:ContextMaxItems` / `ContextMaxTokens`. Selection stops when the next ranked item exceeds the budget. Decisions and session are outside this memory-item budget.")
    change(n, "After every completed project run, the **Scribe** step runs automatically:",
           "Standalone completion and Coordinator finalization own their Scribe work. Coordinator children stop at `assemble-ready`, bypassing their own review/merge/Scribe stages. Where the final Scribe pass runs, it:")
    link(n, "../diagrams/canonical-memory-context.png", "memory-context selection")

    n = "agent-definition.md"
    change(n, "three targets", "five targets", many=True)
    change(n, "all three generated targets", "all five generated targets", many=True)
    generated = TEXT[n].split("## Generated files", 1)[1].split("\n## ", 1)[0]
    anchor = re.findall(r"^\|[^\n]*\|$", generated, re.M)[-1]
    change(n, anchor, anchor + "\n| `docs/public/agents/agentweaver.agent.md` | Byte-identical anonymous docs download. | Generated agent definition |\n| `apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md` | Byte-identical same-origin deployment download. | Generated agent definition |")
    change(n, "`applyToolMapBlock`, `gen-docs.mjs:192`", "`applyToolMapBlock` in `scripts/gen-docs.mjs`")
    change(n, " (`ProjectService.cs:485`)", "")

    n = "project-skills.md"
    change(n, "All routes are project-scoped under `/api/projects/{id}/skills`.",
           "Catalog and assignment routes use `/api/projects/{id}/skills`; defaults use `/api/projects/{id}/skill-defaults`, project marketplaces use `/api/projects/{id}/skill-marketplaces`, and curated discovery uses `/api/skill-marketplaces`.")
    change(n, "`connected-repo-sync`, `repo-import`, `file-upload`, or `manual`",
           "`built-in`, `connected-repo-sync`, `repo-import`, `file-upload`, `manual`, or `marketplace`")
    change(n, "GitHub/Git/raw SKILL.md sources",
           "`owner/repo`, HTTPS GitHub repository/tree/blob URLs, or HTTPS `raw.githubusercontent.com` URLs pointing directly to `SKILL.md`", many=True)
    TEXT[n] += """

## Defaults and project marketplace sources

| REST action | Contract | MCP tool |
|---|---|---|
| `POST /api/projects/{id}/skill-defaults/preview` | Preview bundled role defaults for a confirmed team/predefined blueprint; return a state-bound digest without writes. | `skill_defaults_preview` |
| `POST /api/projects/{id}/skill-defaults/apply` | Atomically apply the matching preview; stale digest returns `409`. | `skill_defaults_apply` |
| `GET /api/projects/{id}/skill-marketplaces` | Curated and project-added sources. | `skill_marketplace_sources_list` |
| `POST /api/projects/{id}/skill-marketplaces/sources` | Add a project source. | `skill_marketplace_source_add` |
| `DELETE /api/projects/{id}/skill-marketplaces/sources/{name}` | Remove a project-added source, not a curated definition. | `skill_marketplace_source_remove` |

`skill_marketplaces_list`, `skill_marketplace_browse`, and `skill_marketplace_import` discover, preview, and import marketplace candidates. Adding a source does not import or assign skills. Curated names win collisions; project sources cannot shadow them. The import parser's strict HTTPS host rules are distinct from marketplace-source parsing.
"""

    n = "agent-executor.md"
    TEXT[n] = TEXT[n].replace("\n\n", "\n\n> **AX claims unverified in this repository-only review.** Treat AX API, roadmap, isolation, oversubscription, and effort comparisons below as hypotheses requiring dated primary-source validation, not deployed Agentweaver facts.\n\n", 1)
    para(n, "welded to the GitHub Copilot A2A agent",
         "**Provider boundary.** Agentweaver uses A2A for remote AgentHost turns, but supports a snapshot-bound Copilot capability or BYOK provider configuration.")
    para(n, "Agentweaver's AgentHost warm pool",
         "**Scale.** The warm pool requests two standby replicas; this is not a total-run capacity ceiling. Worker HPA is separate, currently two to three replicas. An AX oversubscription advantage remains an unverified hypothesis.")
    para(n, "AX actors must have that PVC mounted",
         "**Workspace contract.** Agentweaver owns branch/assembly/merge semantics. Implementation turns use verified pod-local writable checkouts and prepared Git writeback; assembly Build/Test uses a local read-only checkout. An AX adapter would have to preserve commit/tree verification and writeback, not necessarily mount a shared writable PVC.")
    para(n, "gVisor is a weaker threat model",
         "**Isolation hypothesis.** An adapter must preserve execution isolation and filesystem boundaries. AX runtime defaults, gVisor assumptions, and Kata compatibility require separate dated validation; this review establishes neither an isolation regression nor an effort estimate.")
    para(n, "**h)", "**Run capability boundary.** AgentHost receives one-time configuration containing a live `copilotCredential` or BYOK configuration. Repository/MCP credentials are separate purpose-scoped values. A hypothetical AX activation path must preserve these boundaries without ambient user-token lookup.")


def consumer_links():
    report = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/reference.json").read_text())
    owned = set(PLAN["concept_ids"])
    for doc in report["documents"]:
        for concept in doc["concepts"]:
            if concept["id"] not in owned or concept["disposition"] not in ("reuse", "merge"):
                continue
            target = concept["target"]
            if target == "reference-provider-context-lifecycle":
                link(Path(doc["path"]).name, "./api.md#ai-execution-context", "AI execution context")
            else:
                link(Path(doc["path"]).name, f"../diagrams/{target}.png", concept["title"])


def persistence_and_sandbox():
    n = "scaling-data-layer.md"
    para(n, "It is acceptable to ship Postgres",
         "The PostgreSQL path is implemented: operational stores, memory/orchestration entities, durable run events and workflow checkpoints use the shared database. The SQLite idiom table is migration/background guidance, not an outstanding staged-port plan. Changing providers does not transfer existing data.")
    para(n, "Extending the same columns",
         "These columns describe run leases. Coordinator plan ownership also exists, using `WorkPlan.CoordinatorPodId` and `UpdatedAt`: CAS acquisition protects a fresh owner, heartbeat renews ownership, and loss to a peer fences the dispatch loop. The separate per-subtask fencing/idempotency schema below remains design guidance.")
    change(n, "Workers present it on writes; a stale token is rejected",
           "The lease store checks owner and fencing token for renewal/release and exposes an ownership check; consumers must explicitly use these guards")
    para(n, "The known gap is **subtask dispatch**",
         "Do not confuse shipped plan-level coordinator ownership with a per-subtask dispatch-idempotency transaction. The following SQL is a proposed stronger contract, not the current schema, a migration, or an instruction to execute against production.")
    para(n, "Until that conversion lands",
         "The checked-in deployment already runs multiple replicas. Current plan ownership and heartbeat protections apply; evaluate the proposed per-subtask transaction separately rather than treating coordinator ownership as absent.")
    change(n, "Caller sequences are idempotent on duplicate; auto sequences use `MAX(Sequence)+1` under a serializable transaction and retry update conflicts up to three times.",
           "PostgreSQL appends take a per-run advisory transaction lock and allocate `MAX(Sequence)+1` in a ReadCommitted transaction. Supported transient/conflict failures get at most four attempts. An explicit sequence is idempotent only for a matching event; conflicting content is rejected.")
    para(n, "Terminal events stop a subscription",
         "Subscribers emit the full loaded replay batch before closing on a terminal event, preserving persisted diagnostics after that entry. Retryable `coordinator.assembly_blocked` does not itself close the stream.")
    para(n, "Does **not** claim runs",
         "The checked-in `agentweaver-api` and `agentweaver-worker` Deployments use the same image and start at two replicas with RollingUpdate. The worker sets `App:Role=worker`. API retains HTTP/auth/SSE and sandbox/preview control; shared heartbeat services still perform pickup/reconciliation. This is not an API-enqueue-only separation.")
    para(n, "web tier may drop sandbox RBAC",
         "API retains sandbox and preview responsibilities and their permissions. Removing sandbox RBAC or workspace access would require an explicit product change, not merely a deployment-role setting.")
    TEXT[n] += "\nThe active worker HPA ranges from two to three replicas, targeting CPU 70% and memory 80%. Queue-depth KEDA and an API HPA remain guidance, not active resources in the checked-in manifests.\n"
    para(n, "workers prefer `maxUnavailable: 1`",
         "The worker PDB uses `minAvailable: 1`. A 30-second preStop delay and 120-second termination grace are configured. The five-minute run lease TTL exceeds that grace period; orderly shutdown and lease-expiry recovery are distinct mechanisms.")
    para(n, "Once Postgres is the store, drop that PVC",
         "API and worker no longer mount the SQLite data PVC. The `agentweaver-data` RWO claim remains as a rollback resource, not an active PostgreSQL dependency. Both retain the RWX Azure Files workspace at `/workspace`; worker HOME remains `/workspace/.home`. Implementation children execute in pod-local scratch and publish verified Git writeback to authoritative shared worktrees.")
    para(n, "sandbox pods talk to the worker tier",
         "Sandbox pods do not connect directly to PostgreSQL. API and worker mediate durable state. Both can call AgentHost control/A2A endpoints; AgentHost tools call run-scoped API callbacks. API also retains preview and sandbox lifecycle responsibilities.")
    para(n, "The app exchanges its federated service-account token",
         "Passwordless Entra database authentication is migration guidance, not the current deployment contract. Provisioning generates an administrator password; API/worker consume the `agentweaver-postgres` Secret connection string. Passwordless authentication requires explicit provider/token-refresh and database-role work.")
    TEXT[n] += "\nThe PostgreSQL migrations assembly and init-container bundle path already exist. API/worker invoke `efbundle --postgres-migrations`; sequencing and backfill remain explicit deployment operations, not effects of a provider flag.\n"
    change(n, "Flip to `postgres` to cut over; flip back to `sqlite` to roll back.",
           "Selects persistence provider, without copying state. SQLite rollback requires an explicit restore/data plan and single-writer deployment; it does not preserve PostgreSQL leasing.")
    change(n, "The phases map onto a flag-reversible AKS rollout.",
           "This is historical migration guidance, not an unshipped-feature checklist or a promise of flag-only reversal. Current manifests include PostgreSQL, two API/worker replicas, RollingUpdate, and worker HPA. Cutover and rollback require explicit state handling.")
    change(n, "web falls back to the in-process path behind the role flag.",
           "Role selection and `Sandbox:AgentExecutionMode` are separate controls; scaling workers to zero is not a verified execution-topology rollback.")
    change(n, "#checkpointing-durable-resume", "#checkpointing--durable-resume")
    line(n, "../architecture-aks.md", "- [Infrastructure & deployment deep dive](../deep-dive/infra-deployment.md)")

    n = "sandbox-pods.md"
    change(n, "**releases** the pod back to the warm pool",
           "**releases** the claim and deletes the used pod so the pool replenishes capacity; an active preview can defer release")
    change(n, "pods never persist past the run.",
           "a pod can outlive execution while an active preview retains it; release and orphan cleanup resume after durable retention evidence expires.")
    change(n, "with run/user/token/KV secret context plus the workspace descriptor",
           "with run identity, a live Copilot capability or BYOK configuration, separate purpose-scoped credentials, and the execution-workspace descriptor")
    para(n, "node.executionPodName ?? globalPodName",
         "`GET /api/system/runtime` reports the host/API pod, not a fallback execution attribution for coordinator children. Child and workflow nodes use topology `executionPodName` or null; an unbound child must not be labelled as executing on the API pod.")
    line(n, "no `CapabilityTokenService`",
         "| Capability boundary | The API's `GitHubCapabilityBroker` fences immutable purpose-bound snapshots before and after redemption. AgentHost receives the bounded capability through `/configure`, without ambient Key Vault/filesystem user-secret lookup. |")
    section(n, "A **preview port-forward**", """With `Sandbox:Preview:Enabled=true`, the API creates Gateway-direct HTTPS routing to the run's sandbox and returns `preview_url` and `keepalive_url`. Browser traffic bypasses the API. See the [sandbox browser preview reference](./sandbox-browser-preview.md) for authorization, approval, publication, keepalive and stop semantics.

Viewer can list previews; Contributor/Owner can start, retry, keep alive or stop them. Legacy non-project runs retain submitting-principal ownership; trusted internal agent callbacks are explicitly scoped exceptions.

When preview creation is disabled, the operator start route uses the legacy `kubectl` implementation. This is not an automatic fallback after Gateway publication failure.

| Disabled-preview fallback | Scope |
|---|---|
| Bound pod required | Uses the process's `PodNameRegistry`; a local executor without a Kubernetes pod has nothing to forward. |
| `kubectl port-forward --address 127.0.0.1 pod/{pod} :{targetPort} -n {namespace}` | Binds loopback on the **API host**. A remote browser's localhost is not that host. |
| `local_port` | No public preview URL; arrange an appropriate local/operator connection separately. |
| Ports / caps | 1-65535; defaults 3 per run, 20 per service process. Gateway has its own configured allowed range. |
| Lifetime | Process-local, no persisted route annotations or session TTL; stop, exit, disposal or run/pod cleanup ends it. |

![Gateway-direct preview: API control, shared Gateway, HTTPRoute, per-preview Service, sandbox forwarder and app](../diagrams/sandbox-browser-preview-fig1.png)

<!-- Canonical target: ../diagrams/src/sandbox-browser-preview-fig1.drawio.
     The deep-dive-execution owner maintains its export and review evidence. -->
""")
    line(n, "| Cluster API access", "| Cluster API access | Current pod infrastructure enables service-account automount; the model-execution sidecar masks `/var/run/secrets/kubernetes.io/serviceaccount`. The whole pod is not universally tokenless. |")
    rx(n, r"^\| Egress \|[^\n]*$", "| Egress | Default-deny with explicit API/MCP/DNS paths and public HTTPS excluding private/link-local ranges; not a per-run Git-host-only allowlist. No direct PostgreSQL access. |", 2)

    n = "sandbox-setup.md"
    line(n, "| Key Vault URI", "| Run capability delivery | One-time `/configure` with a live snapshot-bound `copilotCredential` or BYOK configuration; repository capability is separate. Sandbox identity is not permission to read user secrets. |")
    line(n, "`gitHubAccessToken`",
         "| AgentHost cannot authenticate | Verify that the immutable run capability is live, API-side redemption succeeds, and `/configure` carries `copilotCredential` in Copilot mode plus a separate `repositoryAccessToken` when needed. BYOK uses its provider configuration. Missing/expired capability fails before readiness; identity federation is not a user-secret fallback. |")
    change(n, "releases it on completion/suspend", "releases it on completion/suspend unless active-preview retention defers cleanup")

    n = "sandbox-browser-preview.md"
    para(n, "exists and the caller owns it",
         "Calls resolve the persisted run's project: mutations require Contributor/Owner, listing Viewer. Legacy non-project runs retain submitting-principal ownership; the agent-start endpoint explicitly allows a trusted internal callback.")
    change(n, "owner-only", "Contributor/Owner (legacy run ownership where applicable)", many=True)
    change(n, "owner OR its own agent callback", "Contributor/Owner or the explicitly permitted trusted internal callback", many=True)
    change(n, "guarded by ownership", "guarded by run authorization", many=True)
    change(n, "30 minutes by default", "1440 minutes / 24 hours by default", many=True)
    change(n, "defaults migration-safely to 30 minutes", "defaults to 1440 minutes (24 hours)")
    line(n, "| `403 Forbidden`", "| `403 Forbidden` | Insufficient run/project access, or preview approval denied. |\n| `408 Request Timeout` | Agent-preview approval expired; response identifies the expired request and retry availability. |")
    para(n, "Its canned prompt tells the agent",
         "Build & Test evaluates build/tests only. Coordinator assembly subsequently invokes platform-owned `PreviewStep` on the retained sandbox for APPROVED and REQUEST_CHANGES results, not DECLINED. It resolves a command, maps cwd to the effective checkout, starts a supervised process, observes its port, obtains approval and publishes through Gateway.")
    TEXT[n] += "\nGateway routes use a per-preview ClusterIP Service selecting the run's pod. Browser data bypasses the API; the API owns authorization and routing lifecycle.\n"
    change(n, "Retain the preview after the run completes / pod is released",
           "Keep routing after completion while a live preview defers backing-pod release; expiry, stop and missing-pod reconciliation still bound cleanup")

    n = "live-preview-provisioning.md"
    section(n, "Both teardown paths now consult", """Release, orphan reaping and preview activity use `ReconcilePreviewLifecycleAsync(runId)`. Durable HTTPRoute annotations determine run-level state and idempotent retention changes.

| State | Durable evidence | Sandbox effects |
|---|---|---|
| `PreviewActive` | At least one route has both idle and maximum expiry in the future. | Extend backing claim TTL and set pod `safe-to-evict=false`; release/reaping defer. |
| `Previewable` | No qualifying route, unavailable client/run identity, or lookup failure. | Restore normal TTL and `safe-to-evict=true`; normal cleanup can proceed. |

Cleanup reads cluster state even if creation is disabled in this process. The backing pod need not exist for a retention decision. Protection patches are best-effort, not a guarantee against infrastructure loss.

Both supported claim-name candidates are merge-patched without removing sibling fields. Active retention uses service-level `LifetimeMinutes * 60 + 600`; inactive state restores `Sandbox:Kubernetes:TimeoutSeconds` (default 600). Stop/expiry reconcile after route deletion: another live route retains the pod; removing the final one reverses protection.
""")
    change(n, "30 minutes by default", "1440 minutes / 24 hours by default", many=True)

    n = "cluster-diagnostics.md"
    change(n, "Non-AKS deployments return `404 Not Found`.",
           "Without a Kubernetes client the endpoint remains available: Kubernetes checks report unknown/unavailable conditions and inventories can be empty. Topology returns unavailable graph/layer results.")
    text = TEXT[n]
    updated, count = re.subn(r',\s*"resource_graph"\s*:\s*\{\s*"nodes"\s*:\s*\[\],\s*"edges"\s*:\s*\[\],\s*"layers"\s*:\s*\[\]\s*\}', "", text)
    if count != 1:
        ERRORS.append({"file": n, "resource_graph_sample_matches": count})
    else:
        TEXT[n] = updated
    line(n, "| `resource_graph`", "| `details` | `TopologyResourceDetailsDto` | Optional bounded cluster-root metadata. |")
    section(n, "## Kubernetes resource graph contract", """## Relationship topology

Relationships come from `/api/diagnostics/cluster/topology`, not a `resource_graph` member on the runtime snapshot. Supported layers are `runtime`, `networking`, `workloads`, `storage`, `autoscaling` and `availability`. Requested layers report `available`, `partial` or `unavailable`; omitted layers report `not_requested`. Bounded discovery retains the limits of 100 objects per type, 250 nodes and 500 edges.
""")
    line(n, "| `404 Not Found`", "")


GRAPH_CONTRACT = """Coordinator graph descriptors combine work-plan topology with persisted status. Nodes may include `status`, `status_reason`, and `terminal_stage`; subtask state also arrives through `coordinator.topology`. Selected-workflow assembly gates become `kind: "live"` when reached, even though their stable IDs start with `planned:assembly-`. Failure projection uses the terminal stage so failure-scribe does not mark never-run gates as executed. Delegated plans leave skipped nodes planned with delegated status.

Leaf subtasks connect to the first selected gate (or merge if none); the gates form a chain followed by merge and Scribe. Each selected gate has a coordinator loopback, excluded from forward degree calculations. Fixed gate lists are examples for a particular workflow, not a universal RAI-only pipeline."""

LAUNCH_CONTRACT = """Coordinator launch has three relevant cases:

| Launch | Behavior |
|---|---|
| `defineOutcome`, Autopilot off | Draft/persist a spec, then wait for confirmation or revision. |
| `defineOutcome`, launch Autopilot on | Draft, then confirm unattended through the normal seam on behalf of the accountable user. |
| `direct` | Persist a confirmed prompt-backed spec and plan directly, without a model-drafted outcome or confirmation RequestPort. |

Confirmation advances into selection, decomposition, dispatch, steering and collective assembly; it is not orchestration completion. Direct mode and Autopilot do not remove workflow review/merge requirements or grant arbitrary tool permissions."""

ROLE_CONTRACT = """Persisted project-scoped runs inherit their stored project's access: Viewer permits inspection; Contributor permits run control, review, approval, questions and steering; Owner permits project administration. Authorization uses persisted `ProjectId`, not caller-supplied context or the submitting-user string. Legacy runs without a project retain submitting-user ownership; dangling project references fail closed. Unauthorized ordinary-run SSE access returns `404` to hide existence."""


def contracts_and_web():
    n = "api.md"
    para(n, "Only the submitting user may access their own runs", ROLE_CONTRACT)
    change(n, "Use owner-scoped `/api/runs/{id}`", "Use project-role-authorized `/api/runs/{id}`")
    change(n, "Requires a valid bearer key and run ownership",
           "Requires valid authentication and Viewer access to the run's persisted project (legacy run ownership otherwise)")
    change(n, "Only the run owner may submit a decision.",
           "Submitting a decision requires Contributor access to the run's persisted project, or legacy run ownership.")
    para(n, "All project endpoints are caller-owned",
         "Projects are role-based: creation establishes ownership and listing returns caller-visible projects. Inspection requires Viewer; supported operational mutations/orchestration require Contributor; administration, including provider settings, role assignments, rename and deletion, requires Owner. Personal Assistant sessions retain their separate caller-ownership rules.")
    change(n, "owner-scoped like any other run", "authorized from its persisted project like other project runs")
    change(n, "Owner-scoped", "Run-authorized (Viewer for inspection; Contributor for mutation; legacy ownership otherwise)", many=True)
    change(n, "owner-scoped", "run-authorized", many=True)
    para(n, "The server resumes from that point in the in-memory event buffer",
         "Set `Last-Event-ID` to the last per-run sequence received. SQLite uses `SqliteRunEventStream`; PostgreSQL uses `EfRunEventStream`. SSE serves a retained local entry when available, otherwise durable replay-and-tail, including another producer's events. Restart or local-entry eviction does not erase persisted history.")
    para(n, "After a process restart, the in-memory event history is lost",
         "Durable subscribers drain the full loaded replay batch before terminating, including persisted diagnostics after a terminal event. `coordinator.assembly_blocked` is not terminal. A `done` frame ends a connection, not necessarily a parked or human-gated run.")
    para(n, "not persisted to SQLite",
         "Run events persist through `IRunEventStream`; `RunStreamStore` also maintains local delivery state. `/events` supplies persisted events, `/stream` supplies streaming/replay, and `/history` is separate persisted session history. The final-result `agent.message` fallback is legacy compatibility for completed runs lacking event rows, not the normal restart contract.")
    para(n, "shape-only", GRAPH_CONTRACT)
    change(n, '{ "targetPort": 3000 }', '{ "target_port": 3000 }')
    para(n, "Starts a port-forward session from a random local port",
         "With `Sandbox:Preview:Enabled=true`, start provisions Gateway-direct routing through a per-preview HTTPRoute and ClusterIP Service to the sandbox, returning `preview_url` and `keepalive_url`. With preview disabled it starts `kubectl port-forward` on API-host loopback; that address is not automatically reachable from a remote browser. Contributor starts/stops; Viewer lists. The `target_port` request first validates 1-65535; Gateway also applies its configured allowed range. The `pf-*`/`local_port` response below describes only the disabled-preview fallback.")
    change(n, '"define_outcome"', '"defineOutcome"', many=True)

    n = "coordinator.md"
    fields = TEXT[n].split("### Outcome spec fields", 1)[1].split("\n## ", 1)[0]
    matching_section(n, r"^## The Phase 1.*$", "## Launch modes and outcome definition\n\n<a id=\"the-phase-1-outcome-spec-flow\"></a>\n\n" + LAUNCH_CONTRACT + "\n\n### Outcome spec fields" + fields)
    para(n, "No work begins before confirmation", LAUNCH_CONTRACT)
    matching_section(n, r"^#{2,3} .*Workflow selection.*$", """### Workflow selection

Workflow selection is trigger-agnostic. Resolve the project default as outer exception fallback, then load all valid workflows, ordered with that default first and then by ID. Honor a resolvable explicit request override, otherwise the backlog override; an unavailable explicit ID is logged and selection continues. Honor conversational `use <workflow-id>` feedback against the complete available set.

Zero/one candidate avoids model selection. With multiple candidates, use process-fit selection and persist/emit its rationale. After decomposition, validate compatibility: explicitly selected code-producing workflows without Build & Test are honored with a warning; automatic selections are reselected or replaced by a suitable platform fallback.

The model gets one attempt and one retry. Unusable/ambiguous output falls back to an available default/standard, then a non-code-review candidate, then the first candidate. An outer exception retains the resolved project default.

Automation uses Schedule and Event triggers, not Manual/Heartbeat eligibility. `triggers` is an ordered array; `trigger` is its first-entry compatibility alias. Automation admits backlog work independently of process selection.

![Workflow selection: valid workflows, explicit and conversational overrides, process-fit selection, bounded fallback, and compatibility checks](../diagrams/canonical-workflow-selection.png)

<!-- Canonical editable source: ../diagrams/src/canonical-workflow-selection.drawio; maintained by the shared owner. -->
""")
    change(n, "## Boundaries and Decisions", "## Untrusted Project Context Data", many=True)
    para(n, "injected as the `## Untrusted Project Context Data`",
         "Child workers receive charters plus active, approved architectural/scope decisions from `CompileDecisionsAsync`. Stored context is emitted as an `agentweaver.untrusted-context.v1` JSON envelope under `## Untrusted Project Context Data`, never trusted instructions. This path excludes the full memory/session stack.")
    change(n, "./web.md#coordinator-orchestration-and-topology-view",
           "./web.md#coordinator-orchestration-and-unified-graph-view")
    change(n, "mermaid flow", "shared workflow-selection diagram", many=True)
    TEXT[n] += """

## Launch versus heartbeat-pickup defaults

Omitted API/MCP launch options default to false. Persisted project pickup defaults are separate: `pickup_autopilot=true`, `pickup_auto_approve_tools=true`, `max_ready_per_heartbeat=3`. Each claim snapshots the current values. The Coordinator heartbeat defaults enabled at 10 seconds, independently of approval/provisioning wait heartbeats.
"""

    n = "events.md"
    change(n, "`agent`, `rai`, `assemble-ready`", "`agent`, `assemble-ready`", many=True)
    change(n, "shape-only", "state-bearing", many=True)
    TEXT[n] += "\n" + GRAPH_CONTRACT + "\n"
    para(n, "In Phase 1 the coordinator run terminates after confirmation",
         "The spec becomes confirmed and `confirmedBy` identifies the accountable confirmer, including unattended normal-seam confirmation. Confirmation advances orchestration rather than inherently emitting `run.completed`; Direct skips the drafted-spec gate.")
    para(n, "SSE stream closes with a `done` frame after this event",
         "Outcome-definition and review waits are nonterminal. A connection may close at a gate; use run/spec state and reconnect with the last per-run sequence. Do not require `done` immediately after `coordinator.outcome_spec`: local SSE closure checks review-requested state, whereas durable subscribers use terminal events.")
    para(n, "every terminal assembly path",
         "Work-plan status and run terminal status differ. `assembly_blocked` can park a recoverable assembly and does not inherently terminate SSE. Actual terminal outcomes emit terminal events; durable replay drains its loaded batch before closing. Budget exhaustion escalates to `in_review` at human review, not terminal steering-budget exhaustion.")
    change(n, "#terminal-failure-diagnostics", "#failed-run-diagnostics")
    change(n, "#tool-approval-pending-heartbeat-issue-212", "#toolapproval_pending-heartbeat-issue-212")
    TEXT[n] += "\nThe SSE envelope sequence is the per-run replay cursor. The `seq` inside `coordinator.topology` is a separate topology snapshot/delta counter, not a substitute for `Last-Event-ID`.\n"

    n = "mcp.md"
    change(n, "This page documents each tool's full parameters and return shape.",
           "This page documents selected parameters and workflows. The generated [tool index](./mcp-tools.md) is the complete name/description catalog; exposed tool schemas define current complete parameter contracts.")
    para(n, "covers `web_fetch` only",
         "Safe-tool auto-approval covers `web_fetch` and `start_preview`. Preview skips the human wait only: port, process-liveness, run/sandbox access and publication validation remain. Grants emit `tool.auto_approved`; arbitrary shell, destructive, privileged, secret-bearing and unrelated network permissions are not granted. Immutable launch policy survives retry and is inherited by children.")
    change(n, "Tasks progress through Backlog → Ready → Active, with terminal states of Done, Failed, and Archived.",
           "Task state is Backlog, Ready or Claimed. A claimed card projects its run into Problems, Human Review, Active or Done. Archiving hides eligible items; it is not another task-state enum value.")
    TEXT[n] += """

## Cross-surface launch and provider contracts

""" + LAUNCH_CONTRACT + """

`run_task` defaults to Direct; `coordinator_start` defaults to defineOutcome; `run_submit` is a legacy Direct Coordinator alias, not the removed standalone REST route. Autopilot auto-answers questions and, when set at launch in defineOutcome, confirms the draft unattended; it does not grant tool permissions. Heartbeat pickup defaults are separate from false-by-default explicit launches.

MCP prepares AI context internally, rejects unresolved providers, and forwards its `execution_key` as `If-Model-Provider-Key`. The forwarding key is not a public tool parameter. Workflow responses include ordered `triggers` plus first-trigger alias `trigger`; writes still use complete workflow YAML generation/save rather than a dedicated structured trigger-edit tool.
"""

    n = "project-generation-model-settings.md"
    line(n, "`CoordinatorRunService.ActivateAsync`",
         "| `outcome_spec_generation_model` | `CoordinatorRunService` passes the project override into `CoordinatorDraftInput`; `CopilotCoordinatorSpecDrafter` applies it or the resolved generation default. | `CoordinatorRunService`, `CopilotCoordinatorSpecDrafter` |")
    TEXT[n] += "\nModel selection order is `project override → per-flow Generation setting → Generation.Model → gpt-5.6-sol`. These are generation-flow settings, not run-model pins. Syntactic model-family validation does not prove provider availability or executability.\n"

    n = "repo-blueprint-suggestions.md"
    section(n, "## Related", """## Related repository picker

Suggestion input (`owner/repo` or supported URL) is heuristic input, not cloning/project-creation authority. The picker browses the caller's Repo App capability and submits a short-lived repository selection code:

| Method | Route | Contract |
|---|---|---|
| GET | `/api/github/repository-selections` | Safe installation/repository browse metadata from the live capability. |
| POST | `/api/github/repository-selections` | Verify `repository_full_name`; mint caller-bound, expiring, single-use code. |
| POST | `/api/projects` | Consume `repository_selection_code`; resolve clone metadata/credentials server-side. |
""")
    line(n, "`has_issues`",
         "| Repository metadata | Name, description and topics contribute substring-mapping text; `has_issues` contributes display signal, not a keyword match. Rules run in order without an LLM. |")
    TEXT[n] += "\nHTTP failures, service timeouts and unavailable repository metadata return the template fallback; caller-requested cancellation propagates.\n"

    n = "resilient-assembly-review.md"
    line(n, "reason=commit_failed_persistent",
         "| `run.failed` (child) | `{ message, errorCode, retryable }` | Persistent commit failure terminalizes the child; internal exception/lock evidence is not the public payload. |")
    para(n, "the `run.failed` event",
         "The following fields describe internal `ChildTurnFailedOutput.Evidence`/lock diagnostics. Public persistence/replay normalize `run.failed` to `{ message, errorCode, retryable }`; raw exception/lock evidence is not its payload. Authorized diagnostic projections have separate contracts.")

    n = "unified-steering.md"
    para(n, "single-agent squads with no eligible rotation author",
         "A released pod can make a child non-resumable, choosing fresh dispatch over in-place steering. Lack of another author does not itself force escalation: with accumulated feedback/context, a fresh same-author run can preserve prior work without changing lockout. Without context, escalate to human review. Autonomous budgets still bound retries.")
    para(n, "Assembly may surface this as `assembly_blocked`",
         "When autonomous budgets exhaust, the decider chooses proceed and assembly escalates durably to `in_review`, stage review, with the run awaiting review. It does not latch terminal assembly-blocked. Human request-changes resets the autonomous budgets as a new supervised mandate; `HumanReviewRoundTrips` is telemetry, not a cap.")
    change(n, "`child_executor_failed:{executor}`",
           "the bounded `{ message, errorCode, retryable }` public failure contract", many=True)

    n = "web.md"
    matching_section(n, r"^#{2,3} .*Configuration.*$", """## Configuration

`API_URL` is the API origin without `/api`. Runtime `window.__AGENTWEAVER_CONFIG__.API_URL` precedes `VITE_API_URL`; an explicit empty runtime value means same-origin. The client adds one `/api` to XHR paths, while auth redirects use origin-root `/auth/entra/*`.

| Setting | Meaning |
|---|---|
| `VITE_API_URL` | Build-time origin fallback. Runtime configuration takes precedence. |
| `VITE_API_KEY` | Not consumed by the current browser client; session authentication is used. |
""")
    line(n, "| `VITE_API_KEY`", "| Browser authentication | Session authentication; the current client does not consume `VITE_API_KEY`. |")
    section(n, "### GitHub sign-in",
         "### Product sign-in\n\nProduct sign-in uses Microsoft Entra ID through `SignInPage` and `AuthGate`; the client sends session authentication. GitHub Repo App and Copilot App are purpose-specific capabilities, not alternative product identities. Start work through Coordinator orchestration or backlog pickup.")
    para(n, "collects a single **Goal** field",
         "The start dialog accepts a goal, optional workflow and launch automation. Direct starts planning from the prompt; outcome definition uses draft/confirm. Both call orchestration and navigate to the Coordinator run.")
    para(n, "getBezierPath",
         "Forward edges use `SpineEdge`: stepped orthogonal routes from `buildSteppedConnectorRoute` and `buildBridgedOrthogonalPath`, with arrow markers, crossing bridges and computed junctions. This is not a two-Bezier-segment router.")
    TEXT[n] += """

## Current navigation and action boundaries

Routes include `/sessions`, `/settings`, `/assistant`, platform-admin-only `/platform-settings`, project skills/cluster/observability, and `/projects/:projectId/team/:agentName/memory`. Legacy project-session and global-observability URLs redirect.

Project creation uses Repo App browse → selection code → server-authorized creation, not arbitrary repository-URL authority. Start through Coordinator; `/api/projects/{id}/runs` is retired (`410`), not an agent-specific submission path.

Safe-tool auto-approval covers `web_fetch` and `start_preview`; Autopilot handles clarifying questions and launch-time outcome confirmation. Human assembly buttons require a human-review gate, not every assembly-review-requested event. Steer only server-reported steerable runs; terminal failed/declined runs are not universally amendable in place. Child questions/approvals target the actual child. Preview failure remains visible independently of the Build/Test verdict.
"""


def precision_and_integrity():
    n = "a2a.md"
    para(n, "The agent card advertises", "Card discovery and turn execution use distinct configured application-layer gates.")
    para(n, "The card's bearer/OAuth2 scheme", "Bearer authorization and TLS identity are independent controls. The deployment matrix in H1 distinguishes plain base configuration from production-overlay mTLS; neither establishes a SPIFFE integration.")
    change(n, "**Authz-gated, not anonymous.**", "A non-empty `CardBearerToken` enables its separate bearer gate.")
    change(n, "pin one exact known-good build", "pin the exact validated client/host combination")
    change(n, "| `in-api` *(default)*", "| `in-api` *(code fallback)*", many=True)
    line(n, "| `AgentHost:CardBearerToken`", "| `AgentHost:CardBearerToken` | token / empty (code default) | Non-empty requires a matching bearer on `v1/card`; empty disables the card gate. |")
    # Keep searchable wire contracts beside the new per-turn-context explanation.
    change(n, "One-time run provisioning still occurs through `/configure`.",
           "One-time run provisioning still occurs through `/configure`.\n\nStreaming returns assistant text and in-band `RunEvent` DataParts (`application/x-agentweaver-run-event+json`). Writable pod-local turns can also return a `PreparedWriteback` descriptor, captured separately from events.")
    rx(n, r"(?<=`Authorization: )[^`\n]+(?=`)", "Bearer <turn-token>", 2)
    notes = re.search(r"^[^\n]*Notes on the gates.*?(?=\n## )", TEXT[n], re.M | re.S)
    if notes:
        change(n, notes.group(), "Notes on the gates:\n\n- Bearer tokens and mTLS are independent controls; label the configured deployment rather than asserting universal mTLS.\n- Listener limits and worker total/read-idle streaming deadlines are distinct.\n- Checkpoints and durable event sequencing are platform-owned, not pod persistence.\n")
    rx(n, r"^\| `Sandbox:AgentHost:RequireMtls`[^\n]*$", "| `Sandbox:AgentHost:RequireMtls` | true in code / production overlay; false in base | Configure host and API/worker client transport consistently. |")
    rx(n, r"^\| `Sandbox:AgentExecutionMode` \| `in-api`[^\n]*$", "| `Sandbox:AgentExecutionMode` | in-api code fallback; pod-per-run in Kubernetes base | Startup selection, changed through deployment configuration. |")

    n = "agent-communication.md"
    change(n, "Active, approved `architectural`",
           "`decision_update` accepts `status` (active, superseded, archived), replacement `content`, and `superseded_by_id`.\n\nActive, approved `architectural`")

    n = "agent-definition.md"
    rx(n, r"(OK:[^\n]+is in sync\.\n)(```)", r"\1OK: docs/public/agents/agentweaver.agent.md is in sync.\nOK: apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md is in sync.\n\2")

    n = "agent-executor.md"
    table = ORIGINAL[n][ORIGINAL[n].index("| Component |"):].strip()
    for old, new in [
        ("Fixed ×2 standby per cluster", "Two standby pods; separate worker HPA"),
        ("Unchanged; PVC mounted into AX actor", "Preserve verified source and writeback contracts"),
        ("**Blocked** — AX HITL is roadmap-only", "Agentweaver-native; AX support unverified"),
        ("gVisor default, or Kata runtime class", "Isolation compatibility to validate"),
        ("API brokers per-user token in `/configure`; sandbox has no KV access (issue #471)",
         "One-time run capability or BYOK; separate repository/MCP credentials"),
        ("Same brokered path, on AX worker pods", "Adapter must preserve capability boundaries"),
        ("| Effort |", "| Unverified AX effort estimate |"),
    ]:
        table = table.replace(old, new)
    section(n, "### Effort estimate and assessment",
            "### Assessment: hypothesis only\n\nAn AX spike must validate lifecycle, recovery, provider-capability delivery, event persistence, verified workspace writeback and isolation. DAG dispatch, assembly, review, steering and persistence remain Agentweaver responsibilities unless an adapter proves otherwise. The repository establishes neither a two-run ceiling nor lack of cross-replica durable checkpoints. AX performance and effort claims require dated primary evidence.\n\n" + table)

    n = "memory.md"
    rx(n, r"```\nLayer 1.*?```", "```text\nDecisions: active, approved architectural/scope records, ordered by creation time\nMemory candidates: eligible own core context + learnings/patterns\nSelection: importance, then recency; one bounded item/token budget\nSession: most recent open session, selected separately\n```")
    TEXT[n] += "\nRuntime tools `record_memory`, `submit_inbox_entry`, `update_session` and `export_memory` correspond to public MCP `memory_record`, `decision_inbox_submit`, `session_update` and `memory_export`.\n"

    n = "coordinator.md"
    change(n, "**outcome spec** before any work begins.", "**outcome spec** when outcome-definition mode is selected; Direct plans from the prompt.")
    change(n, "(in later phases)", "then", many=True)
    change(n, "and (in later phases)", "and then", many=True) if "and (in later phases)" in TEXT[n] else None
    para(n, "No subagent work is dispatched",
         "The confirmation gate applies to interactive defineOutcome, not Direct. Launch Autopilot can confirm unattended through the normal seam. Review/merge and tool-permission boundaries remain independently enforced.") if "No subagent work is dispatched" in TEXT[n] else None

    n = "api.md"
    para(n, "These endpoints back the Kubernetes sandbox preview feature",
         "These run-scoped endpoints manage browser previews. Gateway-direct HTTPS is primary when enabled; kubectl forwarding is only the disabled-preview API-host loopback fallback.")
    change(n, "a non-owner receives `404`", "an unauthorized caller receives `404`")
    change(n, "Non-owners receive `403 Forbidden`.", "Insufficient access returns `403 Forbidden`.", many=True)
    change(n, "Required contract for the Start Task dialog.", "Optional launch mode; `define_outcome` remains an accepted compatibility alias.")
    change(n, "without generating or confirming an outcome spec.",
           "without model-drafting or pausing to confirm an outcome; a confirmed prompt-backed spec is still persisted.")
    change(n, "runs a collective RAI pass over the aggregate diff",
           "runs the selected workflow's automated gates over the aggregate diff")
    TEXT[n] += "\nWork-plan status examples are not exhaustive: delegated, assembly_steering, rai_blocked and needs_resolution also exist. The prepared `execution_key` authorizes a matching operation/scope and is checked against caller, expiry and provider identity; a provider fingerprint is provenance, not authority. Accepted context is revalidated before model use, separately from run snapshots and capabilities.\n"
    line(n, "- PLANNED collective-assembly nodes", "- Assembly nodes are resolved from the selected workflow. Stable `planned:assembly-*` IDs become live when reached; optional status/reason/terminal-stage fields describe persisted execution. Merge and Scribe follow the gates.")
    line(n, "- Edges: `coordinator`", "- Edges connect Coordinator to root subtasks, prerequisite to dependent subtasks, and each leaf to the first selected assembly gate (or merge). Gates chain into merge and Scribe. Every selected gate has a Coordinator loopback; forward degree/cardinality excludes those direct loopbacks.")

    n = "coordinator.md"
    para(n, "before a human confirms the outcome spec",
         "Interactive defineOutcome pauses for the named accountable human. Direct skips that drafted-outcome gate; launch Autopilot confirms unattended on behalf of the accountable user. UI and MCP expose the same confirmation/revision seam.")

    n = "events.md"
    change(n, "When a human confirms the drafted OutcomeSpec and the coordinator run proceeds",
           "When the drafted OutcomeSpec is confirmed through the normal seam, interactively or by launch Autopilot")
    change(n, "No decomposition or child dispatch occurs before a human confirms — the run blocks here until the confirm or revise seam is called.",
           "Interactive defineOutcome waits here for confirm/revise; launch Autopilot can confirm unattended. Direct skips this drafted-spec gate.")
    para(n, "runtime status is NOT baked",
         "The descriptor includes optional persisted status/reason/terminal-stage fields. It is a `GraphDescriptor` with `variant: coordinator`, a Coordinator start node and `coordinator:{coordinatorRunId}` graph ID. Topology snapshot/delta events remain a separate projection.")
    line(n, "- PLANNED collective-assembly chain", "- Selected-workflow assembly gates precede merge and Scribe. Their stable `planned:assembly-*` IDs become live as stages execute, with persisted status/reason/terminal-stage fields.")
    line(n, "- Edges: `coordinator`", "- Coordinator connects to roots; prerequisite subtasks connect to dependents; leaves connect to the first selected gate (or merge). Every selected gate has a Coordinator loopback excluded from forward-degree/cardinality calculations. `coordinator.topology` remains available alongside the unified descriptor.")

    n = "sandbox-browser-preview.md"
    change(n, "Owner-only retry", "Contributor/Owner retry")
    change(n, "targetPort", "target_port", many=True)
    TEXT[n] += "\nWith preview enabled (AKS default), start returns `preview_url` and `keepalive_url`; disabled-preview kubectl forwarding returns API-host loopback `local_port`. Gateway publication failure does not automatically select kubectl. Approval timeout returns 408 and preserves the supervised process for retry; published lifetime and approval timeout are separate limits.\n"
    line(n, "| `Sandbox:Preview:ZoneSuffix`", "| `Sandbox:Preview:ZoneSuffix` | Empty until deployment sets it | Managed zone used by `{token}-preview.{ZoneSuffix}`; read current deployment configuration, not an example cluster hostname. |")

    n = "sandbox-pods.md"
    line(n, "| Reversibility", "| Reversibility | Change `Sandbox:AgentExecutionMode` to in-api through the normal configuration rollout; startup DI wiring is not hot reloaded. |")
    rx(n, r"^\|[^\n]*500m[^\n]*$", "| Resources | AgentHost: requests 300m CPU/1Gi, limits 800m/2Gi. Execution sidecar: requests 700m/2Gi, limits 1200m/4Gi. Each requests 1Gi and limits 4Gi ephemeral storage; shared execution scratch is capped at 8Gi. |")
    rx(n, r"^\| `podName`[^\n]*$", "| `podName` | API/host pod identity, not fallback attribution for a Coordinator child. |")
    rx(n, r"^\| `executionPodName`[^\n]*$", "| `executionPodName` | Authoritative bound execution pod for this run/node, or null. |")

    n = "mcp.md"
    para(n, "The Coordinator agent drafts", LAUNCH_CONTRACT)

    n = "web.md"
    para(n, "The `HomePage` submit form", "The standalone submit/watch route is retired. Start work through Coordinator orchestration or backlog pickup; the dialog navigates to the Coordinator run.")
    para(n, "no dispatch occurs before a human confirms", "Interactive defineOutcome waits for confirm/revise; Direct skips the drafted-spec gate and launch Autopilot can confirm unattended. Review/merge and tool permissions remain separate boundaries.")
    line(n, "- **Planned assembly nodes**", "- **Assembly nodes** use stable `planned:assembly-*` IDs, become live when reached, and display persisted status/reason/terminal-stage. The selected workflow determines the gate chain rather than a fixed muted RAI-only pipeline.")
    needle = "RunSubmitForm.tsx"
    a = TEXT[n].rfind("```", 0, TEXT[n].index(needle))
    b = TEXT[n].index("```", TEXT[n].index(needle)) + 3
    change(n, TEXT[n][a:b], """```text
apps/web/src/
  App.tsx                    route declarations and access gates
  config.ts                  runtime/build-time API origin
  components/
    StartOrchestrationDialog.tsx
    OutcomeSpecPanel.tsx
    WorkflowGraphPanel.tsx
    AgentSessionPanel.tsx
  pages/
    SignInPage.tsx
    SessionsPage.tsx
    SettingsPage.tsx
    SkillsPage.tsx
    ClusterPage.tsx
```

Coordinator and Assistant route boundaries, plus project observability views, are wired from `App.tsx`.""")
    legacy = re.findall(r"^#{2,4} .*(?:new run|submit.*watch|submit.*run).*$", TEXT[n], re.M | re.I)
    for heading in legacy:
        section(n, heading, heading + "\n\nUse [Start an orchestration](#start-an-orchestration). Standalone project-run submission is retired.")
    TEXT[n] = re.sub(r"^.*\*\*New Run\*\*.*\n", "", TEXT[n], flags=re.M)

    for name, text in TEXT.items():
        links = re.findall(r"^See \[([^\n]+)\]\(([^\n]+)\) for the shared visual model\.\n", text, re.M)
        if len(links) > 1:
            text = re.sub(r"^See \[[^\n]+\]\([^\n]+\) for the shared visual model\.\n\n", "", text, flags=re.M)
            title = re.search(r"^# .*$", text, re.M)
            compact = "Shared references: " + "; ".join(f"[{label}]({target})" for label, target in links) + ".\n"
            text = text[:title.end()] + "\n\n" + compact + text[title.end():]
            TEXT[name] = text

    # Preserve frontmatter, containers, tables and final newlines in the proposed patch.
    for name in TEXT:
        TEXT[name] = TEXT[name].rstrip() + "\n"


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--apply", action="store_true")
    args = p.parse_args()
    a2a_and_context()
    persistence_and_sandbox()
    contracts_and_web()
    precision_and_integrity()
    consumer_links()
    if ERRORS:
        print(json.dumps(ERRORS, indent=2, ensure_ascii=False))
        raise SystemExit(1)
    print(f"{len(CHANGES)} operations; {sum(TEXT[n] != ORIGINAL[n] for n in TEXT)} documents")
    diff = "".join("".join(difflib.unified_diff(ORIGINAL[n].splitlines(True), TEXT[n].splitlines(True),
                   fromfile=FILES[n], tofile=FILES[n])) for n in TEXT if TEXT[n] != ORIGINAL[n])
    (Path(__file__).parent / "proposed-document-changes.diff").write_text(diff, encoding="utf-8")
    residuals = []
    suspects = re.compile(r"NOT baked|always.*planned|not currently routed|HomePage|RunSubmitForm|GitHubSignIn|fixed.*ceiling|no redeploy|gitHubAccessToken|30 minutes|planned:assembly|human confirms|before a human|three targets|Production value|KeyVault|ResolveInvocationKind|shape-only|two.*loopback|globalPodName")
    for name, text in TEXT.items():
        if name == "mcp-tools.md":
            continue
        for index, value in enumerate(text.splitlines(), 1):
            if suspects.search(value):
                residuals.append({"file": name, "line": index, "text": value})
    (Path(__file__).parent / "reference-docs-residuals.json").write_text(json.dumps(residuals, indent=2)+"\n")
    if args.apply:
        for name, value in TEXT.items():
            if value != ORIGINAL[name]:
                if name == "mcp-tools.md":
                    raise RuntimeError("Generated document requires out-of-scope source changes")
                (ROOT / FILES[name]).write_text(value, encoding="utf-8", newline=NEWLINES[name])
        (Path(__file__).parent / "document-changes.json").write_text(json.dumps(CHANGES, indent=2)+"\n")
