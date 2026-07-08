# Repository blueprint suggestions

The **Suggested** tab appears in **Create project from GitHub** after a repository is selected or pasted. It analyzes the repository and recommends one catalog blueprint so users can start with a sensible team, workflow, review policy, and sandbox posture without writing a generation prompt.

For the API contract see the [reference](../reference/repo-blueprint-suggestions.md); for the implementation flow see the [deep dive](../deep-dive/repo-blueprint-suggestions.md).

## When it appears

Open **Projects** → **Create from GitHub**. The dialog has repository fields on the left and the shared **Blueprint** panel on the right. The right side defaults to **Suggested** and offers three tabs: **Suggested**, **Templates**, and **Generate** (`apps/web/src/pages/ProjectGalleryPage.tsx:676`).

The Suggested panel only analyzes when both are true:

- the tab is active; and
- the repository field has an `owner/repo` value (`apps/web/src/components/BlueprintPicker.tsx:301`).

If no repository is selected, the card says **Select a repository first** and explains that Agentweaver will analyze it and suggest a matching blueprint (`BlueprintPicker.tsx:318`).

## Step by step

1. Click **Create from GitHub**.
2. Choose a repository from search/recent/account results, or paste an `owner/repo` value. The account list starts with the signed-in user's personal account, shown as `@{login}` with a **You** badge, then lists organizations; selecting any account reloads its repositories (`apps/web/src/pages/ProjectGalleryPage.tsx:471`, `:501`, `:642`, `:645`). Selecting a repo autofills the project name and folder slug when those fields have not been edited (`ProjectGalleryPage.tsx:559`).
3. Stay on **Suggested**. The panel shows **Analyzing repository...** while `apiClient.suggestBlueprint` calls `POST /api/blueprints/suggest` (`BlueprintPicker.tsx:305`).
4. Review the **Recommended** card. It shows the blueprint name, rationale, roster chips, agent count, and confidence percentage (`BlueprintPicker.tsx:337`).
5. Expand details to see repository signals such as description, topics, languages, root files, and issues-enabled (`BlueprintPicker.tsx:350`).
6. Click **Use this blueprint** to apply it. If the recommendation is not right, click **View all templates →** to switch to **Templates**, or choose **Generate** for a custom blueprint (`BlueprintPicker.tsx:350`, `:352`, `:371`).
7. Click **Create project**. The create request carries the chosen catalog blueprint id or generated inline blueprint through the existing project creation path (`BlueprintPicker.tsx:371`).

## How the recommendation is chosen

The recommendation is a catalog match, not a model-generated blueprint. The API reads GitHub metadata, languages, and root files using the submitting user's GitHub token, builds display signals, then maps AI/LLM repos, docs/content repos, product/design repos, and code repos to catalog blueprint ids (`apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:51`, `:132`, `:149`).

That means the recommendation is fast and predictable. If you want a bespoke team or workflow from a written description, switch to **Generate** and click **Generate blueprint** (`apps/web/src/components/BlueprintPicker.tsx:236`).

## Fallback to Templates

If repository analysis cannot run, the experience does not block project creation. The panel shows a warning such as **Repository analysis unavailable. Choose a template instead.** and offers **View all templates →**, which switches to the shared **Templates** tab (`BlueprintPicker.tsx:323`, `:371`). The same fallback appears for invalid repository strings, unavailable GitHub metadata, network failures, and service fallback responses (`GitHubRepoBlueprintSuggestionService.cs:89`, `:93`).

## Personal repositories

The GitHub picker now includes personal repositories, not only organization repositories. `GET /api/github/accounts` returns the authenticated user first with `type: "user"`, and the UI labels that entry **You** (`apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:207`, `:249`; `apps/web/src/pages/ProjectGalleryPage.tsx:642`). `GET /api/github/repos?account=<login>` uses GitHub `/user/repos?affiliation=owner` when the account is the signed-in user, so search, recent repositories, and manual selection all surface personal repositories too (`AuthEndpoints.cs:295`, `:333`; `apps/web/src/api/client.ts:267`).

## Related reading

- [Projects experience](./projects.md) — the full project creation flow.
- [Project generation model settings](./project-generation-model-settings.md) — choose per-project models for blueprint/workflow generation.
- [Repository blueprint suggestions — Reference](../reference/repo-blueprint-suggestions.md) — route, DTOs, status codes, and mapping rules.
- [Repository blueprint suggestions — Deep Dive](../deep-dive/repo-blueprint-suggestions.md) — analysis-to-mapping flow.
