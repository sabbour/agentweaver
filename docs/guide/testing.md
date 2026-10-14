# Testing

The v1 tests use in-memory providers, fake Azure SDK transports, and disposable
PostgreSQL. They do not require an Azure subscription.

Pure release checks verify the applied P1 source versions for 23 product components
at `1.0.0`: 13 packages and 10 service images.
They keep the acceptance-only Probe's prepared `0.0.1` source version separate.
They verify the canonical receipt and archived source notes.
They check exact dependency compatibility and confirm that version application changes
only version fields and release records, not runtime code or external package pins.
The local checks do not publish artifacts, apply SQL, or prove deployed behavior.
The local deployment-tool tests validate the exact v1 P0 target, release-stage
manifest, source-bound publication result, ten digest-pinned GHCR service
images, and nine shared Deployment/Service pairs. They also validate the
receipt-measured AgentHost image, platform, and byte-count pin supplied to
Environment. Fake
transports prove that the default dry-run issues no write command, GHCR
verification uses the registry HTTP API without Docker/Podman, and resume still
repeats target, provenance, and live health checks. These local contract tests
do not contact GitHub, GHCR, Azure, or Kubernetes or prove
that a published release or complete runtime manifest currently exists.
Workflow source checks verify job-local package permission, the approved GitHub
Packages feed, and the exact `ghcr.io/sabbour` destination guard. They execute
both destination guards locally with accepted and rejected values. They do not
authenticate a token, publish a package, prove package-write access, or prove
that published images are publicly pullable.
Release build-input checks bind the three image references and all 467 dependencies to the approved source.
They reject source drift, wrong image digests, an isolated builder, and dependency integrity failures before packing.
A builder fixture uses standard `docker buildx inspect default` text output and rejects missing, duplicate, or unexpected driver and endpoint fields.
A workflow source check requires the pinned Docker setup action on the `platform_only` path.
It also requires `set-host: true`, which makes the action export its local socket through `DOCKER_HOST` before input preparation. The check rejects attempts to update the reserved `default` context.
These source checks do not start Docker or prove runner storage behavior.
A local npm fixture checks a standard locked install followed by offline cache consumption for both existing integrity algorithms.
These checks run no product image build or publication.
CI comparison regressions use real controlled Git commits and genuine plan/application receipts.
They cover the integrated PR merge, ordinary post-merge push, and unchanged linear push.
They reject forged, stale, unrelated, or changed archive evidence and mismatched event history.
Extra product edits present only in the integrated tested tree still need fresh source notes.
Both CI workflows retain that integrated checkout and its exact coverage identity.

Fixed-child store cases compile with the PostgreSQL suite. They check a null
Open WorkPlan link and a recovered checkpoint association, not runtime permission.
The runtime-owner context matcher separately checks the confirmed Fixed step.
Compilation does not prove PostgreSQL execution.

Two pure tests read the embedded Core and Sessions update-guard migrations.
They check the exact five tables, unconditional rejection, and unchanged deletion rules.
Five PostgreSQL cases check restricted-role inserts, row locks, update rejection, and trigger-disable rejection.
Another 25 PostgreSQL cases check startup rejection of missing, disabled, wrong-function, column-limited, and conditional triggers.
These 30 PostgreSQL cases compile but have not run for this source change.
Checkpoint corruption and size-limit cases insert malformed fixtures directly.
Decision-version cases append new decisions instead of changing saved rows.

Each restricted-role case grants `SELECT`, `INSERT`, and `UPDATE` on only the column in the table.
The column grant permits row locks; the trigger rejects all updates, including an unchanged value.

| Owner migration | Table | Test role's only `UPDATE` column |
| --- | --- | --- |
| Core `021` | `coordinator_decisions` | `decision_id` |
| Core `021` | `maf_workflow_checkpoints` | `checkpoint_id` |
| Sessions `008` | `sessions` | `session_id` |
| Sessions `008` | `session_provider_bindings` | `run_id` |
| Sessions `008` | `session_fork_lineage` | `target_session_id` |

Startup checks the migration record and each named row-level `BEFORE UPDATE` trigger.
Each trigger must use the owner's schema-qualified `reject_row_lock_record_update()` function.
It must be enabled for ordinary execution, with no column filter, condition, or arguments.
The migration revokes public access to the function and rejects updates with SQLSTATE `55000`.
It adds no `DELETE` or `TRUNCATE` trigger and changes no foreign-key deletion rule.
These source checks do not prove installed database permissions or deployed behavior.

The shared Broker coordination test reserves seven child slots: four registered
children, including its Fixed child, plus three confirmed WorkPlan items.
It keeps the two-active-child limit and checks that one more active child is rejected.
Fixed-child model assertions cover both Copilot connections and Azure BYOK bindings.
Historical run-limit JSON checks use a fixture with the optional limits unset.
The retirement fixture supplies both required Environment fencing generations.
The selected Events workload profile uses only pure Node source checks.
Run `node --test scripts\azure\tests\events-runtime-profile.test.mjs` from the repository root.
These checks reject missing startup inputs, changed identity/database scope, credentials,
mutable images, malformed Blob paths, migration settings, and unsafe workload/network settings.
Its image user/group, existing TLS Secret, and owner ports remain operator-reviewed inputs.
The incomplete example must fail rendering.
No check proves an image publication, live certificate/file access, cloud grant, or deployment.
The oversized webhook request uses `Expect: 100-continue` to receive the
exact `413` rejection before uploading its body. The one-MiB limit is unchanged.

The pure [Environment suspend flush checks](./environment-sandbox-testing.md) use a fake owner store and Kubernetes transport.
They cover trusted next-generation evidence, replay without another producer call, uncertain retry, and authority loss before effect or commit.
The shared client checks original actor delivery, owner-bound resolution, no-store, and incomplete-result rejection.
The evidence migration is checked against model metadata without a database connection.
The related PostgreSQL case is compiled separately; these pure results do not prove SQL execution or mounted CSI durability.
The staged resume checks compare the saved source and exact new mount, then require the actual Host hash and verified next Network generation.
Controlled Kubernetes reads reject foreign Service owners, selectors, and DNS names before the pre-configuration request.
The historical-read and lifecycle rollback/replay cases compile but require their separate PostgreSQL run.
Bootstrap contract checks bind the saved server configuration, current registration, server-issued attempt, and exact delivery/loss proof.
Resume preparation rejects missing or changed private configuration before owner calls; ordinary suspend receipt validation remains unchanged.
Journal-rebuild receipts bind the saved journal boundary and current attempt while marking native cache as pending until a real authorized turn.
The ordered prefix checks allow run-wide position gaps and reject changed source references, duplicate events, invalid order, and excessive size.
These contract checks do not prove native cache persistence, authenticated pagination, or material-byte recovery.
Controlled placement cases check authority loss at each reservation, attachment, and provider boundary.
They preserve retained Storage and data, release no-effect leases, detach attempted mounts, and retain resources when provisioning already occurred.
The schema checks compare migration metadata with the current owner-effects model, without applying SQL or changing grants.
Those cases do not prove durable bootstrap restart, concurrent replacement, old-grant rejection, or native cache reconstruction.
These source results do not prove native TLS, mounted restore contents, Cilium traffic enforcement, or deployed resume.

| Test suite | What it exercises | What it does not prove |
| --- | --- | --- |
| Orchestrator Core tests | Typed outcome/workflow/plan/revision validation, workflow grammar and step DAG validation, bounded WorkPlan eligibility and joins, deterministic UTC daily/weekly/monthly occurrence evaluation, stable schedule idempotency inputs, bounded structured GitHub event predicates and regex safety, project-scoped backlog graph edits and cycle checks, revision-fenced readiness for merge-complete and verified non-merge completion, MAF witness serialization and current-capture inventory regressions for late non-empty captures and multiple captures, pinned model/provider selection checks, structured scope diffs, AGT Policy provider validation and evaluation, negotiation/pinning, deny-by-default and project-policy narrowing, plus action-guard grant scope/fence checks, immutable owner receipt persistence, journal-ACK ordering, current-grant revalidation, and no-effect behavior on evidence failure. Its PostgreSQL integration tests cover persisted backlog edits, concurrent claim arbitration, idempotent replay after store restart, rollback of claim/root/decision intent, and rollback when current authority is lost immediately before commit or replay commit. The source-only Guardrails candidate uses controlled classifiers, authority and journal doubles to cover ordered seam/run/resource pin consistency, invalid null/default shapes, content SHA-256 binding, typed stage/content support, AGT deny monotonicity, timeout/failure, revocation, redaction, and append response-loss/replay. | Current network authorization in backlog routes, production task-to-whole-plan evidence association/output-proof verification, automatic workflow initiation, live model output, full child dispatch, deployed authorization, production Prompt Shields transport, a trusted Guardrails journal/current-authority adapter, MAF gate wiring, or protected-effect call-site enforcement. |
| Source Control tests | Real temporary-Git workspace isolation and recovery, controlled GitHub HTTP behavior including exact open-PR reuse and exact-head check evidence, raw-byte HMAC and repository binding, strict Broker redemption, immutable provider-pin restore, typed approval-linked merge-grant state, native PostgreSQL repository locking and webhook-delivery replay/conflict, plus Orchestrator audience rejection of direct GitHub POST before Projects or owner effects. | A deployed Source Control service, trusted unattended webhook relay, live GitHub/Identity calls, or automatic workflow execution from a webhook. |
| Optional runtime-owner startup tests | Start a local Orchestrator endpoint host with and without the optional runtime registration owner. Public suspend/resume routes retain request-body and authorization metadata; the missing-owner internal runtime check is not mapped. | Live registration authority, a deployed runtime owner, or successful suspend/resume effects. |
| Accepted-run skill reader tests | Controlled Projects HTTP responses cover original bearer/tenant forwarding, exact registration and selection hashes/revisions, ordered agent assignments, immutable content/resource hashes, legacy unpinned denial, no-store/error responses, and revocation during the content wait. Existing reviewed-snapshot reader tests retain byte-stable selections and current-authority checks. A controlled native SDK test calls the actual `skillProvider.list` and `skillProvider.read` callbacks, extracts the advertised `/workspace/...` resource path, reads it through `sessionFs.readFile`, and checks exact UTF-8 content and bytes. | A deployed skill owner, deployed runtime route/SDK loader, live skill loading, or complete skill-import management. |
| Private reviewed tool result tests | Managed HTTP tests reject an HTTPS flag without native TLS before reading proof bodies or loading a certificate. Fixed-owner tests check original actor forwarding, exact proof acknowledgment, bounded GET/POST responses, explicit changed-binding denial, and deadlines after headers. Reviewed-agent tests check saved-result recovery before external dispatch, exact proof/bytes/identity matching, and current-authority denial during reads and acknowledgments. Transport handoff checks retain the original context and full proof, reject changed serialized calls before transport or result writes, preserve the checked private copy when the serializer buffer changes, reject authority changes during serialization, and propagate missing-network denial without a retry or result write. | The receiver's actual upstream method, target, or body, a production native transport, positive native mutual TLS, certificate issuance or trust, forced network enforcement, a real remote-tool call, private-result PostgreSQL acceptance, deployed recovery, or enabling the native remote-tool hook. |
| Provider tests | Catalog validation, cardinality, overrides, capability negotiation, and run pinning. | Resource provisioning or network-policy enforcement. |
| AgentHost native prompt-capacity tests | The pinned SDK/runtime sends turns through the accepted BYOK configuration to an isolated loopback endpoint. The endpoint checks the actual bearer credential, model, and request path on every request, and requires a marker for the exact SDK session on the initial route-proof request; after that proof, later requests may omit the marker when compaction removes its history. The endpoint returns a unique response receipt that the native session must return. Successful native compaction must reduce the measured context token count and leave the total at or below the accepted capacity. The overflow input is token-counted by the native runtime under a higher cap before the exact same input must fail with `context_limit` at 4096 without another inference request. Missing SDK provider metadata stays unknown; a non-empty mismatch is rejected. | Hosted Copilot, production BYOK access, or deployed behavior. |
| Azure Files provider tests | Environment-scoped generation PVC identities, pinned Kubernetes API target, requested/configured StorageClass matching, effective reclaim-policy enforcement, claim/PV ownership checks, reclaim/delete UID preconditions, exact release receipts and retry outcomes, Azure Files mount options, Kubernetes quantity normalization, and credential-free request bodies through fake Kubernetes transports. | A live Kubernetes API/CSI driver, Azure Files durability or data erasure, Sandbox attachment, or durable flush. |
| Environment workspace-volume tests | Real PostgreSQL owner CAS and replay, pinned provider binding, fresh Projects authorization, retry-safe no-effect rejections and HTTP status mapping, Release rejection while bound or attached, uncertain Release retry with the same idempotency key, Replace cleanup leases/retries, stale-fence rejection, and truthful blocked/pending cleanup outcomes through fake providers and Projects clients. | A deployed Environment service, live Kubernetes/CSI behavior, Azure Files data erasure, Sandbox mounting, or durable flush. |
| Identity library tests | Exact actor, project, run, purpose, secret ID, and version grants. They cover revocation races, expiry, cancellation, and credential invalidation. Runtime Sandbox health checks validate the no-store JSON response, Service-DNS HTTPS address, pinned Pod endpoint option, and bounded body without sending credentials. | Live Kubernetes ownership, native TLS handshakes, or a deployed broker. |
| Projects Broker run-binding TLS tests | Real local Kestrel handshakes for TLS 1.2 and 1.3, private-route native-TLS checks, certificate profile and leaf-pin checks, and rejection of forwarded HTTPS without a handshake. | Live certificate mounts, cloud identity, deployed routing, or deployed proof reads. |
| Broker run-binding TLS tests | Real local Kestrel TLS 1.2/1.3 requests with custom trust-bundle bytes; wrong roots, leaf DER pins, hostnames, certificate purpose, expiry, malformed PEM bundles, and redirects are rejected. | Live certificate trust, deployed owner reachability, or cloud acceptance. |
| Remote MCP credential recipient composition tests | Check the separate keyed Broker TLS profile, missing and incomplete configuration, service-role and listener validation, rejection of ordinary HTTPS, and denial before certificate resolution. | A mapped credential-use route, positive native mutual TLS, installed certificates or service identities, current exact-call or applied-network authority, credential delivery, or deployment. |
| Remote MCP Identity credential binding helper tests | Pure row checks select the exact current access-token reference and fixed tools-call purpose. They reject changed owner/configuration bindings, non-authorized states, expiry, changed authority after waits, and altered call semantics. The retained disconnect regression compiles with the PostgreSQL suite; it is not part of these pure checks. | Caller authentication, a connected credential-use endpoint, secret reads or delivery, forced network proof, PostgreSQL execution, complete Identity suite acceptance, or deployment. |
| Remote MCP trusted owner TLS tests | Check separate Environment and Events profiles, fixed owner clients and finite timeouts, current-runtime-owner requirements, wrong peer names, principal and certificate-pin conflicts, shared listener ports, and denial before proof-body or certificate reads. The default-off Environment reviewed-tool recipient reuses only its Orchestrator profile. Checks reject missing opt-in, wrong service or port, and ordinary HTTPS before certificate access. Current-call composition checks default-off behavior, required TLS and gateway options, scoped readers, and one shared active handler. | A mapped reviewed-tool route, positive native mutual TLS, complete original-actor authentication, real applied-network verification, credential use, remote dispatch, or deployment. |
| Remote MCP shared current-call and gate tests | Controlled owner HTTP checks the original authenticated actor, full proof and exact call acknowledgment, actor expiry, missing owners, redirects, cache rejection, and changed policy after a wait. Native context checks reject another actor, expired or revoked registration, changed run, and wrong verification receipt; JSON cannot carry the context. Gate checks bind the operation, handler instance, generation, and complete snapshot pins. | A production current-call resolver, active native gate implementation, forced network mediation, credential delivery, positive native TLS, SQL acceptance, or deployment. |
| Remote MCP actual request-byte tests | Pure shared-contract checks bind the actual HTTP method, target, tool operation, name, and canonical argument bytes to the reviewed proof. They check duplicate keys, strict JSON shape, UTF-8 byte limits, the exact body and argument limits, metadata types, and request IDs independent of the reviewed native call ID. | A connected receiving route, original-actor authentication, current owner authority, forced network enforcement, credential delivery, native dispatch, or deployment. |
| Remote MCP caller and receiver source integration tests | Controlled owner HTTP checks the fixed private dispatch target, original actor and tenant forwarding, unchanged proof and copied request bytes, result-proof matching, bounded responses, actor changes after waits, explicit owner timeout, and no retry on missing enforcement. Pure composition checks require explicit opt-in, the existing separate service TLS profile, the dedicated recipient key, and the current-call verifier. The retained receiver checks cover early peer denial and the exact 2-MiB body limit plus one byte. | A successful production sender, positive native mutual TLS, certificate or routing installation, a native SDK hook, forced network enforcement, credential delivery, PostgreSQL execution, or deployment. |
| Remote MCP current gateway reader tests | Controlled active-handler state and Projects reads bind exact options, gateway, egress, Environment fence, and full snapshot. They reject changed actor, selection, handler, or expiry after waits, and missing target purpose or read permission. The managed gate returns its exact descriptor and fresh verification receipt, and rejects another snapshot or revoked state. Controlled current-call service checks read the current Orchestrator registration and full proof before the missing-enforcement denial. Direct route checks reject the wrong TLS port before verifier or certificate creation. Four unknown-length body cases check the exact 2-MiB limit and one extra byte, with absent or read-only server limits and no verifier creation. They use a temporary test certificate, not a native TLS connection. The reader does not call the Cilium store or perform policy writes. A current or copied result still cannot supply missing forced-network proof. | Persistent active-handler state, positive native TLS, forced L3/L7 mediation, an enforcement-proof producer, credential use, native sends, SQL acceptance, or deployment. |
| Identity broker tests | OpenIddict validation, external login, consent, S256 PKCE, refresh replay, PostgreSQL grants, redemption HTTP, and real broker-issued tokens against Projects API source-owned memberships/roles, including forged-claim rejection and revocation. GitHub Repo App PostgreSQL tests cover owner-bound OAuth and installation callbacks, PKCE/state/cookie replay rejection, refresh rotation/revocation/uncertain outcomes, installation/repository discovery, hashed selection codes, same-project reuse and cross-project denial, exact one-repository permission requests and expiry/digest checks, and absence of token/key values from Identity rows. Browser-origin tests check credentialed CORS preflights before authentication for OAuth and Repo App fetch routes. They check that CORS does not allow other origins, internal routes, or health routes. They check the fixed absolute Web completion redirect. Broker-backed Orchestrator/Events integration proves fork denial without reservation, reserved fork registration/replay, revocation before journal commit with no target/lineage change, and explicit unregistered outcomes. It also proves Projects revocation during late fork-command INSERT, owner outbox UPDATE, and registered-duplicate row-lock waits; exact current-fence runtime-owner context absence; and failure/recovery decision, gate, grant, context-history, and restart behavior. Source Control integration proves typed merge approval through real Projects, Broker, and Orchestrator hosts; persisted conflicts with no merge write for revocation during repository-lock wait, redemption, readiness, and the `merge_started` check; truthful merged SHA after post-effect revocation; and replay from a fresh Orchestrator host. **COUPLED fence proof:** the real PostgreSQL advisory-lock wait precedes a Core turn-failure transition while Projects authorization remains valid; the newer decision/fence supersedes the typed approval and the current-grant lookup plus owner CAS confirms the exact grant is no longer current before persisting `source_control_run_binding_changed` with no merge PUT. The same flow rejects a changed accepted-selection PUT and checks the current GET is byte- and hash-identical, including its revision fields, both after the PUT and after the fence change. Concurrent HTTP executions wait on the repository lock, return the same merged SHA with one GitHub merge PUT, and replay from a fresh host. Authorized workspace preparation/diff uses distinct API and checkout `SecretRef` purposes and a dual API/Broker audience token against a real temporary Git origin; responses, workspace manifest, and Git config contain no checkout credential. Other coverage includes current selection authority, typed decision/gate ownership, grant and receipt persistence, Sandbox resolution and revocation, concurrent proposal idempotency, and Policy receipt admission under expired/superseded grants, current Deny, and Core-role revocation after SQL lock waits. | Deployed OAuth, Azure RBAC, Azure acceptance, production Sandbox provisioning/dispatch, an active background relay, automatic AgentHost scheduling, or gate approval/completion from message receipt. |
| Gateway/BFF tests | Broker-signed RSA bearer validation, wrong-audience and missing-token rejection before owner calls, real Projects & Config/PostgreSQL authorization, tenant and bearer forwarding, owner `202` and stale-gate `409` passthrough, owner outage and timeout mapping, complete OpenAPI route/schema/reference checks against serialized Projects and Orchestrator DTOs, Skills/Marketplace route forwarding with exact owner paths, bodies, bearer, tenant selector, required revision queries, and 3 MiB request-body limits for inline and selected-source preview/import, metadata-only skill inventory query bounds, exact query/bearer/tenant forwarding, no-store responses, and owner query/problem passthrough, replay/reconnect cursors, pre-start SSE owner failure mapping, and stream abort after an Events body failure or current Viewer revocation. Events pages use a controlled owner HTTP transport in this boundary test. | Deployed Gateway, public DNS/TLS, production owner routing, automatic AgentHost integration, or cloud acceptance. |
| MCP tests | Native Streamable HTTP initialize/list/call, live Gateway OpenAPI tool schemas, schema-invalid enum and array-item rejection before dispatch, fixed route/argument mapping, idempotent retries, owner denial/status/outage preservation, post-headers body-stall timeout, public RFC 9728 metadata and challenge, plus real OpenIddict issuer/audience/expiry/signing-key rejection. The Broker integration test issues a real OAuth access token and verifies it is accepted by MCP and Gateway and reaches the controlled Knowledge owner unchanged with the idempotency key. Inventory tool schema checks cover required project ID, optional cursor/page size and tenant selector, and the 1–50/default-25 bound derived from Gateway OpenAPI. A separate local-only test pins the installed MCP client to Streamable HTTP `2025-06-18`, exercises a native initialize/initialized/tools-list handshake against an in-memory handler with finite test bounds, and verifies a missing applied-L7 proof never reaches its downstream handler. | A deployed MCP host, live Knowledge/PostgreSQL owner authorization, external MCP client interoperability, project-only network admission, applied L7 enforcement, protected Remote MCP calls, or cloud acceptance. |
| Web client tests | Broker-token handling and exact project/run scoping; Gateway error, idempotency, and SSE parsing; Skills/Marketplace route and tenant forwarding; source-revision-pinned browse, selected-source preview/import with digest and retry-key checks, and late-response invalidation; owner skill inventory pagination, provenance, revocation metadata, and visible permission errors; local file preview/import/assignment with owner receipts and visible errors; runtime-loaded status kept unavailable; cookie/CSRF Repo App Broker DTOs, native popup form posts, callback origin/source/request matching, deferred-CSRF cancellation across navigation/reopen, stale status rejection, and run-bound Gateway installation/pinning; revisioned project configuration; paginated Knowledge records; owner-snapshot refresh and stale-gate responses; exact request-ID/state-version gate actions; journal replay/live ordering; and truthful accepted-selection and usage-total rendering. Run with `npm --prefix apps/web test`; typecheck, lint, coverage, and production build use the scripts documented in the [web client guide](./web-client). | Live Gateway/Broker requests, source imports, real owner acceptance, deployed selected-source routes, deployed UI/image, automatic AgentHost scheduling, model execution, or cloud acceptance. |
| Web production-container check | `node --test scripts/release/tests/web-container-callback.test.mjs` builds the pinned ASP.NET Core 10 image without network access and verifies non-root execution, callback/runtime-config security headers, callback query stripping and execution, runtime configuration encoding, hashed-asset caching, missing-asset 404, SPA fallback, and callback-log redaction. CI preloads `mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4` for `linux/amd64` before Node coverage. Locally, Linux amd64 Docker must have this exact runtime image cached. | Live Broker/Gateway browser journeys, deployed DNS/TLS/ingress, or cloud acceptance. |
| PostgreSQL tests | Outbox and inbox transactions, duplicates, concurrency, leases, relay outcomes, and recovery across restart. | A broker, relay daemon, exactly-once delivery, or cross-service transaction. |
| Events & Sessions tests | Provider-neutral contracts and the P0 Identity Broker principal profile; explicit runtime/migration Entra configuration with no identity fallback; PostgreSQL token refresh and password rejection; project/run-scoped IDs; append, deduplication/conflicts, PolicyEvaluation redaction and provenance checks, receipt-reference admission/no-store acknowledgments/transaction rollback, legacy capability pins, fork prefix lineage and authenticated idempotent retries, owner-admission revocation, migration, and rollback. Addressed-message tests cover idempotency, ordering, leases/fencing, transactional outbox, acknowledgments, expiry, and undeliverable state. PostgreSQL coverage uses disposable containers. | Workload-identity federation, production Entra grants, live cloud migration, AgentHost integration, protected-effect call-site enforcement, or production-scale replica behavior. |
| Environment egress tests | Purpose-aware FQDN/CIDR intersection, Projects authorization freshness, Cilium options and policy rendering, resource-version/generation fencing, object readback, and provider pinning with fake Kubernetes resources. | Sandbox claim/template labels, Kubernetes RBAC/workload identity, a deployed Cilium datapath, actual network reachability, or public HTTPS/Remote MCP L7 mediation. |
| Remote MCP parser and contract tests | Exact Registry response/version and static endpoint pins, tool schema and catalog digests, duplicate/invalid JSON rejection, byte limits, and unlinked delegated-OAuth configuration. | PostgreSQL migration/CAS/replay/restart behavior, a live Registry, MCP transport, reviewed tool acceptance, OAuth consent, or L7 mediation. |
| Environment Sandbox tests | See the [Sandbox testing guide](./environment-sandbox-testing.md) for owner-fenced lease, selected-provider, readiness, and recovery coverage. Source route checks require authentication and POST for Runtime profile resolution. BuildTest source tests check complete checkpoint/profile binding, required-output failure, distinct collector terminal provenance, timeout/cancellation classification, server capability configuration, and migration/model metadata consistency without a database connection. BuildTest PostgreSQL tests create actual owner, attached Workspace, and active Sandbox records. They cover concurrent reservation, connection-pool restart replay, immutable intent, stale-binding no-effect rejection, policy-attempt persistence, timestamp precision, and terminal-evidence preservation. | Live AKS or RuntimeClass behavior, Cilium datapath enforcement, deployed AgentHost configuration, BuildTest command-Pod execution, Core run pins, or deployed service configuration. |
| Version 2 BuildTest source tests | Full binary output documents, baseline preservation, native preparation/capture references, pending-document reconstruction, canonical object and journal-position checks, final-receipt acknowledgment requirement, bounded owner transfers, and lost-response replay. Environment metadata checks surround Core acknowledgment verification. | Native repository preparation, actual descriptor-safe Pod reads, Kubernetes cleanup, PostgreSQL admission/restart integration, or complete BuildTest acceptance. |
| Knowledge tests | Knowledge-owned PostgreSQL migrations and least-privilege runtime grants; project/agent isolation; immutable revision history, CAS conflicts, idempotent retries, explicit proposal decisions and outbox persistence; context filtering/restart; TestServer checks for fresh Projects authority and original-token forwarding; Cosmos adapter option/provider tests with a fake document store, including negotiation rejection for an expiring TTL or missing search index, batch timeout/size/throttle failures, immutable run-pin checks, and no fallback when Cosmos is unavailable; Redis adapter tests with a controlled command-client fake for TLS/options and provider selection, AOF/eviction/topology rejection, persistent-key and hash-field checks, malformed typed-payload rejection during negotiation and scans, single-write batch prevalidation, CAS, lost-response idempotency, server-time lease fencing, and backend-loss behavior; opt-in native Redis tests use the production client/store for real-Lua batch atomicity, concurrent CAS, and promotion/receipt/idempotency plus server-time lease fencing across an AOF-backed restart of one digest-pinned, test-owned instance; deterministic identical-retry races for update, reject, and promote; current scoped receipt reads, legacy three-field N-1 delivery/replay, partial-scope rejection, and a missing-pin lookup that verifies no binding is created. Identity Broker integration tests use broker-issued minimal-profile tokens, live Core membership/role lookup, the Knowledge API, and PostgreSQL to verify the private-content `WriteProjects` boundary. PostgreSQL uses disposable containers. | A deployed Knowledge service, production workload identity, live Cosmos or Redis permissions/durability/availability, or delivery from either Memory adapter to a deployed Events & Sessions journal. |
| Key Vault tests | Azure SDK authentication and secret requests through in-memory HTTP transports. Workload identity tests use generated token files and fake OAuth and Key Vault endpoints. | Live token exchange, Key Vault RBAC, or an Azure deployment. |
| Blob tests | Azure SDK requests, streamed data, create-only writes, and missing-object results through a fake HTTP transport. | Live credentials, permissions, durability, or cloud access. |
| Telemetry tests | In-process OpenTelemetry setup and Azure Monitor exporter behavior through an injected transport. | Azure Monitor ingestion. |
| Azure tooling tests | Target, source, digest, command, and acceptance guards through fake `az` and `git` executors. | A live Azure call or provisioned resource. |
| P1 PostgreSQL initializer source tests | Canonical database grant comparisons, pre-write rejection, five exact principal mappings and memberships, function ownership and EXECUTE grants, planned and read-back installed mutation guard checks, ordered role-stage checks, partial-stage receipts, and original-error preservation when the failure receipt fails. The existing Foundation Probe test project compiles the same dependency-free helper used by the initializer. | Initializer execution, actual PostgreSQL catalog or transaction behavior, live Entra principal creation, restricted-role mutation tests, runtime identity proof, or deployment. |
| Foundation Probe tests | Receipt and token checks, provider pins, exact-version Key Vault read, owned Blob cleanup, PostgreSQL effects, and trace evidence. | Azure resource access or Identity broker redemption. |
| AgentHost tests | Actual native SDK transport, authenticated configure/refresh/A2A, exact replay, current owner/source rejection, guarded effects, turn content, idle-boundary priority, accounting acknowledgment, cache recovery, and exact startup-time ceilings. CI separately measures the compressed amd64 OCI image and starts its image-owned pinned runtime with non-executable private mounts, without network or a model call. | Automatic scheduling, deployed TLS/Sandbox placement, live OAuth/entitlement, paid model execution, or publication. |

Source Control regressions cover GitHub's actual PR-list and check-runs response shapes,
ruleset and classic branch-protection requirements, and repository issue availability.
PostgreSQL tests cover case-insensitive repository-name matching while retaining exact
provider IDs. Broker integration tests exercise the webhook route through actual Kestrel:
an authenticated, correctly signed body over 64 KiB succeeds and a body over 1 MiB is
rejected. Merge settlement coverage cancels the caller after GitHub accepts the merge and
checks that the merged SHA remains durable and can be replayed from another host.

## Run tests

Run the bounded initializer source tests with
`dotnet test tests/Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresInitializerTests`.
This selection does not start the project's PostgreSQL containers.
Both initializer execution blocks remain active.
See the [operator source limits](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#p1-postgresql-initializer-source-proposal)
before interpreting these checks as evidence.

Shared-model and run-admission source tests cover reference-versus-model-ID resolution,
configuration revision/hash mismatch, disabled models, accepted connection binding,
and rejection before native SDK startup.
The compiled PostgreSQL cases cover root/admission atomic persistence, replay without repricing,
hard-limit rejection, and root/backlog rollback after current-authority loss.
Events cases check that real run-start quotes create no usage or native-source rows.
Compiling these cases is not PostgreSQL execution evidence.

Numeric owner cases use the existing disposable Coordination PostgreSQL fixture and actual migrations, registrations, checkpoints, source store, and action-grant store.
They cover exact model-send reservation replay, restart without resend or refund, and concurrent child-runtime admission at the whole-run cap.
Tool cases cover permission-only checks, exact retry, changed-event conflicts, and concurrent reservations at the whole-run cap.
These store tests do not prove HTTP authentication, live native effects, or deployed Policy enforcement.
Controlled TCP tests separately exercise the actual SDK permission notification, pending-permission response, and pre-tool hook without a double debit.
Prompt tests check actual catalog/provider wire fields, missing-capacity denial, accepted narrowing, immutable source pins, and cached resume under increased or decreased capacity.
Capability-wire tests do not prove live token enforcement or truncation.

Managed correction tests cover optional-step skips without success receipts and required BuildTest blocking.
Decision tests cover exact revision limits, invalid proposals, gate replay, fence changes, and saved or older envelopes.
Fixed-work tests compare the exact child association, accepted model, plan, outputs, selection hash, fence, and decision version.
Route tests check registration-read authorization and readiness rejection for anonymous, foreign-tenant, wrong-audience, and stale-registration requests.
These tests use controlled owners and SDK responses.
They do not prove live PostgreSQL reads, native execution, private TLS, or deployment.

Retained catalog tests check wall-time bounds, the exact inclusive deadline, and configured or omitted numeric limits.
The registration persistence case checks complete session-scoped pages without duplicate or cross-scope IDs.
The Knowledge endpoint case rejects a prompt-token request above the accepted run budget.
This checks the Knowledge retrieval budget, not the full model request or SDK compaction.
The latter two cases require the existing disposable PostgreSQL fixtures.
Compilation alone does not prove those database cases passed.

The positive Broker fixture prepares its guarded native turn through the current Orchestrator owner and persisted MAF checkpoint.
One real Events host provides pricing, accounting, and session material; the controlled SDK supplies native completion evidence, not a paid model call.

Ordinary v1 PR CI runs these PostgreSQL suites through `npm run coverage:dotnet` on its existing Linux runner.
Each suite retains standard TRX results beside Cobertura coverage and `source.json`.
The TRX logger can copy a collector report into its attachment directory.
The runner counts byte-identical copies once and still rejects missing or distinct extra reports.
Use the tested source SHA, PR head, run, and attempt to join evidence to the exact reviewed source.
A compiled case or an older green run is not a passing receipt for changed source.

Use the .NET 10 SDK selected by `global.json`, Node.js 24, and a Docker-compatible engine. PostgreSQL integration tests start their own disposable container.
CI installs Chromium for broker browser integration tests from the Release-built
`tests/Agentweaver.Identity.Broker.Tests/bin/Release/net10.0/playwright.ps1` before
`npm run coverage:dotnet`.
Browser callback fixtures also require the locked Web production shell in `apps/web/dist`;
prepare it with `npm ci --prefix apps/web --no-audit --no-fund` and
`npm --prefix apps/web run build` before running those fixtures. They configure the
normalized Broker issuer separately from the ephemeral transport URL.

Run these commands from the repository root:

```powershell
node --test scripts\release\tests\deploy.test.mjs
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GitHubRepoAppConnectionServicePostgresTests
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~SourceControlSecretRedemptionClientTests
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Mcp.Tests\Agentweaver.Mcp.Tests.csproj --no-build --no-restore --configuration Release
npm run coverage:dotnet
npm run coverage:node
node --test scripts\coverage\tests\*.test.mjs
npm run test:azure
npm run test:release
```

Missing Docker, image-pull permission, or PostgreSQL startup fails the integration suite. Testcontainers does not clean up unrelated containers or contact production resources.

### Focused MAF review corrections

Managed tool-callback tests check repeated invocation and secondary permission requests after authority revocation.
They also check cancellation and a turn change during an authority wait.
Stable native call IDs prevent duplicate reservations; they do not cache permission to run a tool.
Managed revision tests retain the exhausted attempt count through gate replay, fence advance, and saved-state recovery.
PostgreSQL quota tests also check the saved revision count before failure, after failure, and after recovery.
Compiling those PostgreSQL tests does not prove that they passed against a database.

### Opt-in native Redis Memory tests

`RedisMemoryNativeIntegrationTests` are skipped unless
`AGENTWEAVER_REDIS_MEMORY_NATIVE=1`. They do not create, pull, or delete a container.
The restart case restarts only the validated dedicated test instance.
Supply an already-running instance through these variables:

| Variable | Requirement |
| --- | --- |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_ENDPOINT` | A loopback `rediss://127.0.0.1:<published-port>/` endpoint. The certificate must be trusted by the test host and valid for the endpoint host; certificate validation is not bypassed. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_CONTAINER_ID` | The full 64-character ID of the dedicated container. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_OWNER_ID` | A unique 32-character run GUID. The container and AOF volume must both have labels `io.agentweaver.redis-memory.native-test=true` and `io.agentweaver.redis-memory.native-test-owner=<run-guid>`. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_IMAGE` | The exact cached image reference, including `@sha256:<64 lowercase hex digits>`. The inspected container image must match it; tests do not pull missing images. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_ENGINE` | `docker` or `podman`, used only to inspect the labeled instance and, in the restart case, restart its exact ID. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_USERNAME` and `AGENTWEAVER_REDIS_MEMORY_NATIVE_PASSWORD` | Optional existing ACL credentials. Set both or neither; the test does not create credentials or print their values. |

Publish TLS port `6380/tcp` only on the loopback host port in the endpoint. Put Redis's
active AOF directory (`dir` plus `appenddirname`, when present) on a dedicated named
volume with the same test-owner labels. Redis must pass normal provider negotiation,
including AOF-always, healthy AOF, standalone-primary, and `noeviction` checks. The
helper verifies the container ID, image, loopback binding, AOF path, and volume labels
before any write. The restart case rechecks ownership immediately before restarting
only that container, then reconnects to the same ID and pinned image. Every test uses a
fresh key prefix; the tests never run `FLUSHALL` or perform namespace-wide cleanup.

After the controlled test-owned instance is allocated, run only the native filter:

```powershell
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-restore --configuration Release --filter FullyQualifiedName~RedisMemoryNativeIntegrationTests
```

Do not set the opt-in flag until all instance, image, volume, endpoint, and ownership
values are confirmed. The native filter is not part of offline test runs.

## Reviewed remote-tool selection

Reviewed remote-tool selection tests use the actual Projects selection reader with
controlled owner responses and a controlled immutable-store resolver. They cover
exact references, duplicate and foreign references, missing or changed snapshots,
and authority loss after resolution. Project validators reject duplicate references
and retain the omitted legacy field. The numeric-limit PostgreSQL tests use the
concrete snapshot store and preserve existing grant and limit behavior.
These checks do not prove production migration activation, current remote-connection
authority, credential access, applied L7 enforcement, or a protected remote call.

## Meter-keyed Cost source selection

`AzureByokNativeUsageUsesBrokerCoreAndEventsWithExactDispatchProvenance` uses the real Broker, Projects, Core PostgreSQL, and Events owners.
Only the external native SDK transport and credential backend are controlled.
The fixture requires an accepted Azure provider pin and exact-version model-session redemption.
It records native SDK usage, duplicate/conflict outcomes, and reference-only Events acknowledgment.
Raw usage callbacks have no proven dispatch ID. The recorded native turn's exact
event IDs link its accounting snapshot to the accepted dispatch.
Events checks the persisted Core observation before and after its Cost transaction.
Contract and PostgreSQL tests reject missing native evidence, foreign dispatches,
changed revisions, wrong event sets, late events, and altered accounting hashes or amounts.
Generic requests still require the raw dispatch ID to match.
Controlled Core-response tests use the real Events PostgreSQL transaction.
They verify both native reads occur outside the Cost lock and reject changes before returning a snapshot.
Core PostgreSQL tests use controlled Events snapshots to check current-checkpoint changes,
concurrent report reuse, retained overlapping reports, and exact completion replay.
The positive Azure fixture also reads the real authenticated Core route and rejects forged report, revision, and fence references.
BYOK authority-loss cases reject stale grants, retired leases, and expiry without native or ledger effects.
The Projects fixture reuses its role-bound connection pool and closes it before role cleanup.
Provider/model mismatch and missing-measurement tests reject invalid source facts.
These checks do not call a paid model or prove Azure deployment.

Provider tests cover independent enabled selections, duplicate/disabled/wrong-seam
catalog rejection, absent sources, exact adapter/options schema requirements, and
required capabilities. The Projects disposable-PostgreSQL tests use the production
catalog loader and run-selection service. They persist and replay multiple source
keys, read the immutable selection, reject invalid requirements, and deny a project
Owner access without current Orchestrator authority. Existing non-Cost selection
and tenant-authorization coverage remains in the same suites. A legacy-format
regression pins the original non-Cost request serialization bytes, seeds a
pre-change snapshot with no meter-source field, and verifies replay/read without
rewriting its fingerprint or stored history.

These tests exercise candidate selection only. They do not prove SDK model-source
provenance, resource negotiation, pricing, or positive usage ingestion.

## Cost bindings, pricing, and usage storage

The additive Provider tests cover Cost resource pins, changed configuration, and
missing or changed providers. Existing candidate-selection and legacy snapshot
tests remain unchanged.

The Events & Sessions tests cover weighted nano-AIU pricing, unweighted quotes,
missing measurements, immutable rate cards, and redacted binding diagnostics.
`AzureCostProviderTests` cover model-scoped standard token arithmetic, explicit
rate-card source/currency/version, missing or unsupported token measurements,
changed pins, overflow, unsupported BYOK quotes, and provisioned-throughput
`Unpriced` behavior. These local provider tests are pure; they do not prove
trusted BYOK receipt pricing, PostgreSQL integration, or Azure price retrieval.
Hosted usage contract tests accept priced nano-AIU without optional SDK accounting metadata.
They still reject wrong source identities, meter shapes, and unpriced entries.
Cost snapshot contract tests distinguish valid zero-work quotes from unavailable pricing.
They reject source, model, rate, unit, and run-scope mismatches.
Observed snapshot tests require bounded, unique references and reject missing, changed, or foreign acknowledgments.
Native completion tests require exactly the usage-event IDs in the durable completion range.
Host tests reject a failed owner join without an answer or a second native send.
They allow explicit unpriced uncapped output and account later usage without inventing range membership.
The PostgreSQL snapshot tests cover real pricing pins without synthetic usage,
authority rollback, and Copilot-only totals with unpriced other meters.
They also join actual source and ledger rows, including explicit unpriced entries,
and reject changed registration, source, platform message, event, hash, or amount.
Run-admission tests call the production service with its PostgreSQL consumer and controlled Projects HTTP responses.
They preserve the original bearer, tenant, and no-store request settings, and verify replay through a new service instance.
Missing initial authority and changed current authority, selection, or bearer expiry must not create a Cost binding.
These tests do not prove a deployed Broker or a live provider.
These tests require the disposable PostgreSQL lane; compilation is not execution evidence.
The disposable-PostgreSQL tests cover concurrent duplicates, content conflicts,
restart, immutable history, transaction rollback, and exact run/agent totals.
Receipt tests read the committed hash and immutable price through a separate
PostgreSQL connection. Duplicate retries return the identical receipt.
Migration tests cover fresh Events version 8, admitted version-3 and version-4 upgrades,
the legacy version-2 project-fact layout, and rejected version gaps.
The Orchestrator migration fixture covers versions 8 through 16 and versions 18 through 21.
It checks fresh initialization and existing version-19 and version-20 schemas that lack resources 016 and 018.
The final history must contain exactly 20 registered versions; version 17 remains unregistered.
Tests reject unknown versions, missing prerequisites, missing tables, and a missing or disabled snapshot trigger.
Repeated initialization preserves the original history and applies no resource twice.
Backlog and reviewed-snapshot tests use the production migrator, not separate SQL setup.
Restricted login roles perform actual task/dependency edits and snapshot persistence, resolution, and exact replay.
They cannot change the schema, write migration history, truncate tables, delete tasks, or update or delete snapshots.
The focused disposable-PostgreSQL selector is
`FullyQualifiedName~CoordinationOwnerMigrationTests|FullyQualifiedName~BacklogOwnerStorePostgresTests|FullyQualifiedName~ReviewedRemoteToolSnapshotStorePostgresTests`.
Source publication does not invoke a migration against a shared or live database.
Repeated migration checks preserve the original migration history.

The combined Broker test connects actual OAuth and bearer validation, current Core
memberships/roles, immutable Projects selection, Environment lease/profile, Orchestrator
registration and source receipt, native SDK callbacks, and Events HTTP/PostgreSQL accounting.
The test controls only external placement, SDK transport/events/catalog, and pricing inputs.
It does not seed producer authority or take a desired model from caller JSON.

The five scenarios cover successful accounting, Broker revocation before SDK creation,
observe-grant revocation during an observation lock wait, registration revocation
before the source lock opens, and Environment retirement during an observation wait.
Three additional scenarios hold the actual SDK `status.get` response before creation.
They revoke the genuine Broker grant, retire the actual Environment lease, or wait
for the real source credential to expire. Each proves zero `session.create` requests,
source records, and accounting entries.
The positive scenario commits duplicate native callbacks to one immutable receipt,
then verifies reference-only ingestion and exact `0.00123456725 AIC` accounting.
It links a real Identity connection through controlled GitHub and native Key Vault SDK transports, then restarts the Broker.
It rotates that connection after Core accepts the selection and verifies unchanged selection bytes and hash.
The model-session response contains only the current access token and typed exact-version proof.
Response, log, and SDK-wire checks exclude refresh tokens and OAuth client secrets.
The native SDK session store and ambient configuration discovery remain disabled.
The custom filesystem captures opaque native cache bytes and restores the same logical session through the actual SDK resume call.
Missing, incompatible, or corrupt caches rebuild context from journal content without a model-turn replay.
Resume producer tests reject Events authorization or transport failures, repeated
or empty pages with a next cursor, and TURN bytes, references, hashes, or per-event
provenance mismatches. These checks run before readiness or native session creation.
Full Host Configure tests accept prior-runtime TURNs and visible-parent TURNs
through saved source-session replay. They do not read same-run events that are
absent from that replay.
Full Host Configure tests also prove that a compatible cache uses native resume and an authenticated incompatible cache falls back to the saved journal.
The guarded positive scenario records the actual user and assistant content, the Policy receipt, and the native cache reference.
Separate controlled Host tests require Core's exact current-operation echo before stopping turns and around native cache writes.
They reject changed operation identity, phase version, or authority, and missing replay cache.
They do not prove a persisted Core suspend operation or PostgreSQL suspend success.
It rotates the genuine source grant before the protected turn and checks refresh replay.
Native cancellation tests hold the actual abort idle event before they admit another turn.
Failed aborts and missing idle events reject later turns and cache capture.
The multiplier is not applied a second time.
Database checks reject mutation and truncation of source and accounting history.
They check each table's statement trigger and attempt dependent, multi-table, and
`CASCADE` truncation. Stored rows and pins remain identical, and receipt replay succeeds.

The fixture disposes the receiver before its caller credential and HTTP transport.
Failure output retains the exercise error and cleanup errors.
Direct console stages identify pending owner requests, SQL waits, native SDK responses,
and cleanup when a test aborts before Xunit reports a result.
Broker hosts own their registered PostgreSQL pools.
Native fixture SQL connections use a fixture-owned data source that closes during cleanup.
Repeated-host and full-suite checks verify that owned connections return to zero after disposal.
The Projects fixture also owns a separate pool for its restricted runtime role.
Its regression opens 16 sequential connections, requires one database backend, and verifies that authority writes remain forbidden.
The fixture closes that pool before it removes the role.
Broker coverage includes the Agent Runtime library through the existing test project and collector.

The canonical Sandbox integration also covers public write versus internal run-read
permission, three current-authority reads, and retained-lease protection against competing retirement.
Neither placement route reads the accepted selection recursively or dispatches provider effects.
The readiness module test holds the actual PostgreSQL advisory lock during fresh Cilium and Sandbox observations.
A competing retirement waits until both readiness observations finish.
The test uses fixture-owned data sources and the retained selection snapshot without recursive selection reads.
These local tests do not prove deployed hosts, live AKS placement, or paid model output.

The [AgentHost source checks](../architecture/agenthost#focused-source-checks) give the exact focused command and image-evidence boundary.

After the Release build, run the combined source scenarios with:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~CopilotConnectionRotationPreservesAcceptedSelectionThroughNativeSdkAccounting --blame-hang-timeout 3m --blame-hang-dump-type none --logger "console;verbosity=normal" -- xUnit.ShowLiveOutput=true
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~AuthorityLossDuringSdkPreparationPreventsNativeSessionCreation
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerIssuedOwnerAndSeparateRunSelectionAuthorizeWorkspaceVolumeHttpEffects
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerTokenTraversesMcpAndGatewayToKnowledgeWithoutMutation
```

The combined case also exercises the existing coordination and Source Control regressions.
Live Xunit output identifies current native requests and cleanup if the case reaches its inactivity limit.
The focused Copilot lifecycle cases cover cookie/subject/state rejection before exchange, callback replay,
concurrent refresh, transient recovery, permanent rejection, uncertain rotation, account change, and current Core revocation.
These cases use disposable PostgreSQL and controlled external transports, not live OAuth or paid model calls.

## Public BuildTest dispatch

The public suite covers six scenarios: successful completion, lost acknowledgment, command failure, revoked authority, foreign binding, and a stale decision.
It uses broker-issued tokens, current Projects authority, real disposable PostgreSQL checkpoints, and controlled Environment responses.
After plan confirmation, it pins and prepares a real temporary Git workspace through the public SourceControl actions.
Before a completed response, it writes the required artifact and calls the existing authenticated SourceControl output-capture action.
It requires an admitted capture and compares the captured file bytes and digest with the Environment collector evidence.
This positive check uses an empty Git baseline; it does not prove capture from a populated repository or automatic production capture.
The first lost-acknowledgment response and all four negative scenarios create no output capture.
This explicit fixture action does not prove automatic production capture.
It retains the accepted command across execution, recovery, and replay.
Completion compares saved session and checkpoint IDs, then checks the revision, work plan, and decision state.

The separate Core case `ExecutionWitnessAppendUsesProductionCheckpointStoreBinding` appends a newer real checkpoint, then rejects the older witness snapshot.
It checks the canonical witness store, not a race in the public dispatch handler.

A passing public suite reports **six executed, six passed, and zero failures, skips, or warnings**.
Compilation or coverage alone is not a successful run.
These cases do not prove live Kubernetes, paid model execution, deployed services, native SDK execution, or the complete BuildTest provider flow.

After the Release build, run:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName=Agentweaver.Identity.Broker.Tests.ProjectsConfigBrokerAuthorizationTests.PublicBuildTestDispatchRetainsItsIntentAndRejectsChangedAuthority --collect "XPlat Code Coverage" --logger "trx;LogFileName=public-buildtest-dispatch.trx" --results-directory artifacts\tests\public-buildtest-dispatch
```

## P1 retained-surface harness candidates

The [API adapter](https://github.com/sabbour/agentweaver/blob/v1/scripts/api-harness/README.md), [UI adapter](https://github.com/sabbour/agentweaver/blob/v1/scripts/ui-harness/README.md),
and [MCP adapter](https://github.com/sabbour/agentweaver/blob/v1/scripts/mcp-harness/README.md) selectively reuse released 0.x patterns.
They use current v1 contracts, not retired monolith routes or an inherited P0 acceptance result.

```powershell
npm run test:harness
npm run test:harness:api
npm run test:harness:ui
npm run test:harness:mcp
```

These commands run controlled local tests. API tests use loopback HTTP with
controlled contracts; MCP tests use the pinned SDK and a loopback JSON-RPC
server; UI tests use injected page doubles, not a browser. The tests cover the
shared runner creating same-invocation fixtures, reaching the selected API,
MCP, or UI gate/message method, retaining cleanup outcomes, and refusing
unverified caller ownership claims. They do not prove live owner behavior,
native model execution, or deployment.

The P1 adapters contain five bounded journeys:
`gate-answer`, `gate-approve`, `gate-reject`, `message`, and `journal`.
The shared CLI verifies source and deployment evidence before reading
credentials or creating surface clients. It requires an
`executionIntent: "execute"` manifest with Environment identity metadata,
provider/runtime/storage/source/image receipt pins, and `deploymentEvidence`
paths named `publicationPath`, `manifestPath`, `agentHostArchivePath`, and
`integrationDeploymentReceiptPath`. The deployment receipt must come from
`release:deploy-integration` and match the source, tree, manifest, publication,
target namespace, image identities, and service readbacks. `fixturePlan`
requires a positive `expectedPlatformRuntimeRevision`, the exact selection
context, a distinct denied actor, and only the gate or message details needed
by the selected journey. The expected revision is sent through the existing
run-selection request and checked by the owner compare-and-swap; it is not an
authority or ownership receipt. The fixture does not read the PlatformAdmin-only
global runtime-default endpoint. Do not include tokens,
`ownerFences`, or `fixtureScope` in the manifest; caller-created ownership
receipts cannot authorize writes.

After source and deployment checks pass, set
`AGENTWEAVER_P1_SETUP_TOKEN` in the process environment and supply
`--identity-module <authorized-identity-provider.mjs>`. That module returns
already-issued run-bound and denied-actor broker tokens. The runner does not
sign in, refresh tokens, or request consent. For UI runs, also provide
`--page-module <authorized-page-provider.mjs>`, which returns an already
authorized Playwright-compatible page on the selected web origin. The runner
does not start a browser or change its profile.

The runner persists one invocation ID before setup. Projects Config's
`--privileged-project-setup` command uses that ID to create the project, default
configuration, and Owner and Orchestrator assignments for an active TenantAdmin, with
typed setup and readback receipts. The command reads a JSON request from
standard input and writes a typed receipt to standard output. The runner then
reads current authorization and project configuration. It accepts the selected
run context against the plan's expected platform-runtime revision. It creates
the root session and, for a message journey, a child session. Before it runs
the selected surface journey, it reads the exact owner state back.

Cleanup revokes those invocation-owned assignments and archives the project only when no accepted
run selection exists. If a selection exists, cleanup returns
`retainedPending` and keeps the project and assignment active. The command does
not delete SQL, cloud, or Kubernetes resources.

The privileged command requires `ConnectionStrings:ProjectsConfigAuthority`,
`ProjectsConfig:AuthorityDatabase:WorkloadIdentity`,
`ProjectsConfig:PrivilegedSetup:ApprovedIssuer`,
`ProjectsConfig:PrivilegedSetup:ApprovedSubject`, and
`ProjectsConfig:PrivilegedSetup:ApprovedTenantId`. The approved issuer must
match `ProjectsConfig:Authentication:Authority`. The command also uses the
configured Projects authentication audience and a separate authority database
identity. It does not fall back to the runtime or migration identity.

The command authenticates the supplied access token with the existing issuer.
It requires an unbound token, an active TenantAdmin grant, and the
`api.read`, `projects.admin`, and `projects.orchestrator` scopes. The setup
token is sent to the privileged command through standard input, not command
arguments. The runner never invokes an Orchestrator dispatch operation.

Before reading the setup token or importing a UI page provider, the runner
checks the exact clean Git commit/tree, publication receipt, AgentHost archive,
Kubernetes service observations, and public GHCR image identities. Use
repository-relative artifact paths where possible. Credential fields and
caller-supplied ownership receipts are rejected in the manifest.

```powershell
npm run harness:p1 -- --surface api --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs>
npm run harness:p1 -- --surface mcp --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs>
npm run harness:p1 -- --surface ui --input <manifest.json> --output <evidence.jsonl> --identity-module <authorized-identity-provider.mjs> --page-module <authorized-page-provider.mjs>
```

The JSONL output keeps source evidence, owner receipts, surface results, and
cleanup status. A surface can pass while `fullP1Status` remains `blocked`
because the admitted clients cannot produce the required native receipts.
If the output cannot verify whether privileged setup finished, it reports an
indeterminate state; reconcile the invocation before retrying.

API and MCP retries reuse the persisted invocation ID and deterministic
idempotency keys. A UI gate or message action uses an application-generated
key that cannot be restored after a process crash. Before that action, the
runner appends a `p1-ui-action-started` output record; a later invocation
refuses to repeat it. Read the current owner state and reconcile before
starting a new UI run. Read-only UI journal journeys do not use this marker.

The UI adapter requires an already authorized page and exact Broker identity metadata.
It neither starts a browser nor changes its profile or stored credentials.
Gate actions require current owner snapshots and exact request, actor, fence, state version, and answer contracts.
Decision receipts must match that fence and the next state version, including owner denials.
After awaited work, the adapter rechecks the exact page scope before each final action click and replay proof.
Actual denials remain denials.
Message acceptance and local browser echoes cannot prove delivery or gate completion.

The retained journal exposes ordered event DTOs and opaque object references.
The public Gateway has no object-content read route.
The UI adapter checks replay pages against the current DOM, without inventing transcript text.

![Retained journal and owner evidence boundaries](/diagrams/flagship/v1-sessions-journal.png)

The retained surfaces observe owner evidence; they do not create authority from browser output.
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">Open full-size PNG</a> | <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.drawio'">Open editable draw.io source</a></p>

Use the shared redacted JSONL writer and the [P1 judge brief](https://github.com/sabbour/agentweaver/blob/v1/scripts/harness-judge/README.md).
Record exact source, target, scenario, surface, and owner evidence references.
Do not persist transient raw bodies or credential values.
An expected source SHA does not observe a deployment. Closing a browser or
client does not clean up a project, run, session, or Environment; only the
invocation-owned Projects Config cleanup receipt records setup-resource cleanup.

## Validate the documentation site

Run these commands after changing a documentation page, link, or diagram:

```powershell
npm --prefix docs ci
npm run docs:build
npm run docs:check-links
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
npm run test:docs-diagrams
```

The build rejects broken internal links. The link checker verifies local pages, anchors, images, and editable diagram files. The diagram checks compare JSON, draw.io XML, PNG, and hash stamps.

VitePress uses a compatible dependency range; `docs/package-lock.json` preserves reproducible installs.
Reuse an existing compatible installation instead of reinstalling it for a version check.
Diagram export accepts compatible installed draw.io Desktop versions and records the detected version in each hash stamp.
Source, XML, PNG, and rendering-recipe integrity checks remain required.

## Azure acceptance boundary

The Bicep compiler and Kustomize checks run offline. The Foundation Probe runs in tests with local fixtures. Neither operation proves a deployed AKS cluster.

The Azure acceptance command returns `blocked` when source receipts, target evidence, or dependent resources are missing. A missing Azure environment does not pass acceptance.

Deployment requires separate approval for the exact target, subscription, source, and cost. See [Azure acceptance](./azure-acceptance) and [dedicated Azure environment](../architecture/azure).

### Selected Projects runtime profile

Run `node --test scripts/azure/tests/projects-runtime-profile.test.mjs`.
These pure checks cover exact runtime identity and database inputs, missing
configuration rejection, immutable product image references, non-root file-access
settings, private HTTPS, bounded resources, and narrow caller and network rules.
They check the existing provider catalog reference, source commit format,
and environment prefix that isolates catalog keys from other startup settings.
The incomplete example and missing CLI inputs produce errors without output.

The checks do not read a live catalog, verify its contents or source binding,
create database roles, apply migrations, grant rights, publish images, or deploy.
The catalog must exist, remain immutable and nonempty, and pass owner review
before any separately approved deployment.
The real .NET loader remains unchanged.
See the [Projects profile boundary](../architecture/projects-config.md#selected-runtime-profile).

## Planned GitHub Canvas adapter acceptance

The cases are **unexecuted requirements**, not a passing suite or current adapter support.
Research [#1901](https://github.com/sabbour/agentweaver/issues/1901) inspected public
declarations and fake/example scenarios; it did not run interoperability, rendering,
or restart tests. Separate implementation
[#1904](https://github.com/sabbour/agentweaver/issues/1904) consumes the
[bounded declaration/action subset and evidence](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/applications-and-surfaces.md#bounded-adapter-subset).
Reuse [#1878](https://github.com/sabbour/agentweaver/issues/1878) C1 owner-state conformance
instead of adding another lifecycle owner.

| Planned case | Required evidence |
| --- | --- |
| Discovery and validation | Pin the exact SDK/CLI/profile. Expose open/action schemas through retained web and applicable MCP/core catalogs. Reject duplicate or reserved `canvas.` actions, unsupported schemas, remote references, and oversized payloads before dispatch. Use C1's admitted schema subset, not an assumed upstream dialect. |
| Ambiguous types and instance identity | Reject unqualified duplicate provider-local types. Explicit provider selection resolves one enabled kind. Re-open focuses the same bound instance; a new instance remains distinct. |
| Artifact and presentation separation | Bind a verified immutable revision/artifact separately from the surface instance. Changed/missing bytes, unsupported content, arbitrary URLs, unsafe schemes, and unavailable renderers fail explicitly without new run pins or fallback. |
| Agent/user action parity | Invoke the same declared action from an agent/MCP call and the authenticated UI. Validate input and derive actor/session/owner authority server-side. Assert the exact owner effect and journal attribution; browser output never approves a gate or creates a grant. |
| Revocation and stale binding | Revoke authority before invocation and during an owner/bridge wait. No protected effect follows authority loss. Reject stale instances, spoofed identities, cross-session/tenant references, and unknown actions. |
| Duplicate and response loss | Lose the response after owner commit. Retry with the same core operation identity and receive one durable outcome/effect. Changed input conflicts; uncertain external effects do not generate a new identity or blind retry. |
| Warm reconnect and cold resume | Preserve the mounted panel with truthful unavailable state. Restore the same instance/revision with current authorization and a fresh URL, not a saved expired one. Respect durable removal and do not create a new run or duplicate action. |
| Bridge and renderer failure | Preserve distinct sanitized structured outcomes for invalid input, handler failure, timeout, dropped connection, missing bytes, load failure, and unsupported capability. Provider `status: ready` alone cannot mark content render-ready. |
| Non-destructive close | Inject `onClose` failure. Closing the view leaves run, journal, revision, and artifact intact; reopen retains the revision. Cleanup remains pending/failed until its actual owner receipt exists. |
| Host isolation and accessibility | Accept only the expected authenticated origin/session/instance and declared action. Reject credential exposure and arbitrary message-to-tool dispatch. Exercise keyboard open/focus/form/action/close, accessible names, focus restoration, and readable failures. This tests Agentweaver's bridge, not a recovered upstream `postMessage` protocol. |
| Revision affinity | A new adapter/content revision does not change an existing instance's accepted pins. New bindings are explicit. Exercise applicable C4 upgrade/drain/retention behavior for bundled content without making every bundle slice a prerequisite. |
| Evidence and claim boundaries | Record exact fixtures and SDK/CLI/profile provenance. Controlled contract/web/MCP tests are not live interoperability. Separately approved exact-SHA AKS C5 acceptance must exercise real retained UI/core/MCP, reconnect, and owned cleanup before deployment/publication claims. |

Implementation requires admitted research, C1, and the retained web/core action
contracts from [#1859](https://github.com/sabbour/agentweaver/issues/1859) and
[#1857](https://github.com/sabbour/agentweaver/issues/1857), with applicable
[#1858](https://github.com/sabbour/agentweaver/issues/1858) MCP operations.
It does not require all P1, the whole bundle epic, A2UI, Cosmos, Redis, or P3.
Passing documentation checks supplies no permission for live deployment, credentials,
paid execution, publication, or destructive cleanup.

## Environment remote MCP connection records

Disposable PostgreSQL tests cover connection migrations, exact Identity-binding
revisions and digests, replay after restart, changed authority, and immutable history.
Runtime-role tests reject updates to configuration history and physical deletion.
Pure parser tests cover exact Registry versions, static HTTPS endpoints, duplicate
JSON fields, tool names, schema shapes, and byte limits.
These tests do not prove live discovery, OAuth credential use, applied L7 policy,
or a protected remote request.

Project skill content has pure tests for manifest/resource validation, normalized
path collisions, digest stability, immutable object writes, and missing or corrupt
objects. The Projects Config PostgreSQL test verifies idempotent imports, revision
conflicts, assignment to active agents, and reads of the exact accepted revision
and resources. The inventory PostgreSQL tests verify unassigned and revoked
revision metadata, exact current assignments and configuration revisions,
stable bounded pages, and current project-admin and cross-project denial. They
require the repository's Testcontainers PostgreSQL fixture.
