package agent_control_specification.agentweaver_tool_permissions

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := {"decision": "allow"} if {
	some rule in data.tool_permissions.rules
	tool_name_matches(rule.tool_name, input.policy_target.value.tool.tool_name)
	rule.action_id == input.policy_target.value.action_id
	rule.phase == input.policy_target.value.tool.phase
	rule.permission_kind == input.policy_target.value.tool.permission_kind
	rule.read_access == input.policy_target.value.tool.read_access
	rule.write_access == input.policy_target.value.tool.write_access
	rule.shell_access == input.policy_target.value.tool.shell_access
	remote_mcp_allowed
}

tool_name_matches(pattern, actual) if {
	pattern == actual
}

tool_name_matches(pattern, actual) if {
	pattern == "*"
	input.policy_target.value.tool.remote_mcp
}

remote_mcp_allowed if {
	not input.policy_target.value.tool.remote_mcp
}

remote_mcp_allowed if {
	remote := input.policy_target.value.tool.remote_mcp
	remote.scan_evidence.contractVersion == 1
	remote.scan_evidence.scannerId == "agentweaver.environment.remote-mcp-catalog"
	remote.scan_evidence.scannerVersion == "1.0.0"
	remote.scan_evidence.state == "Completed"
	remote.scan_evidence.catalogSha256 == remote.catalog_digest
	remote.scan_evidence.configurationRevision == remote.connection_configuration_revision
	remote.scan_evidence.discoveryRevision == remote.connection_discovery_revision
	every requirement in data.tool_permissions.remote_mcp_scan_requirements {
		remote_mcp_requirement_met(requirement, remote.scan_evidence.checks)
	}
}

remote_mcp_requirement_met(requirement, checks) if {
	not requirement.required
}

remote_mcp_requirement_met(requirement, checks) if {
	requirement.required
	some check in checks
	check.checkId == requirement.check_id
	check.outcome == "Pass"
}

remote_mcp_requirement_met(requirement, checks) if {
	requirement.required
	some check in checks
	check.checkId == requirement.check_id
	check.outcome == "Fail"
	check.riskScore <= requirement.maximum_risk_score
}
