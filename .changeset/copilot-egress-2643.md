---
---

The Kustomize deploy helper now adds the exact GitHub Copilot hosts to the agenthost-model-egress Cilium policy on TCP 443
when the rendered AgentHost map has a HostedCopilot model binding. The hosts are api.github.com, api.githubcopilot.com,
api.individual.githubcopilot.com, api.business.githubcopilot.com, and api.enterprise.githubcopilot.com.
No product component version changed.
