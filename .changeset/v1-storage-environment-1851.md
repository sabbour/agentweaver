---
"Agentweaver.Environment": minor
---

Add Environment-owned workspace-volume lifecycle, authorization, and durable replacement cleanup. Reject release while a volume remains bound or attached, record known no-effect provider rejections as retryable failures, and keep failed transition replays from returning success.
