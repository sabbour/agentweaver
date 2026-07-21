---
"agentweaver": minor
---

Made the `Changeset advisory` CI check a required, blocking status check instead
of an advisory-only warning: a PR touching release-relevant paths (`apps/`,
`packages/`, `scripts/azure/`, `k8s/`) with no changeset and no
`changeset:not-required` exemption now fails CI instead of only printing a
warning. Test-only diffs under those paths no longer trigger the requirement.
Also made every path-scoped CI job (`.NET tests`, `Node toolchain tests`, `Web
tests`, `Web lint`, `Docs build`) run only when its relevant paths actually
changed, skipping unrelated suites (e.g. docs-only PRs no longer run the full
.NET/web suites) while always running everything when the CI workflow itself
changes.
