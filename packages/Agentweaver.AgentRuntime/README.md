# Runtime SDK source library

This library is source for the future AgentHost. It is not an AgentHost executable,
dispatch service, or deployment.

The Copilot session factory uses an explicit registered connection and a server-owned
mapping from accepted model references to SDK model IDs and provider configuration.
The accepted platform or project selection fixes `ModelSourceMode`.
There is no personal provider preference or fallback.
Missing or mismatched modes deny SDK startup.
Hosted mode reads the actual SDK catalog before it creates the session.
BYOK mode supplies an explicit SDK `Provider` and does not consult Copilot authentication or its catalog.
Per-request and per-session credentials stay
in memory and on the SDK protocol; the factory does not set credential environment
variables or use ambient login.
Builds disable the SDK's automatic native-runtime download. The approved host
must supply the registered runtime connection; this library does not install one.

The native usage callback pins the actual session, model, SDK/runtime versions, and
source hash. Hosted mode hashes the native catalog.
BYOK mode hashes the approved provider configuration, not a Copilot catalog.
The callback preserves missing measurements and hosted weighted `TotalNanoAiu`.
BYOK records token measurements under `byok.tokens` and rejects Copilot nano-AIU attribution.
Invalid attribution or a full pending channel faults the usage reader. It does not
substitute zero, guess a price, or acknowledge a durable accounting write.

Hosted selection fixes a stable Identity connection ID, not a secret version.
Identity verifies the current owner, scope, GitHub user, freshness, and connection revision.
The `model-session` receipt pins that revision, credential kind, and exact protected secret version.
Identity extracts only the current user access token and bounds its lifetime.
Refresh tokens and OAuth client secrets remain inside Identity.
The Host rejects refresh envelopes, installation tokens, raw BYOK keys, and expired access credentials.
The native SDK receives only the access token under the current typed receipt.

The factory is internal to the runtime library. The authenticated bootstrap receiver
accepts only an Identity-verified pending nonce for its exact registration.
The original Broker bearer and a separate observe credential authorize source writes.
The credential fixes the runtime, revision, configuration hash, audience, and expiry.

`AuthorizedRuntimeSession.CommitTurnContentAsync` stores actual bounded turn content
through the typed Events material interface.
The original bearer and current runtime registration authorize the write.
Events records bytes before the journal reference and rechecks authority after waits.
The material client checks the returned digest, length, execution identity, and SDK/model bindings.
Historical reads address a committed session/event/kind, never an arbitrary object key.

`RuntimeSessionBootstrap` consumes a delivered configure nonce before SDK binding.
It derives the model reference from the current registration, exchanges the consumed
nonce for an observe credential, and rechecks authority after native SDK awaits.
After SDK preparation, it compares the current registration before verifying the
observe grant. It checks credential lifetimes and cancellation immediately before
`session.create`, with no intervening asynchronous owner lookup.
Only a fully initialized immutable session is returned. Initialization failure revokes
or invalidates the source credential and disposes the SDK session.
`AuthorizedRuntimeSession.SendTurnAsync` records the user content before the native SDK turn.
The Orchestrator grants the exact action and records its Policy receipt before the protected effect.
The runtime rechecks that admission and current authority before the SDK call.
The native pre-tool hook and permission callback use the same owner gate.
Unknown tools, cross-session callbacks, sandbox bypass, and managed approval requests deny.
Without an action owner, the SDK receives no tools and rejects permission requests.

Native turns remain serialized until the SDK reports `session.idle`.
Cancellation requests an actual SDK abort and waits for that idle event before another turn.
A failed abort or missing idle event marks the session indeterminate.
The runtime then rejects further turns and cache capture.
Disposal cancels and drains an active turn before the SDK session closes.

The runtime records actual assistant content and opaque native cache bytes at the turn boundary.
The custom SDK filesystem captures native state without an alternative transcript format.
The native session identity remains stable across runtime replacements but differs across tenant, project, run, or session boundaries.
Compatible cache recovery uses `session.resume` with `ContinuePendingWork` disabled.
A missing, incompatible, or corrupt cache rebuilds context from the recorded journal without replaying a model turn.

`AuthorizedRuntimeSession.CommitUsageAsync` registers the actual SDK facts with
the Orchestrator, then commits native callbacks through the fixed owner HTTP client.
The owner checks current Core permission, accepted selection, session, turn,
Environment lease, registration, and observe grant after database waits.
It returns an immutable source receipt only after the PostgreSQL commit.
Identical SDK events return the original receipt. Changed event content conflicts.

Events accepts only that receipt ID through its separate usage route.
It fetches the source receipt from the fixed Orchestrator owner and commits
the price, immutable rate card, ledger entry, payload hash, and inbox together.
Database triggers reject updates, deletes, and statement-level truncation of source
records, accounting receipts, and run-scoped Cost bindings.
The source receipt and accounting acknowledgment are different contracts.
Without an admitted Cost provider, BYOK accounting remains explicitly `Unpriced`.
Unknown cost is not zero and cannot satisfy a hard cost bound.
A committed source receipt survives restart and supports explicit accounting retries.
The library does not run a relay or persist the in-memory callback queue.
Failed writes remain explicit errors, not successful acknowledgments.

The combined local test uses actual Broker, Core, Projects, Environment,
Orchestrator, SDK, and Events code with disposable PostgreSQL.
External GitHub and Key Vault HTTP, placement, SDK transport, catalog, and pricing inputs remain controlled.
Native lifecycle tests cover browser nonce and subject binding, single-use callbacks,
serialized rotation, refresh rejection, transient recovery, uncertain rotation, and current Core revocation.
The combined scenario rotates the linked credential after selection acceptance and checks the unchanged selection bytes and hash.
It also checks the exact current secret version, access-token-only response, and absence of refresh/client secrets on the SDK wire.
SDK configuration disables its session store and ambient configuration discovery.
This evidence does not prove GitHub entitlement, deployed hosts, live AKS placement, or paid model execution.
