# Runtime SDK source library

This library is source for the future AgentHost. It is not an AgentHost executable,
dispatch service, or deployment.

The Copilot session factory uses an explicit registered connection and a server-owned
mapping from accepted model references to SDK model IDs. It reads the actual SDK
catalog before creating the session. Per-request and per-session credentials stay
in memory and on the SDK protocol; the factory does not set credential environment
variables or use ambient login.
Builds disable the SDK's automatic native-runtime download. The approved host
must supply the registered runtime connection; this library does not install one.

The native usage callback pins the actual session, model, SDK/runtime versions, and
catalog hash. It preserves missing measurements and weighted `TotalNanoAiu`.
Invalid attribution or a full pending channel faults the usage reader. It does not
substitute zero, guess a price, or acknowledge a durable accounting write.

The factory is internal to the runtime library. Authenticated configure, source
enrollment, owner receipts, and Events ingestion remain in progress. These factory
and callback primitives alone are not producer-authentication proof.
