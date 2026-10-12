---
"Agentweaver.Orchestrator": minor
"Agentweaver.Environment": minor
"Agentweaver.Abstractions": minor
"Agentweaver.Identity": minor
"Agentweaver.Identity.Broker": minor
---

Add separate, default-off TLS profiles for Environment's current-call verifier and Identity owner clients.
Keep the existing Events listener and peer pins separate, and reject incorrect service roles, shared listener ports, and ordinary HTTPS before reading a proof.
These source profiles do not enable remote MCP dispatch, credential delivery, or applied network enforcement.
Add exact current-call and gate contracts, an authenticated owner client, and a non-wire native call context.
An in-process gate receipt is not proof of forced network enforcement.
Read full current gateway bindings from active-handler state and exact accepted run options, with repeat checks after waits.
Keep the Cilium-only L7 rejection intact and refuse dispatch without a real forced-network proof producer.
Carry the original actor context and full owner-built proof through the reviewed-call transport handoff.
Keep native transport registration and the SDK remote-tool hook unavailable.
Select the current Identity access-token reference for the fixed reviewed tools-call purpose.
Recheck OAuth row bindings after waits without reading or delivering a credential.
Add a separate, default-off Environment listener for its existing Orchestrator peer.
Reject wrong-service, shared-port, and non-native-TLS requests before proof reads.
Keep the recipient source separate from route, actor, and network authorization.
