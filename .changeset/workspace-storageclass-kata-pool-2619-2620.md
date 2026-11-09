---
---

Ship the product workspace StorageClass `agentweaver-workspace-azurefile` with the required Azure Files mount options, and point Environment at it. Both infrastructure templates now create or update the `kata` node pool (Azure Linux, Kata VM isolation, SSH disabled, autoscale 1-2). `npm run deploy:kustomize` creates a missing product StorageClass, stops before writes when no Ready Kata node exists, and runs an install smoke check in an AgentHost-labelled sandbox pod.
