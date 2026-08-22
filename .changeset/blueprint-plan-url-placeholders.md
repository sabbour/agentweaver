---
"agentweaver": patch
---

Replace hardcoded staging project/orchestration IDs in blueprint capture plan with env-var placeholders

Beats 2.3–2.8 had hardcoded project ID (71cdf9d6) and orchestration ID (38cdd5a3) in their startUrls.
These break after clean-staging removes the old project.

Fix: replace with {{AGENTWEAVER_DEMO_PROJECT_URL}}/board and {{AGENTWEAVER_DEMO_ORCHESTRATION_URL}}.
Add AGENTWEAVER_DEMO_ORCHESTRATION_URL prerequisite to all affected beats.
