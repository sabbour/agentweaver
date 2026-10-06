# Identity and secrets

`Agentweaver.Identity.Broker` is a .NET 10 service candidate. It uses OpenIddict and owns the `identity_broker` PostgreSQL schema. The service has no published image or deployment.

The broker accepts an external OIDC provider. It validates the upstream issuer, signature, audience, expiry, and nonce before it creates a local user.

OAuth authorization codes require registered clients, exact redirect URIs, registered scopes, and S256 PKCE. Consent requires the local cookie and antiforgery token. Refresh-token replay revokes the authorization and token family.

After upstream OIDC validation, the broker creates a local subject and issues access tokens with registered OAuth scopes and resources. It does not forward upstream tenant or role claims and does not assign application roles. Projects & Config is the sole live owner of issuer-and-subject project memberships and resource-role assignments. Other resource services obtain current authorization context through its versioned owner contract instead of keeping duplicate membership or role records; token claims and tenant selectors cannot create authority. Grant-validated `project_id` and `run_id` are included only for tokens bound to that exact project and run.

```mermaid
sequenceDiagram
    actor User
    participant IdP as Configured OIDC provider
    participant Broker as Identity Broker
    participant API as Resource API
    participant Projects as Projects & Config authorization owner
    User->>IdP: Authenticate
    IdP-->>Broker: Validated upstream identity
    Broker->>Broker: Replace upstream subject with local broker subject
    Broker->>Broker: Apply registered scopes and resource audience
    Broker-->>API: Signed access token (sub, scope, audience, optional project/run binding)
    API->>API: Validate issuer, signature, lifetime, and audience
    API->>Projects: Read current authorization context through versioned owner contract
    Projects-->>API: Current membership and role context
    API->>API: Authorize without duplicate membership or role records
    Note over API,Projects: Logical contract boundary; no transport or endpoint is specified here.
```

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'" alt="After bearer validation, Identity.Broker passes the authenticated actor to the authorization wrapper. The wrapper checks the exact grant before and after exact-version Key Vault access, invalidates a credential on a failed postcheck, and the host invalidates it after responding." />
  </a>
  <figcaption>This sequence starts after native bearer validation. It shows the redemption boundary, not upstream sign-in, OAuth consent, or grant issuance.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.drawio'">Open editable draw.io source</a></p>

The broker issues `project_id` and `run_id` claims only after it finds an active grant for the authenticated local user. Token exchange and refresh repeat that binding check. These claims constrain a request to the signed project/run; they do not grant a project role. Projects & Config owns its issuer-and-subject memberships, resource-role assignments, and immutable authorization audit in `projects_config`. Its runtime database principal can read but cannot insert, update, or delete those records; provisioning and revocation use a separate privileged source path.

`POST /secrets/redeem` accepts a `SecretRef` identifier and exact version, purpose, and run ID. The bearer token supplies actor, project, and run claims. The broker does not accept caller-supplied identity claims.

`AuthorizedSecretRedemption` checks the exact grant before and after backend acquisition. A changed, expired, revoked, missing, or ambiguous grant denies redemption. The broker invalidates an acquired credential after the response.

The Key Vault adapter uses an injected credential or explicit `WorkloadIdentityCredentialOptions`. The AKS composition requires tenant ID, client ID, and an absolute projected token-file path. It has no `DefaultAzureCredential` fallback.

The adapter requests the exact Key Vault version. A returned credential expires within five minutes or at the earlier vault expiry. The grant store keeps references and binding snapshots, not secret values.

No grant-management HTTP endpoint exists. Read [contracts and endpoints](../reference/contracts) for the implemented route list and [configuration](../reference/contracts#identity-host-configuration) for host keys.

## PostgreSQL authentication and workload boundary

Runtime PostgreSQL connections use the explicit `WorkloadIdentityCredential`, no password, and `SslMode.VerifyFull`. Npgsql acquires an Entra token asynchronously through `UsePasswordProvider` for each new physical connection, using the fixed scope `https://ossrdbms-aad.database.windows.net/.default`. The runtime username is the separately bootstrapped Entra runtime role.

Ordinary host startup verifies that the `identity_broker` schema exists and that all migrations are applied; it never applies migrations or creates database roles. Only the explicit `--migrate` command runs migrations, in a separate one-shot Job with its own ServiceAccount, workload identity, connection-string key, token settings, and schema-owner role. See [PostgreSQL configuration and roles](../reference/contracts#identity-postgresql-access).

The Kubernetes source defines an HTTPS Deployment behind an HTTPS `ClusterIP` Service. It defines no Ingress, Gateway, or ingress controller. The source has not been deployed. Operators must supply the real issuer and external OIDC authority/domain, client registrations and credentials, approved immutable image, TLS certificate/key, signing PFX/password, durable key-ring storage, approved PostgreSQL administrator and roles, and the exact read-only Key Vault fixture. These inputs are placeholders or external responsibilities; this page makes no deployed-readiness claim.
