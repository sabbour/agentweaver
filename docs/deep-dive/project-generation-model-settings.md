# Project generation model settings — Deep Dive

Project generation model settings let one project choose different GitHub Copilot model ids for three planning surfaces: blueprint generation, workflow generation, and outcome-spec drafting. The setting is project data, not a global switch. For the API contract see the [reference](../reference/project-generation-model-settings.md); for the operator flow see the [experience guide](../experience/project-generation-model-settings.md).

## Flow

![Three project model preferences feed generation consumers without replacing provider admission](../diagrams/project-generation-model-settings-fig1.png)

<!-- Editable source: ../diagrams/src/project-generation-model-settings-fig1.drawio.
     Export with pinned draw.io Desktop 31.4.5 using --spec project-generation-model-settings-fig1.
     Review lineage: ../diagrams/reviews/project-generation-model-settings-fig1/iteration-manifest.json. -->

## Stored fields

`ProjectRecord` now stores `BlueprintGenerationModel`, `WorkflowGenerationModel`, and `OutcomeSpecGenerationModel` (`apps/Agentweaver.Api.Data/Memory/ProjectRecord.cs:26`). `MemoryDbContext` maps them to `blueprint_generation_model`, `workflow_generation_model`, and `outcome_spec_generation_model` (`MemoryDbContext.cs:227`). The Postgres migration `20260708040300_AddProjectGenerationModelSettings.cs` adds the database columns; the project response DTO and web `Project` type expose the same snake_case fields (`apps/Agentweaver.Api/Contracts/Dtos.cs:588`; `apps/web/src/api/types.ts:183`).

## Save path

The web settings page has a **Generation models** section with three text fields and a **Reset to inherit** action (`apps/web/src/pages/ProjectSettingsPage.tsx:538`). Saving calls `PUT /api/projects/{id}/provider-settings` with the three generation model ids plus the existing default-provider fields (`ProjectSettingsPage.tsx:334`). The API rejects model ids that are not allowed by `IsAllowedModelId` and persists the three values through `ProjectService.UpdateProviderSettingsAsync` (`apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:270`).

Blank values are saved as `null`, which means inherit the configured global generation default in the resolver path (`ProjectSettingsPage.tsx:342`).

Resolution follows project preference, per-flow configuration, shared generation model, then the
built-in default. These are model preferences, not credential or provider authority. Generative
operations still require [effective model-provider admission](./api-core.md#effective-model-provider-admission),
including caller/project/operation binding and pre-call provider revalidation. A saved model ID
cannot bypass that fence or select an ambient credential.

## Runtime consumers

- **Blueprint generation** reads the project, resolves `project.BlueprintGenerationModel` through `GenerationModelOptions.ResolveBlueprintModel`, and passes it to `BlueprintService.GenerateAsync` (`apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:58`). `CopilotBlueprintGenerator.GenerateRawAsync` sends that `modelId` to `IAgentRunner.ExecuteAsync`, falling back to its configured default only when the project model is null (`apps/Agentweaver.Api/Blueprints/CopilotBlueprintGenerator.cs:36`, `:236`).
- **Workflow generation fallback** receives `workflowGenerationModel` from `BlueprintService.GenerateAsync` and places it in `WorkflowGenerationRequest.GenerationModel` before invoking `IWorkflowGenerator` (`apps/Agentweaver.Api/Blueprints/BlueprintService.cs:460`, `:554`).
- **Outcome-spec drafting** resolves `project.OutcomeSpecGenerationModel` when a coordinator run activates, then carries it in `CoordinatorDraftInput.OutcomeSpecGenerationModel` (`apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:247`, `:281`).

## Why this hardens generation

The generation paths now preserve the model chosen for the specific project surface. Blueprint generation provider failures are returned as classified `BlueprintGenerationFailureKind` values with a stable `ErrorCode` and `FailureMessage`, rather than being treated as validation drift (`apps/Agentweaver.Api/Blueprints/BlueprintService.cs:475`, `:625`). The blueprint parser also preserves an empty `workflows` array as the explicit "no library workflow fits" signal instead of defaulting to `default` (`apps/Agentweaver.Api/Blueprints/IBlueprintGenerator.cs:103`). If fallback workflow generation is required, it uses the workflow generation model configured for the project.

## Source

| Concern | File |
|---|---|
| Project fields and EF mapping | `apps/Agentweaver.Api.Data/Memory/ProjectRecord.cs`; `MemoryDbContext.cs` |
| Migration | `apps/Agentweaver.Api.Migrations.Postgres/Migrations/20260708040300_AddProjectGenerationModelSettings.cs` |
| Request/response DTOs | `apps/Agentweaver.Api/Contracts/Dtos.cs`; `apps/web/src/api/types.ts` |
| Settings UI | `apps/web/src/pages/ProjectSettingsPage.tsx` |
| Provider-settings endpoint | `apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs` |
| Blueprint and workflow generation use | `apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs`; `BlueprintService.cs`; `CopilotBlueprintGenerator.cs`; `IBlueprintGenerator.cs` |
| Outcome-spec generation use | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs`; `CoordinatorMessages.cs` |

## See also

- [Project generation model settings — Reference](../reference/project-generation-model-settings.md)
- [Project generation model settings — Experience](../experience/project-generation-model-settings.md)
- [Repository blueprint suggestions](../experience/repo-blueprint-suggestions.md)
- [Coordinator & orchestration](../experience/coordinator-orchestration.md)

<!-- diagram-context:project-generation-model-settings-fig1:start -->
<details id="diagram-context-project-generation-model-settings-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Generation preferences, not authority</td></tr>
<tr><td>takeaway</td><td>Three project preferences select models; execution admission separately authorizes use.</td></tr>
<tr><td>group-title-0</td><td>PERSISTED SELECTION + PRECEDENCE</td></tr>
<tr><td>group-title-1</td><td>FLOW CONSUMERS + AUTHORITY</td></tr>
<tr><td>Project settings</td><td>Project settings</td></tr>
<tr><td>Project settings</td><td>Blueprint · workflow · outcome spec</td></tr>
<tr><td>Project settings</td><td>Three nullable model preferences</td></tr>
<tr><td>Project settings</td><td>ProjectSettingsPage.tsx:617-648</td></tr>
<tr><td>Blueprint generation</td><td>Blueprint generation</td></tr>
<tr><td>Blueprint generation</td><td>Resolved blueprint preference</td></tr>
<tr><td>Blueprint generation</td><td>Generation still requires admission</td></tr>
<tr><td>Blueprint generation</td><td>BlueprintEndpoints.cs:119-141</td></tr>
<tr><td>Project record</td><td>Project record</td></tr>
<tr><td>Project record</td><td>Save nullable preference values</td></tr>
<tr><td>Project record</td><td>Preferences do not contain credentials</td></tr>
<tr><td>Project record</td><td>ProjectEndpoints.cs:815-817</td></tr>
<tr><td>Fallback workflow generation</td><td>Fallback workflow generation</td></tr>
<tr><td>Fallback workflow generation</td><td>Resolved workflow preference</td></tr>
<tr><td>Fallback workflow generation</td><td>Used when no library workflow selected</td></tr>
<tr><td>Fallback workflow generation</td><td>BlueprintService.cs:765-773</td></tr>
<tr><td>Generation model resolver</td><td>Generation model resolver</td></tr>
<tr><td>Generation model resolver</td><td>Project → per-flow configuration</td></tr>
<tr><td>Generation model resolver</td><td>Then shared generation model → default</td></tr>
<tr><td>Generation model resolver</td><td>GenerationModelOptions.cs:37-75</td></tr>
<tr><td>Coordinator spec drafter</td><td>Coordinator spec drafter</td></tr>
<tr><td>Coordinator spec drafter</td><td>Resolved outcome-spec preference</td></tr>
<tr><td>Coordinator spec drafter</td><td>Coordinator input supplies the preference</td></tr>
<tr><td>Coordinator spec drafter</td><td>CopilotCoordinatorSpecDrafter:145</td></tr>
<tr><td>Caller + project authority</td><td>Caller + project authority</td></tr>
<tr><td>Caller + project authority</td><td>Execution-plan / provider admission</td></tr>
<tr><td>Caller + project authority</td><td>Selection never grants credential access</td></tr>
<tr><td>Caller + project authority</td><td>AiExecutionPlanService.cs</td></tr>
<tr><td>Admitted model invocation</td><td>Admitted model invocation</td></tr>
<tr><td>Admitted model invocation</td><td>Authorized provider execution</td></tr>
<tr><td>Admitted model invocation</td><td>Selected model and admitted access differ</td></tr>
<tr><td>Project settings</td><td>save</td></tr>
<tr><td>Project record</td><td>preferences</td></tr>
<tr><td>Generation model resolver</td><td>model</td></tr>
<tr><td>Caller + project authority</td><td>authorize</td></tr>
<tr><td>scope</td><td>Consumer cards are separate generation flows. The authority row is not a preference inheritance step.</td></tr>
<tr><td>groups</td><td>PERSISTED SELECTION + PRECEDENCE; FLOW CONSUMERS + AUTHORITY</td></tr>
</tbody></table>
</details>
<!-- diagram-context:project-generation-model-settings-fig1:end -->
