package agent_control_specification.agentweaver_model_use

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := {"decision": "deny"} if {
	some rule in data.model_use.rules
	rule.effect == "deny"
	rule_matches(rule)
}
else := {"decision": "allow"} if {
	some rule in data.model_use.rules
	rule.effect == "allow"
	rule_matches(rule)
}

rule_matches(rule) if {
	rule.model_selection_reference == input.policy_target.value.model_use.model_selection_reference
	rule.model_id == input.policy_target.value.model_use.model_id
	rule.source_mode == input.policy_target.value.model_use.source_mode
	rule.provider_type == input.policy_target.value.model_use.provider_type
	rule.provider_id == input.policy_target.value.model_use.provider_id
	rule.configuration_revision == input.policy_target.value.model_use.configuration_revision
	rule.configuration_hash == input.policy_target.value.model_use.configuration_hash
}
