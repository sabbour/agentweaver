---
"agentweaver": patch
---

Fix preview runner pod OOM kills by setting explicit resource requests/limits and Node.js heap cap

Sets 1Gi memory request and 2Gi memory limit on AgentHost and agentweaver-exec containers in the
SandboxTemplate. Adds NODE_OPTIONS=--max-old-space-size=1024 to prevent V8 heap growth from
triggering cgroup OOM kills during Next.js/Vite preview server startup. Adds deploy-render test
assertions to lock these values into the rendered deployment contract.

Fixes #845.
