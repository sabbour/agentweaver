package agent_control_specification.agentweaver_tool_permissions

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := {"decision": "allow"} if {
	some rule in data.tool_permissions.rules
	rule.tool_name == input.policy_target.value.tool.tool_name
	rule.action_id == input.policy_target.value.action_id
	rule.phase == input.policy_target.value.tool.phase
	rule.permission_kind == input.policy_target.value.tool.permission_kind
	rule.read_access == input.policy_target.value.tool.read_access
	rule.write_access == input.policy_target.value.tool.write_access
	rule.shell_access == input.policy_target.value.tool.shell_access
}
