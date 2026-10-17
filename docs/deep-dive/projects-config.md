# Projects and configuration

## The mental model

A project is the durable boundary for configuration and authorization. It is not a repository checkout. Projects & Config stores the project's identity, lifecycle, team, skills, model selection, and provider choices. Environment and Source Control own the resources used to execute that configuration.

The service separates editable configuration from accepted run selection. Editing a project changes a revision head. Accepting a run freezes the exact inputs used by that run. Later edits do not rewrite that snapshot.

## Where this lives

All service paths below are under `services/projects-config/Agentweaver.Projects.Config/`.

- `Program.cs` and `ProjectConfigEndpoints.cs` — host composition and HTTP routes.
- `ProjectAuthorization.cs` — current membership, roles, scopes, and effective permissions.
- `ProjectConfiguration.cs` — typed settings, validation, limits, and egress narrowing.
- `ProjectsConfigService.cs` — project lifecycle, revision writes, and run selection.
- `ProjectsConfigDbContext.cs` and `ProjectConfigRecords.cs` — service-owned persistence.
- `ProjectsConfigCastingService.cs` and `ProjectsConfigCastingTransferService.cs` — reviewed team proposals and transfers.
- `ProjectSkillContentService.cs` and `ProjectMarketplaceSourceService.cs` — imported skill content and source ownership.
- `PrivilegedProjectSetupCommand.cs` and `ProjectsConfigPrivilegedAuthorityStore.cs` — separate privileged setup.
- `ProjectsConfigMigrator.cs` — explicit migration and runtime permission checks.

## Core concepts and invariants

### Authorization stays current

The service owns issuer-and-subject tenant memberships and resource-role assignments in `projects_config`. OAuth scopes are necessary route permissions, not a substitute for those assignments.

Project reads require a current project role or tenant administrator. Writes require Owner or tenant administrator authority. Project creation requires an existing tenant administrator and does not automatically assign an Owner. Platform defaults require platform administrator authority. Run selection requires the project Orchestrator role and its scopes.

`GET /api/authorization/context` projects these effective permissions for other services. It returns a versioned, no-store response. A signed project/run binding restricts the target; headers and token role claims cannot create authority.

The runtime database principal reads authority records but cannot mutate them. Setup and revocation use the separate privileged source path. Ordinary service requests do not bootstrap their own membership.

### Each revision has one purpose

Project metadata has its own revision. Project configuration and platform runtime defaults have separate revision histories. Configuration contains typed model references, provider overrides, charters, casting, workflow references, skill pins, egress narrowing, run limits, and optional Source Control settings.

Updates supply the expected current revision. The service appends a new revision and advances the head. A competing update produces a conflict instead of overwriting the other writer.

## From configuration to an accepted run

1. The Orchestrator supplies a run ID, expected project-configuration and platform-default revisions, and a revisioned selection context.
2. The service starts a serializable transaction and locks current authority, the project and platform heads, and the run ID.
3. It rechecks the Orchestrator role. A new run requires an active project and configured platform defaults.
4. It selects the project model or platform default and checks that the reference is available in the supplied context.
5. `ProviderResolver` resolves candidates from the enabled platform catalog. Project choices cannot bypass catalog override permissions.
6. Egress requirements must fit the platform baseline and project narrowing. Project run limits cannot widen platform limits.
7. The service writes the immutable effective selection and its request fingerprint, then rechecks authority before commit.

An exact retry returns the stored snapshot without reopening mutable configuration inputs. A different request for the same run ID conflicts. Archiving blocks new acceptance; it does not erase an already accepted snapshot.

The result contains provider candidates, not provisioned resource pins. Each resource owner negotiates its actual backend and persists the consumer binding. See [Providers and models](./providers-models.md).

## Reviewed team and skill changes

Casting proposals stage a complete configuration draft for review. Scenario templates and manual role selections produce version-bound proposals. Confirmation appends a configuration revision only while the proposal and configuration remain current. Rejection leaves configuration unchanged.

Skills use imported content revisions and digests, with assignments to project agents. Configuration validation checks these pins. An accepted run reads its assigned imported revisions, not whatever content a marketplace source returns later. Marketplace browsing and imported project content have different owners and lifetimes.

## Failure and recovery

Stale revisions, unavailable model references, invalid skill pins, and incompatible provider selections stop acceptance. The service does not replace them with a different default. Revoked authority blocks privileged operations even when the token has not expired.

Ordinary startup verifies applied migrations and runtime database privileges. It does not apply migrations. Retained revisions and idempotent selection replay provide recovery without rewriting accepted history.

## Related pages

- [Projects & Config contract](../architecture/projects-config.md)
- [Skill marketplace sources](../architecture/skill-marketplace.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Identity and secrets](./identity-secrets.md)
