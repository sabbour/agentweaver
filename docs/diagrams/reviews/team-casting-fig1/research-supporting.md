# Supporting research: git, memory/decisions, team casting

## Scope and provenance

- Researcher: independent GPT-6 Astra researcher 3 of 3.
- Worktree: `.worktrees\drawio-diagram-authoring`, HEAD `f10738018`.
- Read `CONTRIBUTING.md`'s AI contribution process, the supplied area plan, and the owned page/concept/diagram entries in `deep-dive-orchestration.json`.
- **Facts** below come from current implementation, configuration, model definitions, and inspected test assertions. Existing images and diagram sources were not inspected or used as implementation evidence.
- **Proposals** are the content groupings and node/edge models for the parent's authoring pass, not new runtime requirements.
- **Assumptions / handoffs** are explicitly marked. This researcher did not trace coordinator dispatch, review arbitration, workflow binding, or steering. Those belong to the other two researchers.
- Existing changes were already present in all three pages; patches preserve them. No branch/index/stash mutation, commit, runtime execution, rendering, shared-asset editing, or global reconciliation was performed.

## Dispositions enacted

| Consumer/concept | Disposition and medium |
| --- | --- |
| `git-integration-fig1` | Retain path; redesign content-isolation graphic, connecting each producer to candidate content and guarded merge. |
| `git-integration-fig2` | Retain path; redesign git-content assembly, not another complete coordinator lifecycle. |
| Git lifecycle | Retain inline state notation; prose now identifies `CommittingCandidate` as an operation, not a stored status. |
| Git capture, remote boundary, recovery | Correct prose; no new bitmap. |
| `memory-decisions-fig1` | Retain path; redesign governance with verified authorship and eligibility gates. |
| `memory-decisions-fig2` | Retain path; redesign provenance-aware de-collision with repeated availability checks. |
| `memory-decisions-fig4` | Retain path; redesign provider-neutral, asymmetric mirror. |
| `canonical-memory-context` | Reuse shared graphic; no shared asset edits. Exact selection filters now have a local table. |
| Memory entity/state models | Retain inline ER/state notation, with conceptual-ownership and provenance qualifications in adjacent prose. |
| Memory operations | Retain prose; no CRDT or backup graphic. |
| `team-casting-fig1`, `team-casting-fig2` | Retain semantic identity: compiler overview and summary-only analysis boundary answer distinct questions. |
| `team-casting-fig3` | Retain path; redesign guarded confirmation with ordered, non-atomic side effects. |
| Casting class model | Retain inline notation; default-only review-policy meaning clarified in prose. |
| Naming, charters, persistence, sync, blueprint application | Retain/correct prose and algorithms; no additional bitmap. |
| Casting memory | Replace duplicated import/export description with a link to the memory page's canonical model; retain casting-specific seed behavior. |

No owned bitmap was deleted: the supplied report retains/redesigns all eight owned images and reuses the ninth shared image. No new bitmap is proposed.

## Source keys

The following keys expand to repository-relative paths. Every `key:line-range` below is a file:line citation using this expansion.

| Key | File |
| --- | --- |
| `G` | `apps\Agentweaver.Api\Git\WorktreeManager.cs` |
| `GI` | `apps\Agentweaver.Api\Git\ProjectGitInitializer.cs` |
| `C` | `apps\Agentweaver.Api\Casting\CastingService.cs` |
| `CP` | `apps\Agentweaver.Api\Casting\CastProposalStore.cs` |
| `ECP` | `apps\Agentweaver.Api\Infrastructure\Ef\EfCastProposalStore.cs` |
| `BP` | `apps\Agentweaver.Api\Blueprints\BlueprintService.cs` |
| `M` | `apps\Agentweaver.Api\Memory\MemoryContextCompiler.cs` |
| `D` | `apps\Agentweaver.Api\Endpoints\DecisionsEndpoints.cs` |
| `ME` | `apps\Agentweaver.Api\Endpoints\MemoryEndpoints.cs` |
| `A` | `apps\Agentweaver.Api\Security\RunAuthorship.cs` |
| `DP` | `apps\Agentweaver.Api\Memory\DecisionPromotion.cs` |
| `S` | `apps\Agentweaver.Api\Runs\PostRunScribeService.cs` |
| `LE` | `apps\Agentweaver.Api\Memory\MemoryLedgerExporter.cs` |
| `FE` | `packages\Agentweaver.Squad\Memory\SquadMemoryExporter.cs` |
| `FI` | `packages\Agentweaver.Squad\Memory\SquadMemoryImporter.cs` |
| `DB` | `apps\Agentweaver.Api.Data\Memory\MemoryDbContext.cs` |
| `AM` | `apps\Agentweaver.Api.Data\Memory\AgentMemory.cs` |
| `SC` | `packages\Agentweaver.Squad\Analysis\ProjectSignalScanner.cs` |
| `UA` | `packages\Agentweaver.Squad\Naming\UniverseAllocator.cs` |
| `CC` | `packages\Agentweaver.Squad\Squad\CharterCompiler.cs` |
| `SW` | `packages\Agentweaver.Squad\Squad\SquadWriter.cs` |
| `CM` | `packages\Agentweaver.Squad\Model\CastingModels.cs` |
| `BM` | `packages\Agentweaver.Squad\Model\Blueprint.cs` |
| `SYNC` | `packages\Agentweaver.Squad\Sync\SquadGitScribe.cs` |
| `PR` | `packages\Agentweaver.AgentRuntime\Workflow\OpenPullRequestTurnExecutor.cs` |
| `TC` | `tests\Agentweaver.Tests\Casting\ScenarioCastingTests.cs` |
| `TM` | `tests\Agentweaver.Tests\Memory\MemoryContextCompilerSecurityTests.cs` |
| `TA` | `tests\Agentweaver.Tests\Memory\MemoryAuthorshipEndpointsTests.cs` |
| `TG` | `tests\Agentweaver.Tests\Git\SquadStateMergeTests.cs` |

## Proposed surviving diagram content models

Each table supplies both node identity and edge semantics. Tables describe **content**, not a rendered layout. Do not add arrows not supported by these contracts.

### `git-integration-fig1`: isolated candidates, shared repository history

**Question:** How can independent runs produce separately reviewable content and merge it back safely?

Use two parallel candidate lanes, A and B. Repeat the same proven lane rather than inventing different behavior for B.

| Nodes / edge | Evidence |
| --- | --- |
| Repository with originating branch -> resolved starting commit | `G:130–165`: resolve branch tip before worktree provisioning. |
| Starting commit -> `agentweaver/{runId}` branch + isolated worktree | `G:77`, `G:145–191`: deterministic branch/path and one-step `git worktree add`. |
| Worktree -> agent-edited candidate files -> capture | `G:676–730`: capture reads worktree status and stages changed, non-ignored paths; represent the agent as the external producer, not a new execution subsystem. |
| Capture -> candidate tree hash | `G:685–701`: unchanged candidate returns existing tree; otherwise commit returns tree SHA. |
| Origin tree + candidate tree -> full diff | `G:800–810`: compare both branch tips' trees. |
| Candidate tree -> externally reviewed/approved tree identity | `G:1902–1919`: merge accepts `expectedTreeHash` and rejects a mismatch. Review UI/arbitration detail is deliberately outside this figure. |
| Approved identity + originating branch -> guarded merge | `G:1922–1953`: containment check and checked-out/ref-only routing. |
| Guarded merge -> originating branch advanced and checkout aligned | `G:2019–2028`: fast-forward hard reset when checked out; normal three-way path follows. |
| Guarded merge -> conflict/block, not success | `G:1914–1919`, `G:1991–2014`, `G:2034–2039`: mismatch, dirty-state block/reconcile, genuine merge conflict. |

**Authoring notes:** Label review nodes “Approved candidate tree,” not raw `ReviewA`/`ReviewB` identifiers. Connect every lane through capture and diff. Do not depict `CommittingCandidate` as a database status. No direct “agent -> origin” write arrow.

### `git-integration-fig2`: committed prerequisite handoff and aggregate git content

**Question:** How do isolated child candidates contribute to one assembled content tree?

| Nodes / edge | Evidence |
| --- | --- |
| Origin tip -> reset/create integration branch | `G:850–878`. The branch is assembled headlessly; stale checked-out references are handled before reset. |
| Ordered child branch inputs -> accumulator | `G:881–905`: caller supplies order; missing/empty branches skip, already-contained tips skip, eligible tips fast-forward or merge. |
| Tree conflict + merge base -> overlay later child's changes | `G:906–954`: apply the child's delta from merge base, including deletions/renames; not simply “choose the entire child tree.” |
| Auto-resolved overlay -> recorded resolution + continue | `G:949–958`: two-parent commit plus branch/file resolution record. |
| Tree conflict + no merge base -> assembly conflict result | `G:918–924`: no successful partial aggregate returned. |
| Successful accumulation -> integration ref, aggregate tree hash, diff, auto-resolution list | `G:971–986`. |
| Integration candidate -> prerequisite containment test | `G:483–507`: `BranchContains` validates the candidate commit is reachable; branch-tip presence alone is insufficient. |
| Verified starting commit -> new child branch/worktree | `G:153–184` proves generic provisioning from a selected branch tip. **Handoff:** the coordinator's actual choice/publication calls must be confirmed by researcher 1. |
| Aggregate candidate -> workflow-authored gates -> approved hash -> final merge | `G:1902–1919` proves approved-hash input; actual authored gate routing is researcher 1/2's responsibility, not independently traced here. |

**Cross-area handoff, not firsthand evidence:** The owned audit points to `apps\Agentweaver.Api\Coordinator\CoordinatorDispatchService.cs:1212` and `:2710` for integration-prerequisite dispatch and isolated local child checkout/publication. Its correction was applied to the owned git page, but the parent must attach researcher 1's verified call-site evidence before treating those arrows as fully researched. Do not draw shared mutable child worktrees or a fixed RAI -> human gate order.

### `memory-decisions-fig1`: governance, authorship, and prompt eligibility

**Question:** Which observations can become accepted project policy or useful memory?

| Nodes / edge | Evidence |
| --- | --- |
| Human caller / internal run caller -> author resolution | `A:40–89`: human identity or verified run capability, exact project scope, agent-name consistency. |
| Verified author -> pending inbox | `D:49–62`, `D:130–149`: contributor check, authorship, persisted source fields. |
| Verified author -> pending agent memory | `ME:125–160`: type/importance validation, normalized tags, source provenance, pending trust. |
| Pending inbox -> owner/verified-Coordinator approval -> approved active decision | `A:18–37`, `D:211–224`, `DP:22–55`: authorize, transaction, promote/link. |
| Pending inbox -> authorized rejection -> retained rejected row | `D:285–301`: status update, not delete. |
| Run-scoped low-risk inbox -> post-run Scribe -> approved active decision | `S:42–104`: project, agent, pending state, creation window, source run ID, and learning/pattern/update type filters. |
| Same verified Coordinator's architectural/scope inbox -> finalization backstop -> approved decision | `DP:65–98`: exact run ID and Coordinator agent/type filters; transaction. No generic “all pending boundaries” arrow. |
| Active approved architectural/scope decision -> compiler | `M:58–65`. Active status alone does not qualify. Low-risk promoted decisions do not automatically join the boundaries prompt layer. |
| Same-agent non-legacy core/high learning/pattern -> compiler | `M:68–90`. Other-agent memory requires approved trust, high importance, learning/pattern type, and whole `cross-team` tag. |
| Current open session -> compiler | `M:95–101`; post-run Scribe can append its outcome to the session at `S:110–129`. |
| Selected data -> explicitly untrusted JSON envelope | `M:175–217`; trust determines eligibility, not permission for embedded instructions. |
| Ledger -> file mirror | `LE:37–83`, `FE:31–44`. Show this as a separate output, not the database's authority source. |

**Tests inspected:** `TA:17–99` verifies absent/forged run capabilities fail and server-resolved provenance persists. `TM:23–69` verifies injection strings remain JSON data; `TM:73–103` proves cross-team approval gating; `TM:107–160` proves legacy exclusion until approval.

### `memory-decisions-fig2`: provenance-aware slug allocation

**Question:** Which submission updates an existing row, and which receives a fresh slug?

| Nodes / edge | Evidence |
| --- | --- |
| Authorized, verified submission -> lookup `(project, requested slug)` | `D:49–70`. |
| No existing row -> create pending row with requested slug | `D:72`, `D:130–152`. |
| Existing row -> same-agent test | `D:76–77`, case-insensitive. |
| Same agent + terminal row -> explicit HTTP conflict | `D:78–79`, before any provenance-match update. |
| Same agent + pending + matching source kind AND identity -> update existing row | `D:81–103`. |
| Different agent, or pending same-agent with different provenance -> `slug--agent-segment` | `D:64–66`, `D:114–116`. |
| Candidate exists -> increment `--2`, `--3`, ... -> test candidate again | `D:117–126`. The back-edge must go to availability checking, not directly to creation. |
| Candidate free -> insert pending row | `D:130–152`. |
| Insert -> unique database backstop | `DB:93–94`; database prevents duplicate `(project, slug)` values. |
| Racing collision -> insert failure, no transparent retry | `D:151–152` has no insert-retry or uniqueness-to-409 mapping. Do not label this as guaranteed HTTP 409. |

The stale implementation comment at `D:109–113` says a unique index is future work; the actual current model at `DB:94` wins. Comments are not a substitute for inspecting configuration.

### `canonical-memory-context`: shared selection contract, reused only

**Question:** What stored data is eligible for compilation?

| Nodes / edge | Evidence |
| --- | --- |
| Active approved architectural/scope decisions -> decisions list | `M:58–65`. |
| Same-agent non-legacy core context -> memory candidates | `M:68–75`. |
| Same-agent high-importance non-legacy learning/pattern -> memory candidates | `M:79–91`. |
| Other-agent high-importance approved learning/pattern + whole cross-team tag -> candidates | `M:79–91`. |
| Candidate set -> importance descending, recency descending -> bounded selection | `M:114–140`. Core entries share this ranking; no absolute core-first guarantee. |
| Item/token budget -> bounded selection | `M:15–17`, `M:45–54`: defaults 20 items/4,000 approximate tokens; selected memories only. |
| Latest open session -> session payload | `M:95–101`. |
| Decisions + selected memories + session -> untrusted JSON | `M:175–217`. |
| No eligible decisions/memories/session -> no compiled block | `M:103–104`. |

No direct “all memories -> prompt” or “approved content -> trusted instructions” arrow. Parent owns any shared-canonical edits. Local table now makes these restrictions explicit even if the bitmap has not yet been replaced.

### `memory-decisions-fig4`: one authority, asymmetric file exchange

**Question:** Which files are exports, and which files can create proposals on import?

| Nodes / edge | Evidence |
| --- | --- |
| Provider-neutral memory store -> eligibility-filtered DTOs | `LE:37–83`; `DB:63–74` includes operational entities, so do not draw universally separate databases. Annotate SQLite's `memory.db`, not a mandatory file. |
| Active approved decisions -> `.squad/decisions.md` | `LE:45–51`, `FE:47–66`. |
| Pending inbox -> `.squad/decisions/inbox/{slug}.md` | `LE:53–55`, `FE:68–80`; old pending markdown is removed before rewriting. |
| Selected non-legacy learning/update -> agent `history.md` | `LE:57–63`, `FE:84–105`. |
| Latest open session -> `.squad/identity/now.md` | `LE:66–71`, `FE:108–116`; no write when session is absent. |
| Active approved architectural/scope -> `boundaries.md` | `LE:45–51`, `FE:119–139`. |
| Approved pattern memories -> `patterns.md` | `LE:57–61`, `FE:142–157`. |
| Inbox markdown only -> parser -> missing-slug pending row | `FI:20–71`, `ME:528–548`: parse four front-matter keys, skip malformed files, leave existing slugs alone. |
| Imported pending row -> review required, not approved decision | `ME:538–543`, `DP:22–55`; import does not set approval/provenance to an authenticated run. |
| Explicit export -> file write + committed ledger or error | `ME:484–503`. Incidental export failure logs via `LE:90–113`. |

No import arrows from `decisions.md`, histories, session focus, boundaries, or patterns. Do not depict all six exports as bidirectional stores. Agent-memory record/promote endpoints do not synchronously export (`ME:158–163`, `ME:209–214`).

### `team-casting-fig1`: draft first, confirmation owns writes

**Question:** How does an intent become a named, persisted team?

| Nodes / edge | Evidence |
| --- | --- |
| Scenario/manual/model-assisted intent -> resolved roles | `C:125–150`, `C:245–283`, `C:614–646`; models cannot persist arbitrary invented role IDs as recognized roles. |
| Blueprint roster -> manual proposal -> confirm new | `BP:357–366`; review policy is only default at `BP:113–115`. |
| Existing state -> policy + registry + history | `C:150–173`; model-assisted path `C:472–493`. |
| Policy/history/override/seed -> selected universe | `C:176–190`, `UA:23–52`. Signals do not feed this node. |
| Universe + reserved names + role count -> allocated names | `C:192`, `UA:59–106`, case-insensitive reservation and `member-N` overflow. |
| Allocated names + trusted role metadata -> compiled charters | `C:194–217`, `CC:21–51`. |
| Proposed roster/charters/current revision -> pending proposal store | `C:219–232`, `CM:37–47`; stores are short-lived with best-effort persistence, not necessarily in-memory only. |
| Proposal -> reject/remove or expire -> no `.squad/` mutation | `C:889–894`, `CP:69–81`; `TC:100–123` checks rejection creates zero Squad files. |
| Proposal -> confirmed current-revision intent -> roster/built-ins | `C:899–1033`; full guard belongs in confirmation detail, not an unexplained bypass. |
| Roster -> workspace files/events -> canonical state | `C:1042–1181`, `SW:135–157`. |
| Completed confirmation -> best-effort seed + `.squad/` auto-commit | `C:1182–1208`; seeds currently retain legacy trust, so do not promise immediate prompt memory. |

**Boundary wording:** “No `.squad/` writes before confirmation,” not “no state changes”: generating a proposal stores it, and model-assisted casting also writes run metadata (`C:554–581`, `C:689–700`).

### `team-casting-fig2`: analysis summary, not raw source

**Question:** What repository information influences role selection?

| Nodes / edge | Evidence |
| --- | --- |
| Repository -> bounded scanner | `SC:14–22`, `SC:38–53`, `SC:127–175`: 500-file cap, excluded directories/filenames, reparse-point skip. |
| Scanner -> language/framework/test/docs/CI/size signals | `SC:55–103`. Framework detection may read local manifests; the model-bound object is the summary. |
| Signals -> text summary | `SC:106–124`. |
| Summary + catalog role menu -> fenced analysis prompt | `C:511–530`; universe already selected independently at `C:497–508`. |
| No signals -> general-purpose prompt + warning | `C:521–535`. This is not a scan failure. |
| Model result -> parse role selections | `C:614–623`. |
| Parse failure -> failed proposal generation | `C:615–622`. |
| Parsed IDs -> catalog resolution -> skip unknown IDs | `C:625–637`. |
| No recognized IDs -> failed proposal generation | `C:639–645`. |
| Resolved roles -> deterministic names/charters -> stored proposal | `C:647–689`. |

No repository source payload arrow to the model. No role-selection -> universe-scoring edge. A valid subset of selected catalog IDs may survive even when some returned IDs were unknown.

### `team-casting-fig3`: guarded ordered confirmation

**Question:** What is checked before writes, and which side effects are only best-effort?

| Nodes / edge | Evidence |
| --- | --- |
| Proposal ID -> pending proposal lookup -> missing/expired exit | `C:905–907`; `CP:69–81`, `ECP:70–83`. |
| Existing-team flag + caller intent -> resolve new/augment/recast or require choice | `C:911–922`. |
| Captured `TeamRevision` -> acquire team-mutation lease | `C:924–927`; `C:87–99` maps lease failure to team-change conflict. |
| Current team existence != proposal flag -> conflict, no writes | `C:928–930`; writer created only at `C:932`. `TC:66–97` verifies revision conflict and absent `.squad/`. |
| Valid guard -> read current team/registry -> apply intent | `C:933–1010`. |
| Augment -> keep existing + add new names, retire none | `C:946–969`. |
| Recast -> desired proposal roster + absent old names retired | `C:970–998`. |
| New -> proposal roster + old roster recorded as retired | `C:999–1010`. Do not silently substitute the recast semantics. |
| Final roster -> ensure Scribe/Ralph/Rai/Coordinator | `C:1015–1033`. |
| Roster -> team/routing/support files, charters, histories, alumni | `C:1042–1113`. |
| Roster changes -> registry/history events -> canonical JSON + coordinator file | `C:1118–1181`, `SW:135–157`. |
| Writes -> complete mutation lease -> remove proposal | `C:1182–1184`. No global atomic filesystem transaction is implied. |
| Confirmation -> seed core memory/session, best-effort | `C:1190–1192`, `C:1218–1283`. Model defaults `AM:15–18` leave seeds legacy until approved. |
| Confirmation -> `.squad/` auto-commit, best-effort failure logged | `C:1194–1207`. Do not imply this commits every written support file outside `.squad/`. |

Tests additionally inspect successful confirmation producing `team.md` at `TC:37–62`.

## Inline surviving models

### Git lifecycle

Retain the existing inline lifecycle as a **conceptual** model. Candidate capture is `G:676–701`; approved-tree merge branches are `G:1902–1953`; dirty block and final conflict branches are `G:1991–2039`. The graph's persisted review/commit/revision/recovery transitions belong to researcher 1's review evidence; no claim was made to re-verify that state machine in this supporting pass. The page now explicitly distinguishes the conceptual `CommittingCandidate` operation from persisted `committing`.

### Memory ER model

Nodes are Decision, DecisionInboxEntry, AgentMemory, SessionContext, and conceptual Project ownership. `DB:81–104` enforces decision supersession, inbox-decision FK, and project/slug uniqueness; `DB:126–127` enforces project/session uniqueness. `ProjectId` scopes records but does not establish every drawn ownership edge as a foreign key. The existing supersession association is conceptual: `SupersededById` points from the older decision to its replacement. Do not infer a cascade policy from the diagram.

### Memory inbox state

- Submit -> Pending: `D:130–152`.
- Pending -> Pending only with same agent, slug, source kind, source identity: `D:76–103`.
- Pending -> Merged + approved Decision/link: `D:211–224`, `DP:22–55`.
- Pending -> Rejected, row retained: `D:285–301`.
- Same-agent replay of a merged/rejected requested slug -> conflict: `D:78–79`.

**Parent authoring task:** the current inline self-loop still uses an abbreviated “same agent + same slug” label. Adjacent prose now explicitly supplies source equality; update the literal edge label during the parent's diagram pass. No Mermaid source was authored by this researcher.

### Casting class model

`BM:20–45` supports Blueprint and roster role references/bespoke definitions. `CM:8–46` supports Role, ProposedMember, CastProposal, Team, and CastMember with the composition/reference edges. `CM:60–86` supports RegistryMember, CastingRegistry, CastSnapshot, CastHistory. Review-policy metadata is default-only (`BP:113–115`), explicitly qualified in surrounding prose. Keep TeamRevision out of this conceptual identity picture if the detailed confirmation graphic owns concurrency.

## Corrections actually made

### Git page

1. Initial blank repository commit normally contains seeded `.gitignore`; it is not universally empty (`GI:71–100`).
2. Candidate lanes now explicitly connect edits -> commit/tree/diff -> approved identity -> guarded origin merge.
3. `CommittingCandidate` identified as a conceptual operation.
4. Removed shared-mutable-child-worktree claims in overview, trade-off, and gotcha.
5. Removed “all integration conflicts stop / all-or-nothing” absolute; documented later-child delta overlay, no-merge-base failure, missing/contained branch skips, resolution records.
6. Replaced fixed collective RAI/human implication with authored-gate abstraction.
7. Removed “opening PRs out of scope.” Scoped PR action exists; no automatic push inferred (`PR:111–141`).
8. Corrected ref-only trade-off and rebuild guidance: never use it for a dirty checked-out originating branch.
9. Documented current tracked-content auto-commit before checked-out merge safety (`G:1968–1986`, `G:2180–2234`).
10. Documented final merge's origin-preferred Squad bookkeeping while preserving real application conflicts (`G:2031–2039`; `TG:39–79`, `TG:120–148`).

### Memory page

1. Provider-neutral memory store; SQLite separate-file arrangement is not universal (`DB:63–74`).
2. ER ownership marked conceptual; actual optional FKs distinguished.
3. Pending update requires matching source kind/identity; same-agent different-provenance pending submissions de-collide.
4. Every numbered candidate rechecks availability; unique index is real despite stale source comment.
5. Racing insert is not promised a 409 or transparent retry.
6. Promotion described as an actual caller-owned transaction, not merely “transactional in spirit.”
7. Exact compiler eligibility table; core memory shares importance/recency ranking; budget covers memories only and stops at first over-budget candidate.
8. Provider-neutral mirror, patterns export included, no bidirectional-file assumption.
9. Export approval/trust filters explicit.
10. Agent memory record/promote do not synchronously export; incidental and explicit export failure behaviors distinguished.
11. API-created memory starts pending; this does not incorrectly characterize legacy-default internal seeding.

### Casting page

1. Proposal generation leaves `.squad/` unchanged, rather than claiming absolutely no writes/state changes.
2. Blueprint review-policy field validated as default-only, including conceptual-model explanation.
3. Proposal TTL and provider-specific persisted/cached stores documented.
4. TeamRevision lease plus existence guard added before file writes.
5. Detailed confirmation ordering, proposal removal, best-effort memory and auto-commit; no global transaction claim.
6. Clarified new versus recast retirement behavior.
7. Replaced duplicate import/export prose with canonical memory-model link.
8. Seeded core-memory rows retain current legacy defaults and therefore are not immediately compiler-eligible; this is an observed limitation, **not** a request to change trust.
9. Sync stages only `.squad/`, but `repo.Commit` consumes the existing index. Documentation no longer guarantees that unrelated pre-staged content cannot be committed (`SYNC:60–101`). Runtime code unchanged.

## Validation and limits

- `git diff --check -- docs\deep-dive\git-integration.md docs\deep-dive\memory-decisions.md docs\deep-dive\team-casting.md` passed after edits.
- Read-only static validation passed for balanced fences and existing local link targets in all three pages, and for 172 cited line ranges resolving within 29 source files. The initial range check caught three overlong endpoints; they were corrected and the check rerun successfully.
- Inspected test assertions are evidence, **not tests executed**. Runtime/workflow execution and rendering were explicitly excluded; no application tests or docs build were run.
- Parent must perform authoring/render validation, diagram visual review, and the final docs build.
- Parent must attach researcher 1's actual coordinator call-site evidence for isolated child publication/prerequisite dispatch, and researcher 1/2's evidence for authored gates and review lifecycle. Supporting git helper contracts are verified here; this report does not pretend those prove their entire caller chain.
- A direct sibling evidence handoff was attempted, but `write_agent` rejected the synchronous sibling (“write_agent only supports background agents”). No additional agent was launched; cross-area questions remain clearly identified for the parent.
- No new runtime defect was fixed or ticket/workflow created. Legacy seeded-memory eligibility and sync's pre-existing-index caveat are documented as current behavior.

## Paths changed by this researcher

- `docs\deep-dive\git-integration.md`
- `docs\deep-dive\memory-decisions.md`
- `docs\deep-dive\team-casting.md`
- `docs\diagrams\reviews\team-casting-fig1\research-supporting.md`
