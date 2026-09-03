# Auth and security

## Platform identity and access

Agentweaver uses Microsoft Entra ID for platform authentication. A protected request must have a valid Entra identity or an Agentweaver broker token accepted by that endpoint.

Platform roles control platform-level access:

- `PlatformAdmin`
- `ProjectCreator`
- `Contributor`
- `Viewer`

Project roles control access to individual projects:

- `Owner`
- `Contributor`
- `Viewer`

A GitHub connection is a separate capability for repository and Copilot operations. It cannot grant platform or project access.

## MCP authentication

Agentweaver is an OAuth 2.1 authorization server for MCP clients. A client uses discovery metadata, PKCE S256, authorization code exchange, token refresh, and token revocation.

The authorization server authenticates the user with Entra before issuing an Agentweaver broker token. The MCP server accepts only a valid broker token with its expected issuer, resource audience, signature, lifetime, subject, and `mcp:invoke` scope. It forwards that token to the API, which still enforces project authorization.

## GitHub capabilities

GitHub is brokered after platform authentication. The API creates a capability handoff for a permitted project and uses the resulting capability only for the required repository or Copilot operation.

Missing or invalid GitHub capabilities fail closed. Agentweaver does not use GitHub OAuth as a platform sign-in path, GitHub organization rules as a platform gate, or ambient per-user GitHub token stores.

## AgentHost configuration

AgentHost warm-pool pods start without run identity. After a claim binds, the API sends one `/configure` request with the run context.

The payload includes:

- run, project, agent, and execution-purpose identity;
- shared and local workspace descriptors;
- turn authentication and approval settings;
- repository and preview credentials when required;
- an optional MCP broker token; and
- one model-provider alternative: `copilotCredential` or `byokProviderConfiguration`.

`copilotCredential` is required only when no BYOK provider configuration is supplied. AgentHost does not retrieve ambient user credentials from Key Vault, CSI volumes, shared storage, or host configuration.

The endpoint accepts one configuration per pod. It is protected by the in-cluster NetworkPolicy because it delivers the turn credential. After configuration, AgentHost accepts A2A turns only when their bearer token matches the configured run.

## Guardrails

- The fallback policy denies unclassified endpoints.
- OAuth redirect and client validation happens before a redirect.
- Public OAuth endpoints apply rate limits.
- Broker access tokens are short-lived.
- Refresh-token families and persisted token entries can be revoked.
- Credentials and secret values are not logged.
- A run capability is limited to its run and intended purpose.

## Source

- `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs`
- `apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs`
- `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs`
- `apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs`
- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`
- `apps/Agentweaver.AgentHost/Program.cs`

## Related reading

- [MCP server](./mcp-server.md)
- [AgentHost capability credential delivery](./agent-token-delivery.md)
- [Sandbox pod execution](./sandbox-pod-execution.md)
