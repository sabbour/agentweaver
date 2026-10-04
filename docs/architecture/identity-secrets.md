# Identity and secrets

`Agentweaver.Identity.Broker` is a .NET 10 service candidate. It uses OpenIddict and owns the `identity_broker` PostgreSQL schema. The service has no published image or deployment.

The broker accepts an external OIDC provider. It validates the upstream issuer, signature, audience, expiry, and nonce before it creates a local user.

OAuth authorization codes require registered clients, exact redirect URIs, registered scopes, and S256 PKCE. Consent requires the local cookie and antiforgery token. Refresh-token replay revokes the authorization and token family.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'" alt="After bearer validation, Identity.Broker passes the authenticated actor to the authorization wrapper. The wrapper checks the exact grant before and after exact-version Key Vault access, invalidates a credential on a failed postcheck, and the host invalidates it after responding." />
  </a>
  <figcaption>This sequence starts after native bearer validation. It shows the redemption boundary, not upstream sign-in, OAuth consent, or grant issuance.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.drawio'">Open editable draw.io source</a></p>

The broker issues `project_id` and `run_id` claims only after it finds an active grant for the authenticated local user. Token exchange and refresh repeat that binding check.

`POST /secrets/redeem` accepts a `SecretRef` identifier and exact version, purpose, and run ID. The bearer token supplies actor, project, and run claims. The broker does not accept caller-supplied identity claims.

`AuthorizedSecretRedemption` checks the exact grant before and after backend acquisition. A changed, expired, revoked, missing, or ambiguous grant denies redemption. The broker invalidates an acquired credential after the response.

The Key Vault adapter uses an injected credential or explicit `WorkloadIdentityCredentialOptions`. The AKS composition requires tenant ID, client ID, and an absolute projected token-file path. It has no `DefaultAzureCredential` fallback.

The adapter requests the exact Key Vault version. A returned credential expires within five minutes or at the earlier vault expiry. The grant store keeps references and binding snapshots, not secret values.

No grant-management HTTP endpoint exists. Read [contracts and endpoints](../reference/contracts) for the implemented route list and [configuration](../reference/contracts#identity-host-configuration) for host keys.

## PostgreSQL authentication and workload boundary

Runtime PostgreSQL connections use the explicit `WorkloadIdentityCredential`, no password, and `SslMode.VerifyFull`. Npgsql acquires an Entra token asynchronously through `UsePasswordProvider` for each new physical connection, using the fixed scope `https://ossrdbms-aad.database.windows.net/.default`. The runtime username is the separately bootstrapped Entra runtime role.

Ordinary host startup verifies that the `identity_broker` schema exists and that all migrations are applied; it never applies migrations or creates database roles. Only the explicit `--migrate` command runs migrations, in a separate one-shot Job with its own ServiceAccount, workload identity, connection-string key, token settings, and schema-owner role. See [PostgreSQL configuration and roles](../reference/contracts#identity-postgresql-access).

The Kubernetes source defines an HTTPS Deployment behind an HTTPS `ClusterIP` Service. It defines no Ingress, Gateway, or ingress controller. The source has not been deployed. Operators must supply the real issuer and external OIDC authority/domain, client registrations and credentials, approved immutable image, TLS certificate/key, signing PFX/password, durable key-ring storage, approved PostgreSQL administrator and roles, and the exact read-only Key Vault fixture. These inputs are placeholders or external responsibilities; this page makes no deployed-readiness claim.
