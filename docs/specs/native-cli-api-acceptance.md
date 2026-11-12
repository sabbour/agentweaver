# Specification: Native CLI API acceptance

**Status: Superseded.** The former native acceptance runner and its `test` command were removed. This specification no longer describes the supported acceptance interface.

Use [Run a read-only CLI API case](../guide/api-acceptance-migration.md) for the current API acceptance procedure. The checked-in case invokes `auth status`, `api operations`, `api describe`, and `rest` through the native CLI apphost. It is read-only and does not prove a full API journey or P1 acceptance.
