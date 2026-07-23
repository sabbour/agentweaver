---
"agentweaver": patch
---

`azure:provision-infra` interactive installer now supports arrow-key selection (with the numbered prompt as a fallback when raw-mode TTY is unavailable), walks you through creating a GitHub OAuth App (with link and callback-URL guidance) before asking for the client ID/secret, and prompts for the GitHub org(s) allowed to sign in (`GITHUB_ALLOWED_ORG`, also available as `--github-allowed-org`). Prompts now validate and reprompt on invalid input, and az-backed discovery (subscription/resource group/location) degrades to a manual prompt instead of crashing on transient failures.
