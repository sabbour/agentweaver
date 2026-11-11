package agent_control_specification.agentweaver_platform_network

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

destination_is_approved if {
	some destination in data.agentweaver.platform_egress.approved
	destination.host == input.policy_target.value.network.destination.value
	destination.port == input.policy_target.value.network.port
}

destination_is_system if {
	some destination in data.agentweaver.platform_egress.system.destinations
	destination.host == input.policy_target.value.network.destination.value
	destination.port == input.policy_target.value.network.port
}

destination_is_known if {
	destination_is_approved
}

destination_is_known if {
	destination_is_system
}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.network.check_kind == "packet"
	input.policy_target.value.network.protocol == "TCP"
	input.policy_target.value.network.port == 443
	input.policy_target.value.network.destination.kind in {"dns", "ip"}
	is_string(input.policy_target.value.network.destination.value)
	data.agentweaver.platform_egress.mode == "PublicHttps"
}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.network.check_kind == "packet"
	input.policy_target.value.network.protocol == "TCP"
	input.policy_target.value.network.port == 443
	input.policy_target.value.network.destination.kind == "dns"
	is_string(input.policy_target.value.network.destination.value)
	data.agentweaver.platform_egress.mode == "Allowlist"
	destination_is_known
}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.network.check_kind == "known_destination_use"
	input.policy_target.value.network.protocol == "TCP"
	input.policy_target.value.network.port == 443
	input.policy_target.value.network.destination.kind == "dns"
	is_string(input.policy_target.value.network.destination.value)
	destination_is_known
}
