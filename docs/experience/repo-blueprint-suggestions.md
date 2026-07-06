# Repository blueprint suggestions

The **Suggested** tab appears in **Create project from GitHub** after a repository is selected or pasted. It analyzes the repository and recommends one catalog blueprint so users can start with a sensible team, workflow, review policy, and sandbox posture without writing a generation prompt.

For the API contract see the [reference](../reference/repo-blueprint-suggestions.md); for the implementation flow see the [deep dive](../deep-dive/repo-blueprint-suggestions.md).

## When it appears

Open **Projects** → **Create from GitHub**. The dialog has repository fields on the left and blueprint choices on the right. The right side defaults to **Suggested** (`apps/web/src/pages/ProjectGalleryPage.tsx:516`) and offers three tabs: **Suggested**, **Templates**, and **Generate** (`ProjectGalleryPage.tsx:660`).

The Suggested panel only analyzes when both are true:

- the tab is active; and
- the repository field has an `owner/repo` value (`apps/web/src/components/BlueprintPicker.tsx:301`).

If no repository is selected, the card says **Select a repository first** and explains that Agentweaver will analyze it and suggest a matching blueprint (`BlueprintPicker.tsx:318`).

## Step by step

1. Click **Create from GitHub**.
2. Choose a repository from search/recent/org results, or paste an `owner/repo` value. Selecting a repo autofills the project name and folder slug when those fields have not been edited (`apps/web/src/pages/ProjectGalleryPage.tsx:529`).
3. Stay on **Suggested**. The panel shows **Analyzing repository...** while `apiClient.suggestBlueprint` calls `POST /api/blueprints/suggest` (`BlueprintPicker.tsx:305`).
4. Review the **Recommended** card. It shows the blueprint name, rationale, roster chips, agent count, and confidence percentage (`BlueprintPicker.tsx:337`).
5. Expand details to see repository signals such as description, topics, languages, root files, and issues-enabled (`BlueprintPicker.tsx:350`).
6. Click **Use this blueprint** to apply it, or choose **Other blueprints** / **Generate** if the recommendation is not right (`BlueprintPicker.tsx:352`).
7. Click **Create project**. The create request carries the chosen catalog blueprint id or generated inline blueprint through the existing project creation path (`BlueprintPicker.tsx:371`).

## How the recommendation is chosen

The recommendation is a catalog match, not a model-generated blueprint. The API reads GitHub metadata, languages, and root files using the submitting user's GitHub token, builds display signals, then maps AI/LLM repos, docs/content repos, product/design repos, and code repos to catalog blueprint ids (`apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:51`, `:132`, `:149`).

That means the recommendation is fast and predictable. If you want a bespoke team or workflow from a written description, switch to **Generate** and click **Generate blueprint** (`apps/web/src/components/BlueprintPicker.tsx:236`).

## Fallback to Templates

If repository analysis cannot run, the experience does not block project creation. The panel shows a warning such as **Repository analysis unavailable. Choose a template instead.** and renders a **Templates** section with starter choices (`BlueprintPicker.tsx:323`). The same fallback appears for invalid repository strings, unavailable GitHub metadata, network failures, and service fallback responses (`GitHubRepoBlueprintSuggestionService.cs:89`, `:93`).

## Related reading

- [Projects experience](./projects.md) — the full project creation flow.
- [Repository blueprint suggestions — Reference](../reference/repo-blueprint-suggestions.md) — route, DTOs, status codes, and mapping rules.
- [Repository blueprint suggestions — Deep Dive](../deep-dive/repo-blueprint-suggestions.md) — analysis-to-mapping flow.
