package agent_control_specification.agentweaver_guardrails

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := {"decision": "escalate"} if {
	input.policy_target.value.guardrails.stage == "userInput"
	input.policy_target.value.guardrails.content_class == "text"
	ordered_checks_match
	not blocked_finding
	escalated_finding
}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.guardrails.stage == "userInput"
	input.policy_target.value.guardrails.content_class == "text"
	ordered_checks_match
	not blocked_finding
	not escalated_finding
}

ordered_checks_match if {
	data.guardrails.required_checks == input.policy_target.value.guardrails.checks
}

blocked_finding if {
	some blocked in data.guardrails.blocked_findings
	some finding in input.policy_target.value.guardrails.findings
	blocked == finding
}

escalated_finding if {
	some rule in data.guardrails.escalation.rules
	data.guardrails.escalation.version == "guardrails-escalation-v1"
	some finding in input.policy_target.value.guardrails.findings
	rule == finding
}
