# Projects and configuration

Use this page to understand how Projects & Config manages project authority,
revisioned settings, and the immutable configuration snapshot for a run. A
project is not a repository checkout: Environment and Source Control own the
resources used to execute its accepted configuration.

## Understand project authority and revisions

Projects & Config stores project identity, lifecycle, team, skills, model
selection, and provider choices. It separates editable configuration from an
accepted run selection. Editing advances a revision; accepting a run freezes
the exact inputs for that run. Later edits do not rewrite the snapshot.

| Source | Responsibility |
| --- | --- |
| `ProjectAuthorization.cs` | Current membership, roles, scopes, and effective permissions |
| `ProjectConfiguration.cs` | Typed settings, validation, limits, and egress narrowing |
| `ProjectsConfigService.cs` | Project lifecycle, revision writes, and run selection |
| `ProjectsConfigDbContext.cs` and `ProjectConfigRecords.cs` | Service-owned persistence |
| `ProjectsConfigCastingService.cs` and `ProjectsConfigCastingTransferService.cs` | Reviewed team proposals and transfers |
| `ProjectSkillContentService.cs` and `ProjectMarketplaceSourceService.cs` | Imported skill content and source ownership |
| `ProjectsConfigMigrator.cs` | Explicit migration and runtime permission checks |

All paths in this table are under
`services/projects-config/Agentweaver.Projects.Config/`.

The service owns issuer-and-subject tenant memberships and resource-role
assignments in the `projects_config` schema. OAuth scopes are necessary route
permissions, not a substitute for those assignments.

- Project reads require a current project role or tenant administrator.
- Writes require Owner or tenant administrator authority.
- Project creation requires an existing tenant administrator; it does not
  automatically assign an Owner.
- Platform defaults require platform administrator authority.
- Run selection requires the project Orchestrator role and its scopes.

`GET /api/authorization/context` returns a versioned, no-store projection of
effective permissions. A signed project/run binding limits its target; headers
and token role claims cannot create authority.

The runtime database principal reads authority records but cannot mutate them.
Setup and revocation use a separate privileged path. Ordinary service requests
do not bootstrap membership.

## Edit configuration safely

Project metadata, project configuration, and platform runtime defaults have
separate revision histories. Configuration contains typed model references,
provider overrides, charters, casting, workflow references, skill pins, egress
narrowing, run limits, and optional Source Control settings.

When you update a revision, supply the expected current revision. The service
appends a new revision and advances the head. If another writer wins first,
your update conflicts instead of overwriting its change.

## Accept a run selection

Before acceptance, you need an active project, current Orchestrator authority,
configured platform defaults, and the current configuration revisions. The
selection request is made by Orchestrator with a run ID and revisioned selection
context.

1. The service starts a serializable transaction and locks current authority,
   project and platform heads, and the run ID.
2. It rechecks the Orchestrator role. A new run requires an active project and
   configured platform defaults.
3. It selects the project model or platform default and checks that the
   reference is available in the supplied context.
4. `ProviderResolver` resolves candidates from the enabled platform catalog.
   Project choices cannot bypass catalog override permissions.
5. Egress requirements must fit the platform baseline and project narrowing.
   Project run limits cannot widen platform limits.
6. The service writes the immutable effective selection and request fingerprint,
   then rechecks authority before commit.

An exact retry returns the saved snapshot without reopening mutable settings. A
different request for the same run ID conflicts. Archiving blocks new
acceptance; it does not erase an accepted snapshot.

The result contains provider candidates, not provisioned resource pins. Each
resource owner negotiates its actual backend and persists the consumer binding.
See [Providers and models](./providers-models.md).

For supported setting names and project setup, see the
[runtime configuration reference](../reference/runtime-configuration.md) and
[provider configuration guide](../guide/configure-providers.md).

## Review team and skill changes

Casting proposals stage a complete configuration draft for review. Scenario
templates and manual role selections produce version-bound proposals.
Confirmation appends a configuration revision only while the proposal and
configuration remain current. Rejection leaves configuration unchanged.
The [Gateway casting routes](../reference/api.md#casting-through-the-gateway)
expose the same review flow, templates, roles, and explicit transfers. The owner
still checks current permissions and revision fences; import is limited to
1 MiB at both entry points and never applies a team before confirmation.

Skills use imported content revisions and digests, with assignments to project
agents. Configuration validation checks these pins. An accepted run reads its
assigned imported revisions, not later content from a marketplace source.
Marketplace browsing and imported project content have different owners and
lifetimes.

## Recover from configuration errors

Stale revisions, unavailable model references, invalid skill pins, and
incompatible provider selections stop acceptance. The service does not replace
them with a different default. Revoked authority blocks privileged operations
even when a token has not expired.

Ordinary startup verifies applied migrations and runtime database privileges;
it does not apply migrations. Retained revisions and idempotent selection replay
provide recovery without rewriting accepted history.

## Related content

- [Projects & Config contract](../architecture/projects-config.md)
- [Skill marketplace sources](../architecture/skill-marketplace.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Platform components](../guide/platform-components.md)
- [Identity and secrets](./identity-secrets.md)
