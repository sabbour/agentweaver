---
"Agentweaver.Environment": patch
---

Require full-population source selectors for default-deny egress, enforce
source-aware Cilium deny subtraction and namespace checks, fail closed on
malformed `enableDefaultDeny` and extra destination labels, and allow the shared
`identity-broker` Gateway on TCP 443 in the System-only policy-set evaluator.
