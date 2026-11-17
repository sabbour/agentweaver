package agent_control_specification.agentweaver_actions

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.action_id in data.agentweaver_actions.allowed_action_ids
}
