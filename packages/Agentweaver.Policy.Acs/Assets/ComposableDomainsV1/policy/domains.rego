package agent_control_specification.agentweaver_domains_v1

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := data.agent_control_specification.agentweaver_actions.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_actions"
}

pre_model_call_verdict := data.agent_control_specification.agentweaver_platform_network.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_platform_network"
}
