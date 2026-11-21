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
keeps its current write default. The v4 Policy path, deployed enforcement, API
schema, storage schema, approval flow, and service topology remain unchanged.

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

`composable-domains-v2` adds four more reviewed files and keeps the actions
and network files unchanged:

| Domain or component | Policy ID or path | Rule |
| --- | --- | --- |
| Model use | `agentweaver_model_use`, `policy/model-use.rego` | Allows only an exact match for model reference, model ID, source mode, provider type and ID, configuration revision, and configuration hash. An explicit deny wins. |
| Model-use data | `policy/model_use/data.json` | Contains a closed list of exact `allow` or `deny` rules. Missing provider fields are represented as `null`; they are not wildcards. |
| Guardrails | `agentweaver_guardrails`, `policy/guardrails.rego` | Requires the ordered `UserInput`/`Text` checks and denies blocked normalized findings. |
| Guardrails data | `policy/guardrails/data.json` | Contains exact ordered provider, adapter, schema, and options revisions plus blocked finding code and severity tuples. |
| Selector | `agentweaver_domains_v2`, `policy/domains.rego` | Selects one of the four domains at `pre_model_call`. |

The unified profile rejects unknown fields, duplicate keys and rules, additional
files, changed templates, unsupported stages, approval settings, and budget
facts. It accepts an empty Guardrails check list. The Orchestrator treats that
list as inactive.

The opt-in runtime source boundary receives an accepted Policy snapshot and a
current snapshot. It independently validates and evaluates every applicable
revision. Its admission binding records each scope, Policy ID, revision, and
complete artifact hash with the exact request and registration hashes.
Verification fails when that binding changes. The gate does not substitute a
different model or provider.

The existing Guardrails resolver still owns provider registration, adapter and
options matching, resource pins, and classifier execution. The native result narrows the existing AGT and classifier result. A denied, unavailable, stale,
revoked, or mismatched native result prevents the model send. Budget checks and
their evidence remain independent.

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
2. Register the runtime source and admission store only through a later
   activation decision. Source support does not grant runtime authority.

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
| Native Policy denies, errors, changes revision, or no longer matches the model pin | Runtime admission or verification fails before the model send. |

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
| Documentation and source-map coverage | Docs build, link, and drift checks | Candidate validation |

The `native-acs` CI shard runs on Ubuntu, verifies OPA 0.70.0 against the
locked SHA-256, and fails if a required native test is skipped or does not
pass. A Windows skip is not acceptance evidence.

## Rollout and compatibility

The profile is source support only. No production service registers the
opt-in runtime composition, no API route accepts a new Policy form, and no
policy write or runtime activation is enabled. Existing unqualified APIs
retain their current profile behavior. The change requires no migration,
image, deployment, or publication.

## Open questions and decisions

Activation needs the separately approved exact-scope Policy source, durable
admission fence, owner receipt link, and compatible native assets. Approval
and unattended rules remain outside this profile version.
