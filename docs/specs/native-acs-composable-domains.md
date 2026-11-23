# Specification: Composable native ACS domains

**Status:** Implemented in source. Target: v1.1.0.

This specification defines fixed composition profiles for the native ACS
library. The first profile combines reviewed action and platform-network
rules. The second profile also carries exact model-use and Guardrails rules.
The profiles do not activate ACS or add a production Policy source consumer.

## Problem and goals

The finite network profile accepts one reviewed network module and its data.
The action policy uses a separate adapter. A future complete artifact needs a
fixed way to carry both domains without allowing one domain's data or input
facts to affect the other.

The profile must keep unqualified validation and evaluation unchanged. A
caller must select a domain through a typed native-library entrypoint.

## Non-goals

The library accepts only reviewed rule patterns. The service hosts do not
register the `RuntimeNativePolicyGate` source composition. Projects & Config
keeps its current write default. Production enforcement and service topology
remain unchanged. Approval flow support is limited to one exact native
Guardrails escalation and the existing coordinator approval gate.

## Intended behavior and requirements

The `composable-domains-v1` profile fixes the manifest, module bytes, file
paths, policy IDs, and input facts:

| Domain or component | Policy ID or path | Rule |
| --- | --- | --- |
| Actions | `agentweaver_actions`, `policy/actions.rego` | Allows an action ID listed in `data.actions.allowed_action_ids`; otherwise denies. |
| Platform network | `agentweaver_platform_network`, `policy/network.rego` | Uses the existing finite network profile and its validated System catalog. |
| Selector | `agentweaver_domains_v1`, `policy/domains.rego` | Reads the server-selected `domain_id` and invokes only that domain at `pre_model_call`. |
| Actions data | `policy/actions/data.json` | Contains only the closed `allowed_action_ids` schema in `data.actions`. |
| Network data | `policy/data.json` | Contains only the existing finite network data schema. |

The Platform artifact contains exactly the reviewed manifest, three reviewed
Rego modules, and two domain data files. A Project artifact uses the same
manifest and modules but omits `policy/data.json`. Project validation rejects
that file when present. The selector and manifest accept only
`pre_model_call`. The native caller supplies a typed `NativeAcsDomainId`;
evaluation places that selection in the model request. Separate input-fact
allowlists restrict the action and network rules. The selector reads only
the selected `domain_id` and is the only module that calls both domain rules.

`NativeAcsComposableDomainsProfile.Inspect` validates the exact manifest,
module bytes, file closure, data schemas, and complete artifact hash.
`ClassifyChanges` accepts data-only changes and reports which domain data
changed. Changes to the manifest, modules, or file closure fail validation.
`NativeAcsArtifactValidator.ValidateComposed` validates Platform artifacts.
`ValidateComposedProject` validates Project artifacts using the same profile.
`Validate` remains on the existing profile path and rejects composed
artifacts.

`NativeAcsPolicyEvaluator.EvaluateComposedAsync` evaluates the selected domain
only. An unselected domain's default deny does not affect the result.
`EvaluateAsync` keeps its existing behavior. The Environment compiler has
separate composed and composed-System-only candidate methods. Both preserve
the hash of the complete manifest and every file. The existing compiler
entrypoints keep accepting only the finite network profile.

## Opt-in model-use and Guardrails profile

`composable-domains-v2.1` adds six reviewed files for model, Guardrails, and
tool permissions. It keeps the actions and network modules unchanged. The
validator also accepts historical pinned `composable-domains-v2` artifacts with
the exact legacy `tool-permissions.rego` module and its scan-less data shape.
`composable-domains-v3` adds the Skills domain and keeps the existing domain
files unchanged:

| Domain or component | Policy ID or path | Rule |
| --- | --- | --- |
| Model use | `agentweaver_model_use`, `policy/model-use.rego` | Allows only an exact match for model reference, model ID, source mode, provider type and ID, configuration revision, and configuration hash. An explicit deny wins. |
| Model-use data | `policy/model_use/data.json` | Contains a closed list of exact `allow` or `deny` rules. Missing provider fields are represented as `null`; they are not wildcards. |
| Guardrails | `agentweaver_guardrails`, `policy/guardrails.rego` | Requires the ordered `UserInput`/`Text` checks, denies blocked findings, and escalates exact configured findings. |
| Guardrails data | `policy/guardrails/data.json` | Contains exact ordered provider, adapter, schema, and options revisions plus hard blocked and bounded escalation tuples. |
| Tool permissions | `agentweaver_tool_permissions`, `policy/tool-permissions.rego` | Matches the exact SDK or reviewed remote tool, owner action, callback phase, permission kind, capabilities, and required Remote MCP scan results. |
| Tool-permission data | `policy/tool_permissions/data.json` | Contains the closed set of tool permission rules and the Remote MCP required-check and risk-threshold list. |
| Skills | `agentweaver_skills`, `policy/skills.rego` | Allows an owner-verified built-in exemption or an exact server-owned score at or above the policy threshold when required evidence is complete. |
| Skills data | `policy/skills/data.json` | Contains only `schemaVersion`, `minimumScore`, and `requireCompletedEvidence`. |
| Selector | `agentweaver_domains_v2`, `policy/domains.rego` | Selects one of the domains at `pre_model_call`. |

The tool-permission data keeps `create` and `edit` as separate names:

```json
{"remote_mcp_scan_requirements":[
  {"check_id":"mcp.accepted-baseline-drift","required":false,"maximum_risk_score":100}
],"rules":[
  {"tool_name":"view","action_id":"tool.read","phase":"invocation","permission_kind":null,"read_access":true,"write_access":false,"shell_access":false},
  {"tool_name":"view","action_id":"tool.read","phase":"permission","permission_kind":"read","read_access":true,"write_access":false,"shell_access":false},
  {"tool_name":"edit","action_id":"tool.write","phase":"invocation","permission_kind":null,"read_access":false,"write_access":true,"shell_access":false}
]}
```

Remote MCP policies add the required checks and their maximum risk scores at
the data root. The default policy requires poisoned-description, hidden
Unicode, look-alike-name, and cross-server-collision checks at score `0`.
Baseline drift is an optional observation:

```json
{"remote_mcp_scan_requirements":[
  {"check_id":"mcp.poisoned-description","required":true,"maximum_risk_score":0},
  {"check_id":"mcp.hidden-instructions-unicode","required":true,"maximum_risk_score":0},
  {"check_id":"mcp.lookalike-name","required":true,"maximum_risk_score":0},
  {"check_id":"mcp.cross-server-collision","required":true,"maximum_risk_score":0},
  {"check_id":"mcp.accepted-baseline-drift","required":false,"maximum_risk_score":100}
]}
```

Remote tool facts bind the exact reviewed snapshot, gateway descriptor and
enforcement generation, transport options, and owner scan evidence. A failed,
missing, stale, errored, or required `Unknown` check denies. Optional
observations remain `Unknown` without changing the decision.

Tool permission facts do not carry command text, file contents, credentials,
or display diffs. The runtime supplies exact bounded identity and digest fields.
Workspace containment is a separate local check and always runs for SDK file
and shell callbacks.

The unified profile rejects unknown fields, duplicate keys and rules, additional
files, changed templates, unsupported stages, free-form approval settings, and
budget facts. Guardrails data may add the bounded
`guardrails-escalation-v1` rule. A rule names one exact normalized
check/code/severity tuple. Hard blocked findings win over escalation. A
non-blocked escalated finding produces a native `Escalate` verdict.

The native `PolicyInput` adds the owner-verified `turn_id` and exact
lower-case SHA-256 `arguments_digest`. It never adds prompt bytes. The
`ApprovalResolver` must bind the retained input and native approval handle.
Changed turn or digest values reject resolution. `require_approval`,
auto-approval, and `on_timeout: allow` are invalid profile data. The profile
accepts no unattended approval mode. It accepts an empty Guardrails check list.
The Orchestrator treats that list as inactive. Projects & Config uses the
greater Platform and Project Skills
score threshold. A completed-evidence requirement in either scope remains in
force. Capability declarations stay as Skill requests; they do not become ACS
permissions.

The runtime source boundary reads accepted and current Policy snapshots. Tool
callbacks use the current Platform and Project revisions as the retained
invocation snapshot. The gate independently validates and evaluates every
applicable revision. Its admission binding records each scope, Policy ID,
revision, and complete artifact hash with the exact request and registration
hashes. Verification fails when that binding changes or when a secondary
permission does not match its retained invocation. The gate does not substitute
a different model or provider.

The existing Guardrails resolver still owns provider registration, adapter and
options matching, resource pins, and classifier execution. When the native
composition is present, native Guardrails policy owns normalized finding
decisions. Independent action policy, model permission, dispatch authorization,
budgets, and current-authority checks remain active. A denied, unavailable,
stale, revoked, or mismatched native result prevents the model send.

An escalation first saves the owner turn ID, operation ID, argument digest,
policy-input digest, and retained native evaluation. The Orchestrator then opens
one idempotent coordinator approval request. Replays recover that record before
opening another request. A different pending gate returns a collision. After an
exact approval receipt arrives, the retry resolves the retained native
evaluation, checks current authority and restrictions, and consumes the handoff
once. The handoff does not authorize another operation or a sibling turn.

## Contracts and boundaries

`Agentweaver.Policy.Acs` owns the profile, its validation, domain selection,
and native evaluation. Environment owns composed egress-candidate compilation.
Projects & Config uses Project-scoped validation for submitted artifacts.
That route does not create a Policy write or an admitted enforcement snapshot.
The Project network domain is not selected. A future consumer must select
Platform network from the Platform scope and apply hard-deny precedence.
The current evaluator rejects a Project artifact with omitted network data.

The conservative owner defaults are:

1. Use fixed reviewed rule patterns. Do not accept authored rules or share
   data between domains.
2. Register the runtime source and admission store with the runtime owner.
   A missing evaluator, source, or binding denies native tool callbacks.

The owner can override these defaults before later domain additions.

## Failure and recovery

| Condition | Result |
| --- | --- |
| Wrong manifest, module bytes, paths, or data schema | Validation fails with diagnostics. |
| Artifact hash does not cover the supplied bytes | Validation fails with `artifact_hash`. |
| Unsupported domain or intervention point | Evaluation returns `domain_intervention_point_unsupported`. |
| Missing, invalid, or unavailable native/OPA dependency | Evaluation returns an error; it never allows. |
| Invalid selected-domain facts | The selected policy follows its existing deny or validation behavior. |
| Cleanup fails after evaluation | The evaluator returns an error, including after an allow or deny. |
| Invalid data-only update | Classification rejects the artifact; no candidate is produced. |
| Native Policy denies, errors, or changes revision | Runtime admission or verification fails before the model send or native tool effect. |

Correct the artifact or dependency and retry validation. No state changes during
validation, evaluation, or candidate compilation.

## Acceptance and validation

| Criterion | How to verify | State |
| --- | --- | --- |
| Exact profile and isolated domain schemas | Policy ACS unit tests | Implemented |
| Selected decisions ignore the other domain's deny | `ComposedNativeSelectorKeepsDomainDecisionsIndependent` with Linux native ACS and pinned OPA | Required in Linux CI |
| Existing finite-network entrypoints reject composed artifacts | Validator and Environment compiler tests | Implemented |
| Full source hash survives normal and System-only candidate compilation | Environment compiler tests | Implemented |
| Exact model/provider tuple and Guardrails result | `UnifiedNativeModelAndGuardrailsDecisionsAreHashBound` and `RuntimeNativePolicyGateTests.NativePolicyBlocksEveryWithheldModelSendAndBindsRevision` | Required in Linux CI |
| Retained native approval binds exact turn and arguments | `NativeGuardrailsEscalationRetainsExactTurnAndDigestAcrossApprovalResolution` | Required in Linux CI |
| Documentation and source-map coverage | Docs build, link, and drift checks | Candidate validation |

The `native-acs` CI shard runs on Ubuntu, verifies OPA 0.70.0 against the
locked SHA-256, and fails if a required native test is skipped or does not
pass. A Windows skip is not acceptance evidence.

## Rollout and compatibility

Production registers the runtime Policy source and admission store. Native tool
callbacks use the composed profile before owner approval release. No API route
accepts a new Policy form, and no Policy write is added. Existing unqualified
APIs retain their current profile behavior.

## Open questions and decisions

Activation needs the separately approved exact-scope Policy source, durable
admission fence, owner receipt link, and compatible native assets. Approval
syntax is accepted only when the pinned Linux native resolver test passes.
