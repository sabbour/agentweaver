---
"Agentweaver.Identity.Broker": patch
---

Redeem runtime model credentials through their verified grant ID and revision so
distinct current runtimes can share an authorized connection without introducing
ambiguous generic secret redemption. Preserve fresh receipt, connection,
revocation and expiry checks before and after secret acquisition.
