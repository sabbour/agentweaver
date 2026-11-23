package agent_control_specification.agentweaver_skills

import rego.v1

default pre_model_call_verdict := {"decision": "deny"}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.skills.built_in_exempt == true
}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.skills.built_in_exempt == false
	input.policy_target.value.skills.evidence_complete == true
	input.policy_target.value.skills.trust_score >= data.skills.minimumScore
}

pre_model_call_verdict := {"decision": "allow"} if {
	input.policy_target.value.skills.built_in_exempt == false
	data.skills.requireCompletedEvidence == false
	input.policy_target.value.skills.trust_score >= data.skills.minimumScore
}
