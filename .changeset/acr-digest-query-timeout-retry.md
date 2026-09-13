---
"agentweaver": patch
---

Stop a single hung `az acr repository show` call from aborting a release deployment. The ACR digest lookup passed `allowFailure`, which only covers a non-zero exit code, so a query timeout rejected instead and escaped the surrounding retry/backoff loop. The lookup now treats a timeout as "not visible yet" and stays retryable, matching the sibling tag-digest lookup.
