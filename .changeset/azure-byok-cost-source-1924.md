---
"Agentweaver.EventsAndSessions": minor
---

Add a versioned Azure BYOK Cost provider for explicitly configured standard-token
rate cards. It uses exact decimal arithmetic, does not fetch live rates or quote
BYOK work, and leaves missing or unsupported usage unpriced. Provisioned-throughput
usage remains unpriced without trusted usage-share evidence. The provider is not
yet wired into native receipt pricing; BYOK receipts remain unpriced until the
trusted Azure provider and deployment facts are admitted.
