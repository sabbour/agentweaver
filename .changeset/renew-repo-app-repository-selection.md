---
"agentweaver": patch
---

Keep GitHub repository selection working when a connected Repo App user's access token expires or is rejected. Renew it through the authorized refresh path, distinguish temporary provider failures from lost authorization, and preserve the connection during temporary refresh failures.
