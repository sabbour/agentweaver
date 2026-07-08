# Project generation model settings

Project generation model settings let you pin the model used for project-shaping work without changing the default model used by normal agent runs. For the API see the [reference](../reference/project-generation-model-settings.md); for the implementation flow see the [deep dive](../deep-dive/project-generation-model-settings.md).

## When to use it

Use these settings when a project needs a different model for planning surfaces:

- **Blueprint generation model** — the model that turns a written description into a team blueprint.
- **Workflow generation model** — the model used when blueprint generation says no library workflow fits and Agentweaver drafts a custom workflow.
- **Outcome spec generation model** — the model that drafts the coordinator's initial outcome plan before dispatch.

Leave a field blank to inherit the global generation default shown in the UI (`apps/web/src/pages/ProjectSettingsPage.tsx:540`). Blank values are saved as `null` (`ProjectSettingsPage.tsx:342`).

## Step by step

1. Open a project and go to **Settings**.
2. In **Generation models**, fill one or more model id fields: **Blueprint generation model**, **Workflow generation model**, or **Outcome spec generation model** (`ProjectSettingsPage.tsx:538`).
3. Click **Save**. The browser sends `PUT /api/projects/{id}/provider-settings` with the three generation fields and preserves the existing default provider settings (`ProjectSettingsPage.tsx:334`).
4. If the save succeeds, the page shows **Generation model settings saved.** (`ProjectSettingsPage.tsx:586`).
5. To inherit defaults again, click **Reset to inherit**. The UI saves all three generation fields as `null` (`ProjectSettingsPage.tsx:566`, `:342`).

## What changes after save

- The next **Generate blueprint** request for this project uses `blueprint_generation_model` after resolving it through `GenerationModelOptions.ResolveBlueprintModel` (`apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:58`).
- If generated blueprint parsing signals that no library workflow fits, the workflow fallback uses `workflow_generation_model` in `WorkflowGenerationRequest.GenerationModel` (`apps/Agentweaver.Api/Blueprints/BlueprintService.cs:554`).
- The next coordinator run in this project uses `outcome_spec_generation_model` when drafting the outcome spec (`apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:247`).

Existing runs do not change; these settings affect future generation calls.

## Error behavior

If a model id is not allowed, the settings save returns **model_id is not allowed** (`apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:274`). If a configured generation model cannot be used later, the generation endpoint now surfaces provider-specific remediation instead of hiding the failure as generic validation: auth failures, rate limits, unavailable model lists, unavailable models, runtime misconfiguration, and transient provider failures are classified by `AgentProviderException` (`packages/Agentweaver.AgentRuntime/Providers/AgentProviderException.cs:41`).

## Related reading

- [Project generation model settings — Reference](../reference/project-generation-model-settings.md)
- [Project generation model settings — Deep Dive](../deep-dive/project-generation-model-settings.md)
- [Repository blueprint suggestions](./repo-blueprint-suggestions.md)
- [Coordinator & orchestration](./coordinator-orchestration.md)
