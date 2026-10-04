# Identity and secrets

`Agentweaver.Identity.Broker` is a .NET 10 service candidate. It uses OpenIddict and owns the `identity_broker` PostgreSQL schema. The service has no published image or deployment.

The broker accepts an external OIDC provider. It validates the upstream issuer, signature, audience, expiry, and nonce before it creates a local user.

OAuth authorization codes require registered clients, exact redirect URIs, registered scopes, and S256 PKCE. Consent requires the local cookie and antiforgery token. Refresh-token replay revokes the authorization and token family.

<figure class="aw-diagram">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'" alt="Identity flow showing external OIDC sign-in, OAuth consent and authorization, project-run grant checks, exact-version Key Vault access through explicit workload identity, and a short-lived credential response." />
  </a>
  <figcaption>Identity binds OAuth tokens to active project and run grants. Secret redemption rechecks the same exact grant after Key Vault acquisition.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.drawio'">Open editable draw.io source</a></p>

The broker issues `project_id` and `run_id` claims only after it finds an active grant for the authenticated local user. Token exchange and refresh repeat that binding check.

`POST /secrets/redeem` accepts a `SecretRef` identifier and exact version, purpose, and run ID. The bearer token supplies actor, project, and run claims. The broker does not accept caller-supplied identity claims.

`AuthorizedSecretRedemption` checks the exact grant before and after backend acquisition. A changed, expired, revoked, missing, or ambiguous grant denies redemption. The broker invalidates an acquired credential after the response.

The Key Vault adapter uses an injected credential or explicit `WorkloadIdentityCredentialOptions`. The AKS composition requires tenant ID, client ID, and an absolute projected token-file path. It has no `DefaultAzureCredential` fallback.

The adapter requests the exact Key Vault version. A returned credential expires within five minutes or at the earlier vault expiry. The grant store keeps references and binding snapshots, not secret values.

No grant-management HTTP endpoint exists. Read [contracts and endpoints](../reference/contracts) for the implemented route list and [configuration](../reference/contracts#identity-host-configuration) for host keys.
