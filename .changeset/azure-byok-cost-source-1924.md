---
"Agentweaver.EventsAndSessions": minor
---

Add a meter-keyed Azure BYOK Cost provider for explicitly configured, versioned
standard-token rate cards. Missing or unsupported usage remains unpriced; the
provider does not fetch live rates or quote BYOK work, and provisioned-throughput
usage remains unpriced without trusted usage-share evidence. This does not admit
the native BYOK producer receipt path or complete issue #1924.
