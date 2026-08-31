# Project generation model settings — Reference

Terse reference for the per-project model ids used by generation paths. For implementation details see the [deep dive](../deep-dive/project-generation-model-settings.md); for the web flow see the [experience guide](../experience/project-generation-model-settings.md).

## Route

| Method & path | Body | Returns | Notes |
|---|---|---|---|
| `PUT /api/projects/{id}/provider-settings` | `UpdateProjectProviderSettingsRequest` | `204 No Content` | Updates default provider/model fields and the three generation model fields. Owner-only. |

Source: `apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:258`.

## Request fields

`UpdateProjectProviderSettingsRequest` is defined in `apps/Agentweaver.Api/Contracts/Dtos.cs:582`; the web type mirrors it at `apps/web/src/api/types.ts:241`.

| Field | Type | Meaning |
|---|---|---|
| `default_provider` | string \| null | Existing default provider field. |
| `default_model_github_copilot` | string \| null | Existing default GitHub Copilot run model. |
| `default_model_microsoft_foundry` | string \| null | Existing default BYOK run model; the legacy field name remains supported. |
| `blueprint_generation_model` | string \| null | Model id for blueprint JSON generation. Null inherits the configured blueprint generation default. |
| `workflow_generation_model` | string \| null | Model id for generated workflow fallback. Null inherits the configured workflow generation default. |
| `outcome_spec_generation_model` | string \| null | Model id for coordinator outcome-spec drafting. Null inherits the configured outcome-spec generation default. |

## Project response fields

`ProjectResponse` includes the same three generation model fields (`apps/Agentweaver.Api/Contracts/Dtos.cs:605`), and `apps/web/src/api/types.ts:183` exposes them on `Project`.

## Persistence

| Field | Database column | Source |
|---|---|---|
| `ProjectRecord.BlueprintGenerationModel` | `blueprint_generation_model` | `ProjectRecord.cs:26`; `MemoryDbContext.cs:227` |
| `ProjectRecord.WorkflowGenerationModel` | `workflow_generation_model` | `ProjectRecord.cs:27`; `MemoryDbContext.cs:228` |
| `ProjectRecord.OutcomeSpecGenerationModel` | `outcome_spec_generation_model` | `ProjectRecord.cs:28`; `MemoryDbContext.cs:229` |

Postgres migration: `apps/Agentweaver.Api.Migrations.Postgres/Migrations/20260708040300_AddProjectGenerationModelSettings.cs`.

## Validation and status codes

| Code | When |
|---|---|
| `204 No Content` | Settings were saved. |
| `400 Bad Request` | Project id is invalid, a model id fails `IsAllowedModelId` (the check uses a permissive `^(gpt\|claude\|o)` prefix regex — no hardcoded allowlist), or service validation throws `ArgumentException`. |
| `403 Forbidden` | Caller does not own the project. |
| `404 Not Found` | Project does not exist or update found no row. |

Source: `apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:263`, `:270`, `:274`, `:293`.

## Consumers

| Setting | Consumer | Source |
|---|---|---|
| `blueprint_generation_model` | `POST /api/blueprints/generate` resolves it with `GenerationModelOptions.ResolveBlueprintModel` before `BlueprintService.GenerateAsync`. | `BlueprintEndpoints.cs:58` |
| `workflow_generation_model` | `BlueprintService.GenerateAsync` places it in `WorkflowGenerationRequest.GenerationModel` for `IWorkflowGenerator` fallback. | `BlueprintService.cs:554` |
| `outcome_spec_generation_model` | `CoordinatorRunService.ActivateAsync` resolves it and passes it to `CoordinatorDraftInput`. | `CoordinatorRunService.cs:247` |

## Provider failure surface

Blueprint generation returns classified provider errors when the configured model or provider cannot be used. `AgentProviderException` maps Copilot auth, rate-limit, model-list, unavailable-model, runtime, and transient failures to machine-readable codes (`packages/Agentweaver.AgentRuntime/Providers/AgentProviderException.cs:41`). `BlueprintService.ProviderFailureResult` stores those as `ErrorCode` and `FailureMessage`, and `BlueprintEndpoints` returns JSON with `error`, `message`, `details`, and `options` (`BlueprintService.cs:625`; `BlueprintEndpoints.cs:63`).

## See also

- [Project generation model settings — Deep Dive](../deep-dive/project-generation-model-settings.md)
- [Project generation model settings — Experience](../experience/project-generation-model-settings.md)
- [Repository blueprint suggestions — Reference](./repo-blueprint-suggestions.md)
- [Coordinator reference](./coordinator.md)
