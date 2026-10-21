---
"Agentweaver.Abstractions": minor
"Agentweaver.SourceControl": minor
"Agentweaver.Orchestrator.Core": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.Gateway": minor
"Agentweaver.Web": minor
---

List a project's synced repository issues and the pull requests its runs created
or reused, from stored data and without a provider call. Pull request records
start when the code-only Orchestrator migration 032 is applied; until then, the
pull request list reports that it isn't available. Migration 032 and its grants
remain unapplied.
Merge intent requests clear the optional pull request title so stored intents stay stable.
