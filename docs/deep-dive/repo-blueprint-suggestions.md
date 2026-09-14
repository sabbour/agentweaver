# Repository blueprint suggestions — Deep Dive

The **Suggested** blueprint flow recommends a catalog blueprint for a GitHub repository before the project is created. It is intentionally lightweight: the API reads repository signals from GitHub, maps those signals to one of the existing catalog blueprint ids, and returns a normal `BlueprintDto` for the create dialog to apply. It does not call a model; generation remains the separate **Generate** tab.

For the API contract see the [reference](../reference/repo-blueprint-suggestions.md); for the user flow see the [experience guide](../experience/repo-blueprint-suggestions.md).

## End-to-end flow

1. **The dialog has a repository.** `CreateFromGitHubDialog` keeps the active repository in `d.sourceRepository` and passes it into the shared `BlueprintPanel`, whose tab strip starts the GitHub flow on `suggested` (`apps/web/src/pages/ProjectGalleryPage.tsx:676`, `apps/web/src/components/BlueprintPicker.tsx:371`).
2. **The client calls the new endpoint.** `SuggestedBlueprintPanel` calls `apiClient.suggestBlueprint(normalizedRepo)` only when the tab is active and the repo string is non-empty (`apps/web/src/components/BlueprintPicker.tsx:301`, `:305`). The client method posts `{ "repository": "owner/repo" }` to `/blueprints/suggest` (`apps/web/src/api/client.ts:186`).
3. **The endpoint validates shape and identity.** `POST /api/blueprints/suggest` rejects blank `repository` with `400`, resolves the authenticated caller, and passes `caller.User` to the suggestion service (`apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:53`, `:59`, `:63`).
4. **The service parses GitHub coordinates.** `TryParseOwnerRepo` accepts `owner/repo`, a GitHub URL, and a `.git` suffix, then normalizes to owner and repo strings (`apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:17`, `:116`).
5. **GitHub metadata reads are anonymous in the current registration.** The service queries its credential boundary, but `EntraOnlyGitHubCredentialBoundary` returns no ambient access token. Repository selection does not automatically grant this heuristic service a Repo App credential (`EntraOnlyGitHubCredentialBoundary.cs:38-39`, `Program.cs:265-271`).
6. **Repository signals are collected.** The service reads repository metadata, languages, and root contents (`GitHubRepoBlueprintSuggestionService.cs:51`, `:58`, `:62`). `BuildSignals` exposes description, up to five topics, top languages, up to eight root files, and whether issues are enabled (`GitHubRepoBlueprintSuggestionService.cs:132`).
7. **Signals are mapped to catalog blueprint ids.** `PickBlueprint` scores text from name, description, topics, languages, and root file names. AI/LLM signals map to `blueprint-ai-agent-engineering`; docs/content-only signals map to `blueprint-content-authoring`; product/design-only signals map to `blueprint-product-management`; codebase signals or any non-Markdown language map to `blueprint-software-development` (`GitHubRepoBlueprintSuggestionService.cs:149`, `:163`, `:167`, `:171`, `:175`).
8. **Catalog lookup is safe.** If the mapped id is missing, the service falls back to `blueprint-software-development`, then the first available catalog blueprint (`GitHubRepoBlueprintSuggestionService.cs:70`). If GitHub analysis fails or no templates exist, the response sets `fallback: true` and confidence `0` (`GitHubRepoBlueprintSuggestionService.cs:89`, `:93`). The UI renders a warning plus **View all templates →**, which switches to the shared Templates tab rather than blocking project creation (`BlueprintPicker.tsx:323`, `:371`).

## Shared dialog and personal repository source

The suggestion panel is now one tab inside the shared **Blueprint** panel used by both new-project dialogs. Blank projects use **Generated | Templates**; GitHub projects use **Suggested | Templates | Generate**. The Templates tab is the same `StarterTemplatesSection` in both flows, and every compact **View all templates →** control routes to `setSelectedTab('templates')` (`apps/web/src/components/BlueprintPicker.tsx:209`, `:222`, `:371`; `apps/web/src/pages/ProjectGalleryPage.tsx:405`, `:676`).

Repository discovery uses caller-authorized metadata selections and a short-lived, single-use
selection code for project creation (`GitHubRepositorySelectionEndpoints.cs:12-89`). The retired
`/api/github/accounts` and `/api/github/repos` picker is not the current contract. Suggestion accepts
repository coordinates for metadata analysis; creation consumes the capability-bound selection code.

## Why this is separate from generation

Suggestion chooses from catalog blueprints and returns quickly from GitHub metadata. Generation uses a natural-language description and may produce an inline blueprint plus generated workflow YAML. Keeping the tabs separate makes the user choice clear: **Suggested** means "best matching starter template for this repo," **Templates** means manual catalog choice, and **Generate** means bespoke blueprint from a prompt (`apps/web/src/pages/ProjectGalleryPage.tsx:676`, `apps/web/src/components/BlueprintPicker.tsx:236`, `:279`).

## Fallback behavior

A parse failure, unavailable metadata, recoverable transport failure, or empty catalog can return
`fallback: true`, confidence `0`, a rationale, and a template when available. The UI exposes the
Templates tab instead of blocking creation. **Caller cancellation propagates**; it is not converted
into a successful fallback response (`GitHubRepoBlueprintSuggestionService.cs:89-115`).

## Source

| Concern | File |
|---|---|
| Suggested endpoint route and `400` blank-repository validation | `apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:53` |
| Suggest request/response wire fields | `apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs:111` |
| GitHub repo parsing, signal collection, catalog mapping, fallback | `apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:17` |
| DI registration | `apps/Agentweaver.Api/Program.cs:603` |
| Web client method | `apps/web/src/api/client.ts:186` |
| Frontend response type | `apps/web/src/api/types.ts:219` |
| Suggested tab rendering and fallback UI | `apps/web/src/components/BlueprintPicker.tsx:279` |
| Shared Blueprint panel, tab strip, and Templates routing | `apps/web/src/components/BlueprintPicker.tsx:371` |
| Create-from-GitHub tab wiring | `apps/web/src/pages/ProjectGalleryPage.tsx:676` |
| Capability-bound repository selections | `apps/Agentweaver.Api/Endpoints/GitHubRepositorySelectionEndpoints.cs` |

## See also

- [Repository blueprint suggestions — Reference](../reference/repo-blueprint-suggestions.md)
- [Repository blueprint suggestions — Experience](../experience/repo-blueprint-suggestions.md)
- [Project generation model settings](./project-generation-model-settings.md)
- [Projects experience](../experience/projects.md)
- [API reference](../reference/api.md#blueprints)

<details id="diagram-context-repo-blueprint-suggestions-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Repository suggestions are heuristic</td></tr>
<tr><td>takeaway</td><td>Anonymous metadata feeds deterministic matching; suggestions never invoke a model.</td></tr>
<tr><td>group-title-0</td><td>INPUT + CURRENT CREDENTIAL BOUNDARY</td></tr>
<tr><td>group-title-1</td><td>METADATA · MATCHING · OUTCOMES</td></tr>
<tr><td>Blueprint picker: Suggested</td><td>Blueprint picker: Suggested</td></tr>
<tr><td>Blueprint picker: Suggested</td><td>Repository string or GitHub URL</td></tr>
<tr><td>Blueprint picker: Suggested</td><td>Generate is a separate tab and model path</td></tr>
<tr><td>Blueprint picker: Suggested</td><td>BlueprintPicker.tsx:487-517</td></tr>
<tr><td>GitHub metadata requests</td><td>GitHub metadata requests</td></tr>
<tr><td>GitHub metadata requests</td><td>Repository · languages · root contents</td></tr>
<tr><td>GitHub metadata requests</td><td>No bearer when boundary returns null</td></tr>
<tr><td>GitHub metadata requests</td><td>SuggestionService.cs:55-68</td></tr>
<tr><td>Suggestion service</td><td>Suggestion service</td></tr>
<tr><td>Suggestion service</td><td>Parse owner / repository</td></tr>
<tr><td>Suggestion service</td><td>Invalid input returns fallback response</td></tr>
<tr><td>Suggestion service</td><td>SuggestionService.cs:40-48</td></tr>
<tr><td>Deterministic catalog matching</td><td>Deterministic catalog matching</td></tr>
<tr><td>Deterministic catalog matching</td><td>Signals + ordered heuristic rules</td></tr>
<tr><td>Deterministic catalog matching</td><td>No model invocation or repository clone</td></tr>
<tr><td>Deterministic catalog matching</td><td>SuggestionService.cs:70-84</td></tr>
<tr><td>Ambient credential boundary</td><td>Ambient credential boundary</td></tr>
<tr><td>Ambient credential boundary</td><td>Current registration returns null</td></tr>
<tr><td>Ambient credential boundary</td><td>Not a forwarded caller GitHub token</td></tr>
<tr><td>Ambient credential boundary</td><td>EntraOnlyGitHubCredentialBoundary</td></tr>
<tr><td>Suggested blueprint response</td><td>Suggested blueprint response</td></tr>
<tr><td>Suggested blueprint response</td><td>Blueprint + rationale + confidence</td></tr>
<tr><td>Suggested blueprint response</td><td>Signals explain the recommendation</td></tr>
<tr><td>Suggested blueprint response</td><td>SuggestionService.cs:78-88</td></tr>
<tr><td>Recoverable failure</td><td>Recoverable failure</td></tr>
<tr><td>Recoverable failure</td><td>Unavailable metadata / transport</td></tr>
<tr><td>Recoverable failure</td><td>Fallback response offers Templates option</td></tr>
<tr><td>Recoverable failure</td><td>SuggestionService.cs:90-108</td></tr>
<tr><td>Caller cancellation</td><td>Caller cancellation</td></tr>
<tr><td>Caller cancellation</td><td>Propagate OperationCanceledException</td></tr>
<tr><td>Caller cancellation</td><td>Not converted into fallback suggestions</td></tr>
<tr><td>Caller cancellation</td><td>SuggestionService.cs:90-94</td></tr>
<tr><td>Blueprint picker: Suggested</td><td>suggest</td></tr>
<tr><td>Suggestion service</td><td>resolve</td></tr>
<tr><td>Ambient credential boundary</td><td>null token</td></tr>
<tr><td>GitHub metadata requests</td><td>signals</td></tr>
<tr><td>Deterministic catalog matching</td><td>recommend</td></tr>
<tr><td>Suggestion service</td><td>invalid</td></tr>
<tr><td>GitHub metadata requests</td><td>cancel</td></tr>
<tr><td>scope</td><td>Cancellation propagates; recoverable failures fall back. Current ambient token source is explicitly null.</td></tr>
<tr><td>groups</td><td>INPUT + CURRENT CREDENTIAL BOUNDARY; METADATA · MATCHING · OUTCOMES</td></tr>
</tbody></table>
</details>
