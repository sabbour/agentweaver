# Repository blueprint suggestions

The **Suggested** tab in **Create project from GitHub** recommends a catalog blueprint
from repository signals. Repository selection is constrained by the caller's Repo App
authorization; entering a name is not an access grant. Suggestions help choose a team,
workflow, review policy, and sandbox posture without writing a generation prompt.

For the API contract see the [reference](../reference/repo-blueprint-suggestions.md); for the implementation flow see the [deep dive](../deep-dive/repo-blueprint-suggestions.md).

## When it appears

Open **Projects** → **Create from GitHub**. The dialog has repository fields on the left and the shared **Blueprint** panel on the right. The right side defaults to **Suggested** and offers three tabs: **Suggested**, **Templates**, and **Generate** (`apps/web/src/pages/ProjectGalleryPage.tsx:676`).

The Suggested panel only analyzes when both are true:

- the tab is active; and
- the repository field has an `owner/repo` value (`apps/web/src/components/BlueprintPicker.tsx:301`).

If no repository is selected, the card says **Select a repository first** and explains that Agentweaver will analyze it and suggest a matching blueprint (`BlueprintPicker.tsx:318`).

## Step by step

1. Click **Create from GitHub**.
2. Connect the Repo App if required, then select an available repository. The paste field
   accepts a repository **the Repo App can access**, not an arbitrary unauthenticated
   source (`apps/web/src/pages/ProjectGalleryPage.tsx:707`).
3. Stay on **Suggested**. The panel shows **Analyzing repository...** while `apiClient.suggestBlueprint` calls `POST /api/blueprints/suggest` (`BlueprintPicker.tsx:305`).
4. Review the **Recommended** card. It shows the blueprint name, rationale, roster chips, agent count, and confidence percentage (`BlueprintPicker.tsx:337`).
5. Expand details to see repository signals such as description, topics, languages, root files, and issues-enabled (`BlueprintPicker.tsx:350`).
6. Click **Use this blueprint** to apply it. If the recommendation is not right, click **View all templates →** to switch to **Templates**, or choose **Generate** for a custom blueprint (`BlueprintPicker.tsx:350`, `:352`, `:371`).

7. Create the project. The browser verifies the repository against authorized selections
   and obtains a `repository_selection_code`; creation carries that code plus the chosen
   catalog blueprint ID or generated inline blueprint (`ProjectGalleryPage.tsx:259-268`).

## How the recommendation is chosen

The recommendation is a **deterministic catalog match**, not a model-generated blueprint.
The service resolves GitHub access server-side, reads metadata, languages, and root files,
builds display signals, and maps them to catalog blueprint IDs
(`apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:51-83`, `:142`).
This best-effort suggestion neither grants repository access nor persists a new blueprint.

That means the recommendation is fast and predictable. If you want a bespoke team or workflow from a written description, switch to **Generate** and click **Generate blueprint** (`apps/web/src/components/BlueprintPicker.tsx:236`).

## Fallback to Templates

If repository analysis cannot run, the experience does not block project creation. The panel shows a warning such as **Repository analysis unavailable. Choose a template instead.** and offers **View all templates →**, which switches to the shared **Templates** tab (`BlueprintPicker.tsx:323`, `:371`). The same fallback appears for invalid repository strings, unavailable GitHub metadata, network failures, and service fallback responses (`GitHubRepoBlueprintSuggestionService.cs:89`, `:93`).

## Repository access boundary

Use the Repo App-authorized selection list for both personal and organization repositories.
Creation requires a caller-bound, short-lived, single-use selection code. A successful
metadata suggestion is not proof that creation is authorized; the create-time selection
check still applies. If analysis fails, Templates remains available, but GitHub-backed
creation still requires repository access.

## Related reading

- [Projects experience](./projects.md) — the full project creation flow.
- [Project generation model settings](./project-generation-model-settings.md) — choose per-project models for blueprint/workflow generation.
- [Repository blueprint suggestions — Reference](../reference/repo-blueprint-suggestions.md) — route, DTOs, status codes, and mapping rules.
- [Repository blueprint suggestions — Deep Dive](../deep-dive/repo-blueprint-suggestions.md) — analysis-to-mapping flow.
