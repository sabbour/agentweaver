# Native runtime session recovery

The runtime recovers a session from material recorded by the Events owner. The
owner controls event order, material references, and current authorization.
Object storage holds opaque bytes; the runtime never chooses an object key or
reads a shared workspace to find prior state.

## Reusing a native SDK cache

The runtime uses the pinned GitHub Copilot SDK package version 1.0.18. Native
session state is captured and restored through the SDK filesystem callbacks,
then passed back to the SDK's native resume operation. The runtime does not
read private SDK files or define a second cache format.

Before reuse, the runtime checks that the owner-recorded cache belongs to the
current session and tenant and matches the accepted selection, SDK and runtime
versions, model, reasoning effort, prompt capacity, and resolved model binding.
The current runtime registration and execution fence are also required. An
incompatible optional cache can fall back to authorized journal history. A
cache that claims compatibility but is corrupt or unsafe fails explicitly; it
is not treated as missing.

The cache remains opaque apart from bounded archive and path validation and
checks for credential values known to the runtime. Those checks are not a
general-purpose secret scanner. Unsafe material is rejected without editing,
redacting, or rewriting its bytes.

## Rebuilding from the journal

When the optional cache is absent, the runtime reads the owner's ordered event
pages and verifies each required turn reference, byte length, digest, session
scope, and authorization. It follows the existing size bound and rejects
missing or damaged turn content rather than inventing history.

The replacement SDK session receives the verified turns as bounded context
marked as prior conversation history. Recovery creates or resumes a native
session without sending a model turn, dispatching a tool, or replaying an
external effect. The runtime records the recovery mode, reason, journal
position, and journal digest through its existing recovery evidence.

For manifest-based recovery, the runtime hashes only events through the
recorded checkpoint boundary. A missing boundary fails explicitly instead of
producing evidence for a partial journal. Current owner authority is checked
again for reads and writes, so an authorization change during storage or HTTP
work cannot become a successful acknowledgment.

## Retry and verification limits

If a material write commits but its HTTP acknowledgment is lost, the runtime
retries with the same event identity and bytes after refreshing its source
authority. The owner returns the original acknowledgment instead of storing a
second object. A retry with changed content is not equivalent.

The tests use a controlled SDK transport and object store. The owner integration
cases use disposable PostgreSQL to verify replacement-owner reads and lost
acknowledgments. Those cases require Docker and are left to CI when no
Docker-compatible engine is available locally. These tests do not prove live
Blob access, deployed runtime behavior, or paid model execution.
