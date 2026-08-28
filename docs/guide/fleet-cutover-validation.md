---
title: Two-App cutover validation
---

# Two-App cutover validation

This is the release-validation matrix for the two-GitHub-App Fleet migration
([#938](https://github.com/sabbour/agentweaver/issues/938)). It is a release
gate, not a compatibility guide: Microsoft Entra remains the product sign-in,
and the final cutover has no legacy GitHub OAuth or device-flow lane.

Run this matrix only against the exact integrated release candidate after
[#951](https://github.com/sabbour/agentweaver/issues/951) has merged. Earlier
Fleet layers have focused tests of their own; do not add final-cutover tests to
those layers when they would assert an endpoint, tool, or sandbox adapter that
has not shipped.

## Entry criteria

Before beginning, record the release-candidate SHA, deployed image digests,
public API origin, both App IDs, and the source of the production callback
URLs. Use disposable test projects, two distinct human Entra subjects, two
distinct GitHub repositories, and a non-member/unauthenticated client. Never
place a production credential, App PEM, App JWT, installation token, or OAuth
code in test fixtures or captured evidence.

The required dependency order is:

1. Contracts and additive storage: #939, #940.
2. Repo App authorization and installation/webhook authority: #941, #942.
3. Project Copilot binding and broker: #943, #944.
4. Project creation, settings/readiness, sandbox adapters, and activation:
   #945, #946, #947, #948.
5. MCP and Web completion: #949, #950.
6. Direct cutover: #951.
7. This validation gate: #952.

Do not waive a lower-numbered incomplete gate by testing a higher layer.

## Dependency-ordered matrix

| Gate | Required integrated work | Validation and expected evidence | Hard failure |
| --- | --- | --- | --- |
| F0 — candidate integrity | #951 plus all preceding Fleet issues | Run the required CI against the exact SHA. Record migrations, rendered manifests, image provenance, and the API/MCP/Web image digest mapping. | Different SHA, mutable image tag, failed required check, or a manifest not rendered from the candidate. |
| F1 — production App readiness | #941, #942, #943, #946, #951 | Verify the two App registrations are distinct. Confirm the Repo App callback and Copilot App callback use the deployed public API origin and their final App-specific routes; do not reuse the former callback. Confirm the Copilot App reports zero repository permissions, the Repo App does not request user OAuth during installation, and only the API identity can read the Repo App PEM/webhook-secret paths. | Shared App credentials or IDs, incorrect callback route/origin, Copilot repository permission, Repo App installation OAuth, or PEM access outside the API identity. |
| F2 — storage and migration | #940, #951 | On a production-like database copy, apply the forward migrations once, restart all API replicas, and verify durable two-App records, constraints, project cascades, append-only audit records, and immutable snapshots. Re-run migration discovery without applying a second change. Exercise the documented SQLite-to-PostgreSQL transfer failure injection and prove no partial two-App project records remain. | Migration failure, provider-schema drift, partial transfer, duplicate active binding/claim, persisted credential material, or a `Down`/rollback path that restores legacy credential storage. |
| F3 — REST authorization and isolation | #941, #942, #943, #944, #945, #946, #948, #951 | With owner A, owner B, a human platform admin, an internal/shared API key, and an unauthenticated client, exercise every shipped credential-mutating REST operation. Prove human Entra subject and current Owner checks before bind/replace/activation; prove the documented human-admin disconnect exception does not authorize binding. Redeem transactions with wrong subject, missing/wrong callback cookie, expired state, replay, project substitution, and Owner loss. Verify repository routing uses the exact numeric installation and repository IDs after a rename/transfer. | A non-human/internal client mutates credentials, a project crosses owner boundaries, a replay succeeds, a current/default credential is substituted, or a repository name affects authorization. |
| F4 — MCP parity | #949, #951 | From an authenticated MCP client, start each shipped browser handoff and poll it as its initiator, a different subject, and an API-key-only client. Verify the returned transaction identifier is opaque and distinct from OAuth state; polling reveals only the safe lifecycle state. Verify every legacy MCP alias is absent after cutover and MCP errors map only to the shared capability state codes. | Cross-subject polling, leaked state/code/credential/provider detail, an API-key mutation path, a legacy alias, or a broader MCP authorization result than REST. |
| F5 — Web parity | #945, #946, #950, #951 | Use the Web UI to create a GitHub-backed project, manage its Repo App/Copilot readiness, begin/redeem bindings, and configure or invalidate unattended work. Confirm interactive and unattended states are explicit, failure states are actionable, and browser callbacks return only approved routes. Repeat as a non-owner and after role removal. | UI exposes a capability unavailable to REST/MCP, accepts an arbitrary return URL, silently falls back to legacy OAuth, hides an unsafe state, or allows cross-project binding. |
| F6 — broker, run identity, and sandbox isolation | #944, #947, #948, #951 | Launch a GitHub-backed run, child run, retry, and resume. Change, revoke, expire, or rotate each backing source and prove the affected operation fails closed rather than resolving another account, binding, installation, or default. Inspect API, AgentHost, executor, preview, child-process, workspace, volume, command-line, and environment evidence while exercising repository read/write actions. Confirm the backend adapter returns only sanitized, operation-specific data and no process receives a Repo App authorization, installation token, App JWT, PEM, or legacy credential variable. | Any credential reaches a model-controlled surface; direct sandbox `git`, `gh`, REST, GraphQL, or credential-helper path reaches GitHub; a retry/resume resolves a new identity; raw provider data or credentials appear in logs, events, audit, or responses. |
| F7 — webhook and automation isolation | #942, #946, #948, #951 | Deliver valid and invalid signed App webhook requests at the configured size/timeout boundary. Test active and bounded-previous secret rotation, duplicate delivery IDs, canonical installation/repository matches, renamed repositories, removed grants, and permission expansion and reduction. Confirm only the matching project is affected and activation/readiness invalidates on any authority change. | Signature/body-limit bypass, replay creates duplicate work, display name routes a delivery, a foreign project is affected, or changed permissions leave unattended activation live. |
| F8 — residual cutover and rollback boundary | #951 | Run the residual-reference CI scan over shipping assemblies, configuration/deployment manifests, MCP manifests, and published docs. Separately inspect deployed configuration and Key Vault references. Verify legacy OAuth/device-flow endpoints, aliases, secret objects, grants, records, and docs are absent. Rehearse the only supported rollback boundary: stop rollout before destructive cleanup, preserve evidence/data, and redeploy the prior known-good candidate only when its required backing configuration still exists. | Any legacy runtime/configuration/documentation reference remains, a runtime switch or fallback survives, or the plan attempts to recreate deleted legacy credentials/storage after the irreversible cleanup. |
| F9 — staged deployment and sign-off | F0–F8 | Deploy the immutable candidate through the normal deployment workflow, then run `npm run azure:verify` and inspect API, MCP, Web, Gateway, database, Key Vault access, and AgentHost/Kata readiness. Execute the F3–F7 smoke paths against the deployed origin. Monitor typed audit events, webhook outcomes, capability-denial rates, and sandbox startup failures during the agreed observation window; attach sanitized evidence to the release record. | Health/provenance verification fails, any Fleet security gate regresses in the deployed environment, unexpected credential-related diagnostics appear, or the observation window contains unexplained authorization/isolation failures. |

## Executable regression coverage

The current independent architecture regression is
`TwoAppCredentialArchitectureTests.TwoAppContract_HasOnlyPurposeBoundRepoAndCopilotAppCapabilities`.
It locks the foundational contract to exactly the Repo and Copilot Apps and
their four explicit interactive/unattended purposes. It is intentionally
limited to the merged foundation and does not predict #945, #949, #950, or
#951 public surfaces.

Existing focused coverage remains the release prerequisite for transaction
single use, binding exclusivity, provider-derived numeric repository authority,
permission-change invalidation, snapshot fencing/inheritance, broker operation
separation, and run-bound repository credential cleanup. The final
residual-reference test belongs to #951 because it must fail until that issue
removes the legacy implementation.

## Deployment commands

From a clean checkout at the candidate SHA:

```bash
npm run validate:full
npm run azure:deploy-from-commit -- <candidate-sha>
npm run azure:verify
```

Use `kubectl get pods,gateway,httproute,pvc -n agentweaver` and
`kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` as
supporting deployment evidence. These commands do not replace the cross-surface
tests above.
