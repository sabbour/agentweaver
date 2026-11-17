# Specification: Composable native ACS domains

**Status:** Implemented in source. Target: v1.1.0.

This specification defines a fixed composition profile for the native ACS
library. It combines reviewed action and platform-network rules in one
artifact. It does not activate ACS or add an API consumer.

## Problem and goals

The finite network profile accepts one reviewed network module and its data.
The action policy uses a separate adapter. A future complete artifact needs a
fixed way to carry both domains without allowing one domain's data or input
facts to affect the other.

The profile must keep unqualified validation and evaluation unchanged. A
caller must select a domain through a typed native-library entrypoint.

## Non-goals

This slice adds no authored rules, custom modules, shared data between
domains, additional domains, API consumer, write gate, runtime admission, or
service topology. Projects & Config keeps its current write default; the v4
Policy path and deployed enforcement remain unchanged. Actions-plus-network is
the first composition; other domains need separate issues.

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

The artifact contains exactly the reviewed manifest, three reviewed Rego
modules, and two domain data files. The selector and manifest accept only
`pre_model_call`. The native caller supplies a typed `NativeAcsDomainId`;
evaluation places that selection in the model request. Separate input-fact
allowlists restrict the action and network rules. The selector reads only
the selected `domain_id` and is the only module that calls both domain rules.

`NativeAcsComposableDomainsProfile.Inspect` validates the exact manifest,
module bytes, file closure, data schemas, and complete artifact hash.
`ClassifyChanges` accepts data-only changes and reports which domain data
changed. Changes to the manifest, modules, or file closure fail validation.
`NativeAcsArtifactValidator.ValidateComposed` validates this profile.
`Validate` remains on the existing profile path and rejects composed
artifacts.

`NativeAcsPolicyEvaluator.EvaluateComposedAsync` evaluates the selected domain
only. An unselected domain's default deny does not affect the result.
`EvaluateAsync` keeps its existing behavior. The Environment compiler has
separate composed and composed-System-only candidate methods. Both preserve
the hash of the complete manifest and every file. The existing compiler
entrypoints keep accepting only the finite network profile.

## Contracts and boundaries

`Agentweaver.Policy.Acs` owns the profile, its validation, domain selection,
and native evaluation. Environment owns composed egress-candidate compilation.
Projects & Config does not consume this profile in this slice. No method
creates a Platform Policy write or an admitted enforcement snapshot.

The conservative owner defaults are:

1. Use fixed reviewed rule patterns. Do not accept authored rules or share
   data between domains.
2. Ship actions-plus-network composition first. Add other domains in separate
   issues.

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

Correct the artifact or dependency and retry validation. No state changes during
validation, evaluation, or candidate compilation.

## Acceptance and validation

| Criterion | How to verify | State |
| --- | --- | --- |
| Exact profile and isolated domain schemas | Policy ACS unit tests | Implemented |
| Selected decisions ignore the other domain's deny | `ComposedNativeSelectorKeepsDomainDecisionsIndependent` with Linux native ACS and pinned OPA | Required in Linux CI |
| Existing finite-network entrypoints reject composed artifacts | Validator and Environment compiler tests | Implemented |
| Full source hash survives normal and System-only candidate compilation | Environment compiler tests | Implemented |
| Documentation and source-map coverage | Docs build, link, and drift checks | Candidate validation |

The `native-acs` CI shard runs on Ubuntu, verifies OPA 0.70.0 against the
locked SHA-256, and fails if a required native test is skipped or does not
pass. A Windows skip is not acceptance evidence.

## Rollout and compatibility

The profile is library support only. No service selects it, no API route
accepts it, and no policy write or runtime admission is enabled. Existing
unqualified APIs retain their current profile behavior. The change requires
no migration, image, or deployment update.

## Open questions and decisions

The two owner defaults above remain available for override before later domain
additions. Other domains need their own profile and compatibility review.
