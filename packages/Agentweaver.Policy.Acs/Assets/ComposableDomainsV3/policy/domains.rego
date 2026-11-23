package agent_control_specification.agentweaver_domains_v2

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := data.agent_control_specification.agentweaver_actions.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_actions"
}

pre_model_call_verdict := data.agent_control_specification.agentweaver_platform_network.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_platform_network"
}

pre_model_call_verdict := data.agent_control_specification.agentweaver_model_use.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_model_use"
}

pre_model_call_verdict := data.agent_control_specification.agentweaver_guardrails.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_guardrails"
}

pre_model_call_verdict := data.agent_control_specification.agentweaver_tool_permissions.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_tool_permissions"
}

pre_model_call_verdict := data.agent_control_specification.agentweaver_skills.pre_model_call_verdict if {
	input.policy_target.value.domain_id == "agentweaver_skills"
}
