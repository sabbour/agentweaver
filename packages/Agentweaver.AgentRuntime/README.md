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

The factory is internal to the runtime library. The authenticated bootstrap receiver
accepts only an Identity-verified pending nonce for its exact registration.
The original Broker bearer and a separate observe credential authorize source writes.
The credential fixes the runtime, revision, configuration hash, audience, and expiry.

`RuntimeSessionBootstrap` consumes a delivered configure nonce before SDK binding.
It derives the model reference from the current registration, exchanges the consumed
nonce for an observe credential, and rechecks authority after native SDK awaits.
Only a fully initialized immutable session is returned. Initialization failure revokes
or invalidates the source credential and disposes the SDK session.

`AuthorizedRuntimeSession.CommitUsageAsync` registers the actual SDK facts with
the Orchestrator, then commits native callbacks through the fixed owner HTTP client.
The owner checks current Core permission, accepted selection, session, turn,
Environment lease, registration, and observe grant after database waits.
It returns an immutable source receipt only after the PostgreSQL commit.
Identical SDK events return the original receipt. Changed event content conflicts.

Events accepts only that receipt ID through its separate usage route.
It fetches the source receipt from the fixed Orchestrator owner and commits
the price, immutable rate card, ledger entry, payload hash, and inbox together.
The source receipt and accounting acknowledgment are different contracts.
A committed source receipt survives restart and supports explicit accounting retries.
The library does not run a relay or persist the in-memory callback queue.
Failed writes remain explicit errors, not successful acknowledgments.

The combined local test uses actual Broker, Core, Projects, Environment,
Orchestrator, SDK, and Events code with disposable PostgreSQL.
Only external placement and SDK transport, catalog, and pricing inputs are controlled.
This evidence does not prove deployed hosts, live AKS placement, or paid model execution.
