package agent_control_specification.image_probe

import rego.v1

default pre_model_call_verdict := {"decision": "allow"}

pre_model_call_verdict := {
	"decision": "deny",
	"reason": "acs_image_probe_denied",
	"message": "The image probe requested a deny.",
} if {
	input.intervention_point == "pre_model_call"
	input.policy_target.value.block == true
}
