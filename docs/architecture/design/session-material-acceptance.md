# Specification: Integrated session-material acceptance

**Status: Superseded.** The former native-material acceptance journey was removed with the native acceptance runner. This document does not describe a supported live acceptance path or claim deployed session-material evidence.

The current read-only API case is documented in [Run a read-only CLI API case](../../guide/api-acceptance-migration.md). It checks Gateway discovery and the signed-in actor’s authorization context. It does not create sessions, read session material, replace a Host, run a model, or prove session-material recovery.

The Workspace repository-read case uses the `agentweaver workspace refs`, `tree`, and `file` commands. See [Run the Workspace repository-read case](../../guide/api-acceptance-migration.md#run-the-workspace-repository-read-case). Do not use internal CLI types or bypass sensitive-header guards.
