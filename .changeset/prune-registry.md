---
"agentweaver": patch
---

Add `npm run azure:prune-registry`, a cross-platform registry cleanup command.

Deployments accumulate release images, per-commit images, provenance copies, and
preflight staging tags that nothing references. The new `prune-registry`
subcommand removes them, replacing an ad-hoc PowerShell script with a Node
implementation that runs the same way on every platform as the rest of the
deployment toolchain.

It is a dry run by default and prints its full plan before `--execute` will
touch anything. Retention is decided by digest rather than tag name, so the
per-architecture children of a retained multi-arch index are protected even
though they carry no tags of their own — the naive "delete everything untagged"
approach silently destroys those. Images running in the cluster are protected
whether they are referenced by tag or by pinned digest, and the command refuses
to delete anything at all if the running set cannot be read, since that set is
what makes digest protection possible.
