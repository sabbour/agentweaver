---
"agentweaver": patch
---

Wire Auth__CopilotApp__FrontendUrl and Auth__RepoApp__FrontendUrl into the production Kubernetes deployment. Without these, the post-authorization browser redirect for both GitHub Apps fell back to the http://localhost:5173 development default, sending production users to their own machine instead of back to the deployed frontend after connecting.
