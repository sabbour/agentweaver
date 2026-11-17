# Specification: Native CLI API acceptance

**Status: Superseded.** The former native acceptance runner and its `test` command were removed. This specification no longer describes the supported acceptance interface.

Use [Run native CLI API cases](../guide/api-acceptance-migration.md) for the
current targeted procedures. The read-only Gateway case invokes `auth status`,
`api operations`, `api describe`, and `rest` through the native apphost. The
workflow-generation case uses `api invoke` for one model-backed Gateway flow
and disposable-run cleanup. The task-model-completion case uses `api invoke`
to dispatch a claimed task, verify completion and priced BYOK usage, and
cancel a disposable run. These targeted cases do not replace full P1 API
acceptance.
