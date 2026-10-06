# Contracts, endpoints, and configuration

## Provider contract

`ProviderDescriptor` contains a `ProviderSeam`, provider ID, adapter version, options-schema version, hosting pattern, and advertised capabilities.

`ProviderRegistration` adds the enabled state, options revision, and selected schema version. A `PinnedProviderBinding` records the run, resource generation, and negotiated capabilities.

These records contain no credentials or option values. See [providers and models](../architecture/providers-models) for resolver limits.

## Events & Sessions journal

The `Agentweaver.EventsAndSessions` service is an unpublished .NET 10 host candidate.
Its protected routes require an OpenIddict-validated bearer token with a GUID `sub`
and exactly one `project_id` and `run_id` pair issued for an active core grant. They
are service-internal contracts, not public product API routes; tenant and platform
role claims are not required.

| Method and path | Contract |
| --- | --- |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL and current owned-schema readiness; returns `503` when the schema is absent or outdated. |
| `POST /internal/sessions/{sessionId}` | Resolve and pin the run's native Sessions provider, then create the session. A run reuses its immutable provider binding; a different binding returns `409`. |
| `POST /internal/sessions/{sessionId}/events` | Append a versioned typed event to the project/run journal. Returns `201` for a new event, `200` for an identical run-scoped event-ID retry, and `409` if the ID is reused with different event content in that run. |
| `GET /internal/sessions/{sessionId}/events?cursor={cursor}&limit={limit}` | Read an ordered page for one session after an optional opaque cursor. Positions are run-wide and may have gaps in a session-only page. |
| `GET /internal/sessions/{sessionId}/events/live?cursor={cursor}&maximumEvents={count}&maximumDurationSeconds={seconds}` | Poll durable journal state and stream NDJSON `SessionEventDelivery` records, each containing the event and a reconnectable `nextCursor`. |
| `GET /internal/projects/{projectId}/runs/{runId}/events?cursor={cursor}&limit={limit}` | Read a bounded, run-ordered page across all sessions in the authorized project/run. |
| `GET /internal/projects/{projectId}/runs/{runId}/events/live?cursor={cursor}&maximumEvents={count}&maximumDurationSeconds={seconds}` | Poll and stream run-ordered NDJSON deliveries across sessions; each delivery includes a reconnectable `nextCursor`. |

The version-1 abstraction contract provides `SessionIdentity`, `AppendSessionEvent`,
and a discriminated `SessionEventPayload` for turns, tool calls, accepted decisions and
effects, artifact references, and cache references. Payload content is represented by
opaque `ObjectKey` references. The journal does not accept credentials or store large
payload bytes.

The run-level provider pin records provider ID, adapter version, options schema version
and revision, negotiated resource ID and generation, and capabilities. It is inserted
with the first session for a project/run and cannot be replaced by another create
request. Append, replay, and subscription verify the stored pin against the exact
registered provider and resource before proceeding; there is no provider fallback.
Run cursors are opaque and bound to the project/run; session cursors are additionally
bound to the session. Callers should return either token unchanged on replay or
reconnect.

Configuration:

| Key | Requirement |
| --- | --- |
| `ConnectionStrings:EventsAndSessions` | PostgreSQL connection for the service-owned schema and transactional outbox/inbox. |
| `Identity:Issuer` | Absolute HTTPS OpenIddict issuer used to validate caller tokens. |
| `Identity:Audience` | Required token audience for the service. |
| `EventsAndSessions:Provider:ResourceId` | Stable opaque resource identity used in the Sessions provider pin. |
| `EventsAndSessions:Provider:DatabaseName` | Expected live PostgreSQL database name; negotiation compares it with `current_database()`. |
| `EventsAndSessions:Provider:ResourceGeneration` | Positive generation recorded in the immutable provider pin. |
| `EventsAndSessions:Provider:Schema` | Optional owned schema name; defaults to `events_sessions`. |
| `EventsAndSessions:Provider:OptionsRevision` | Optional provider options revision; defaults to `native-postgres-v1`. |
| `EventsAndSessions:Provider:OptionsSchemaVersion` | Optional options schema version; defaults to `1`. |
| `EventsAndSessions:Provider:PollIntervalMilliseconds` | Optional live-poll interval from 50 to 30,000 ms. |
| `EventsAndSessions:Provider:ReferenceRetentionDays` | Optional object-reference retention from 1 to 3,650 days. |
| `EventsAndSessions:ProjectOverrides:{projectId}` | Optional project-level provider IDs permitted by the host catalog. |

Run the executable with only `--migrate` to apply the embedded migration explicitly.
Ordinary startup verifies the service and outbox schema, fails if a migration is
pending, and does not create or alter database objects. The service is source-only:
the repository does not include its deployment, a Gateway route, AgentHost integration,
message delivery, usage ledger, or consistency-manifest workflow. See the
[Events & Sessions journal reference](../architecture/events-sessions).

## Identity broker endpoints

These routes belong to the unpublished Identity broker candidate. They are service contracts in source, not deployed product endpoints.

| Method and path | Contract |
| --- | --- |
| `GET /connect/authorize` | OpenIddict authorization request and external sign-in or consent prompt. |
| `GET /connect/authorize/resume` | Authenticated, single-use external sign-in continuation. |
| `POST /connect/consent` | Consent handle, approval, optional scope subset, local cookie, and `X-CSRF-TOKEN`. |
| `POST /connect/token` | Form-encoded authorization-code or refresh exchange. |
| `GET /diagnostics/whoami` | Bearer validation diagnostic. It is not an audience acceptance test. |
| `POST /secrets/redeem` | Validated bearer and exact secret ID, version, purpose, and run ID. |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL connectivity. |

The service has no grant-administration HTTP endpoint. The browser consent UI is not implemented.

## Identity host configuration

| Key | Requirement |
| --- | --- |
| `ConnectionStrings:IdentityBroker` | Identity-owned PostgreSQL database for runtime access. Use the separately bootstrapped Entra runtime role and omit a password; ordinary startup verifies but does not migrate. |
| `IdentityBroker:Issuer` | Absolute HTTPS issuer. |
| `IdentityBroker:Signing:PfxPath` | Mounted signing, encryption, and data-protection certificate. |
| `IdentityBroker:Signing:PfxPassword` | Deployment secret. Do not store it in source control. |
| `IdentityBroker:DataProtectionKeyPath` | Durable writable key-ring path shared by broker replicas. |
| `IdentityBroker:ExternalProvider:Authority` | Absolute HTTPS OIDC authority. |
| `IdentityBroker:ExternalProvider:ClientId` and `ClientSecret` | Registered upstream confidential client. |
| `IdentityBroker:Clients` | Registered client IDs, types, redirect URIs, scopes, resources, and secrets. |
| `IdentityBroker:SecretRedemption:Audience` | Required HTTPS audience registered as a client resource. |
| `IdentityBroker:SecretRedemption:VaultUri` | Azure Key Vault root URI. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId` | Explicit Entra tenant ID. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityClientId` | Explicit Entra client ID. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTokenFilePath` | Absolute projected token-file path. |

The host does not use ambient credentials or a development-certificate fallback.

## Identity PostgreSQL access

Runtime and schema migration use separate connection strings, projected workload identities, and Entra PostgreSQL roles. Both connections use passwordless async Npgsql token acquisition, `VerifyFull`, and the scope `https://ossrdbms-aad.database.windows.net/.default`.

| Operation | Configuration | PostgreSQL role and boundary |
| --- | --- | --- |
| Ordinary runtime | `ConnectionStrings:IdentityBroker`; `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId`, `WorkloadIdentityClientId`, and `WorkloadIdentityTokenFilePath`. | The operator-selected runtime Entra role has `CONNECT`, schema `USAGE`, DML on current Identity/OpenIddict tables, and `SELECT` on the migration history. It does not own the schema or apply migrations. |
| Explicit migration | Run the executable with only `--migrate`; provide `ConnectionStrings:IdentityBrokerMigration` and `IdentityBroker:Migration:WorkloadIdentityTenantId`, `WorkloadIdentityClientId`, and `WorkloadIdentityTokenFilePath`. | A separate migration Entra role owns `identity_broker` and applies migrations. Its workload identity receives no Azure resource role. |

The database bootstrap and reviewed grant SQL are operator-run steps. The ordinary host checks that the schema exists and no migrations are pending; it fails rather than creating roles or changing the schema.

## AKS Application Routing preview

`appRoutingDnsZoneResourceIds` is an optional deployment input. Empty input requests AKS's managed default domain. One to five unique existing public or private DNS zone IDs request custom domains and disable that default. IDs must belong to the exact authorized subscription, cannot name PrivateLink zones, and can use at most one resource group per zone kind.

The `appRoutingDomain` output has `managedDefaultRequested` and `domainName` fields. For an empty zone list, the template returns the exact `defaultDomain.domainName` from the AKS response. For custom zones, it returns `domainName: null`. The source does not construct hostnames or configure HTTP routes.

The `appRoutingIdentity` output is separate from `foundationProbeIdentity`. It contains the AKS-generated `resourceId`, `clientId`, and `objectId`.

| Role | Scope |
| --- | --- |
| Key Vault Certificate User (`db79e9a7-68ee-4b58-9aeb-b90e7c24fcba`) | The existing Key Vault resource. |
| DNS Zone Contributor (`befefa01-2a29-4197-83a8-272ff33ce314`) | Each exact configured public DNS zone. |
| Private DNS Zone Contributor (`b12aa53e-6015-4669-85d0-8515ebb3ae7f`) | Each exact configured private DNS zone. |

The AKS source uses the Preview `2026-07-02-preview` API, enables the Key Vault CSI provider, and sets secret rotation to `'true'` with a `'2m'` poll interval. These are source definitions. No live role assignment, addon activation, domain, certificate, or route was verified.

## Azure operator command

The supported tool reads the exact subscription and tenant supplied by the operator. Deployment requires separate approval for its target and cost.

Run a non-mutating summary with `node scripts/azure/deploy.mjs` and reviewed target arguments. Add `--execute` only after approval. The checked-in parameter examples contain placeholders.

## External acceptance evidence

The Foundation Probe Job reports its resource-operation results, but it does not attest to its own pod identity, pulled image, or exit status. The separate read-only consumer observes the completed Job and pod, checks their ownership and workload-identity projection, and verifies that both the Job image and observed pulled image match the expected registry manifest.

The consumer validates the native probe receipt against the admitted source SHA, Git tree, deployment, identity, and target. It then queries Azure Monitor only after Job completion and requires fresh `AppDependencies` or `AppRequests` evidence correlated by source SHA, Git tree, nonce, trace, and span. Configuration checks do not substitute for this runtime proof; incomplete or mismatched evidence remains blocked. Local fixtures validate the consumer contract but do not prove a live deployment.
