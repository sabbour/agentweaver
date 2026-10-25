# Session Material Owner Retention

The Events owner controls both the journal references and the retention deadline for session material. Blob storage holds bytes, but having a key or finding bytes does not grant permission to read them.

See the [session material composition](../session-material-composition.md) for the system boundaries and the other implementation slices.

## Reading material

The owner resolves a material key from the exact event recorded for the requested session. It checks the event's persisted reference deadline with PostgreSQL time before returning the event to the storage reader. The deadline is expired when it is equal to or earlier than the database clock.

The owner checks the deadline again after the object read and integrity verification. It also rechecks current project, session, provider, and runtime authority after the asynchronous work. If the deadline expires while storage is returning bytes, the owner denies the read rather than disclosing those bytes.

Retention belongs to each event reference, not just to the object key. A different live reference may keep the shared bytes from cleanup, but it does not extend an expired event's read permission. Missing or malformed reference records fail closed.

Turn content is required history, so an expired turn returns an explicit owner failure. SDK cache is optional recovery input, so an expired cache uses the existing not-found recovery response with a retention-specific error. Both responses are marked `no-store`. Authorization failures and damaged or mismatched bytes remain failures; they are not translated into optional-cache absence.

## Writing and retrying

The owner records an inventory candidate before it writes bytes. It uses a stable key derived from the owner scope, event identity, and content digest, and it never replaces existing bytes. The journal reference and its deadline commit only after the object exists and has passed verification.

An acknowledgment is issued only after the journal commit and the final current-authority check. If the caller loses the response, a new owner instance can retry the same event identity against the same database. The retry reads and verifies the existing bytes, then returns the original event position without another object write. Changed content or a changed owner binding conflicts or is denied.

Authority remains current across waits and restarts. Revocation after the object write prevents the journal commit, and a retry is denied while that authority remains revoked. A changed execution fence also makes a request carrying the old fence invalid.

## Cleanup and interruption

Cleanup selects expired objects and stale unreferenced uploads in bounded batches. It locks each inventory row and checks current reference deadlines before deleting bytes. A live reference preserves the bytes. The owner records the deletion tombstone only after the object-store delete returns, and it preserves the journal event and reference history.

If storage deletes an object but the database commit is interrupted, the next cleanup pass retries the same known key. An already-absent object is safe to tombstone. The lock order prevents a writer from committing a new reference after cleanup has completed its deletion; when the writer locks first, cleanup skips that row and later observes the new live reference.

Historical objects without a durable inventory record remain outside automatic cleanup. The object-store interface has no listing operation, so this owner does not scan the container or guess keys.

## Validation

The Events PostgreSQL tests use a disposable database and a controlled object store. They cover deadlines before, at, and after expiry; expiry during a delayed object read; an expired reference alongside a separate live reference; required-turn and optional-cache responses; object-write response loss; definite journal rollback; cancellation after commit; authority and fence changes; interrupted cleanup; and both cleanup/writer lock orders.
