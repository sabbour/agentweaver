---
'agentweaver': patch
---

Fix AgentHost client mTLS: `agentweaver-api` and `agentweaver-worker` now present a
client certificate and validate AgentHost's server certificate against the pinned CA
when calling the AgentHost A2A endpoint over HTTPS, and their
`Sandbox__AgentHost__RequireMtls` setting is kept in sync with AgentHost's own Kestrel
mTLS listener via a dedicated overlay patch, so a redeploy can no longer silently
revert the client side to plain HTTP while the server side still requires mTLS.
