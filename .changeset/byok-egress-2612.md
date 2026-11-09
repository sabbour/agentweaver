---
---

The Kustomize deploy helper now adds the HTTPS host of each BYOK model binding to the agenthost-model-egress Cilium policy,
so that AgentHost can reach the BYOK Azure OpenAI endpoint on TCP 443. The models byok plan command rejects endpoints on other ports.
No product component version changed.
