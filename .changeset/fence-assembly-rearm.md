---
"agentweaver": patch
---

Prevent a second API replica from re-arming and corrupting an active collective assembly by fencing each attempt with the coordinator run lease.
