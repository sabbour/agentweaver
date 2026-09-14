# Repository blueprint suggestions — Reference

Terse reference for the API that powers the **Suggested** tab in **Create project from GitHub**.

## Route

| Method & path | Body | Returns | Notes |
|---|---|---|---|
| `POST /api/blueprints/suggest` | `SuggestBlueprintRequest` | `SuggestBlueprintResponse` | Authenticated endpoint. Recommends a catalog blueprint by analyzing a GitHub repository. |

Source: `apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs:53`.

## Request DTO

`SuggestBlueprintRequest` is defined in `apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs:111`.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `repository` | string | yes | GitHub repository as `owner/repo`, a GitHub URL, or a value ending in `.git`. Blank values return `400`. |

## Response DTO

`SuggestBlueprintResponse` is defined in `apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs:116`; the web type mirrors it at `apps/web/src/api/types.ts:219`.

| Field | Type | Meaning |
|---|---|---|
| `recommended_blueprint` | `BlueprintDto` \| null | The catalog blueprint to use. On fallback, this is the first catalog blueprint when one exists. |
| `rationale` | string \| null | Human-readable reason for the recommendation or fallback. |
| `confidence` | number | Heuristic confidence from `0` to `1`. Fallback responses use `0`. |
| `signals` | string[] | Displayable evidence: description, topics, languages, root files, and issues-enabled when present. |
| `fallback` | boolean | `true` when repository analysis was unavailable or invalid and the UI should fall back to Templates. |

The embedded `BlueprintDto` includes `id`, `name`, `description`, `roster`, `workflow`, `workflows`, `review_policy`, `sandbox_profile`, and optional `bespoke_roles` (`BlueprintDtos.cs:11`).

## Status codes

| Code | When |
|---|---|
| `200 OK` | Suggestion returned, including graceful fallback responses. |
| `400 Bad Request` | `repository` is blank (`BlueprintEndpoints.cs:59`). |
| `401 Unauthorized` | Caller is not authenticated by the API auth middleware. |

GitHub `404`, rate-limit failures, network failures, or timeouts do not surface as endpoint errors; the service logs and returns `fallback: true` (`GitHubRepoBlueprintSuggestionService.cs:85`).

## Signals used

`GitHubRepoBlueprintSuggestionService` reads three GitHub REST resources with the caller's GitHub token scope (`GitHubRepoBlueprintSuggestionService.cs:48`, `:51`, `:58`, `:62`):

| GitHub signal | How it is used |
|---|---|
| Repository metadata | Name, description and topics contribute substring-mapping text; `has_issues` contributes display signal, not a keyword match. Rules run in order without an LLM. |
| Languages | Top languages are displayed and any non-Markdown language marks the repo as code. |
| Root contents | Up to eight root file names are displayed and included in mapping text. |

## Mapping rules

| Signal match | Recommended blueprint id | Confidence |
|---|---|---|
| `agent`, `llm`, `copilot`, `prompt`, `rag`, `ai-`, `openai`, `semantic-kernel`, or `langchain` | `blueprint-ai-agent-engineering` | `0.86` |
| `docs`, `documentation`, `blog`, `content`, `book`, `site`, or `website`, and no code languages | `blueprint-content-authoring` | `0.78` |
| `prd`, `roadmap`, `product`, `prototype`, `ux`, or `design`, and no code languages | `blueprint-product-management` | `0.74` |
| `frontend`, `backend`, `api`, `service`, `app`, `kubernetes`, `terraform`, `docker`, or any non-Markdown language | `blueprint-software-development` | `0.82` |
| Anything else | `blueprint-software-development` | `0.58` with signals, `0.35` without signals |

Source: `GitHubRepoBlueprintSuggestionService.cs:149`.

## Fallback

Fallback returns:

```json
{
  "recommended_blueprint": { "id": "..." },
  "rationale": "Repository analysis was unavailable; choose a template instead.",
  "confidence": 0,
  "signals": [],
  "fallback": true
}
```

The selected fallback blueprint is the first predefined catalog entry when available (`GitHubRepoBlueprintSuggestionService.cs:93`). The UI treats `fallback: true`, a missing recommendation, or a client error the same way: show a warning and offer **View all templates →**, which switches to the shared Templates tab (`apps/web/src/components/BlueprintPicker.tsx:323`, `:371`).

## Related repository picker

Suggestion input (`owner/repo` or supported URL) is heuristic input, not cloning/project-creation authority. The picker browses the caller's Repo App capability and submits a short-lived repository selection code:

| Method | Route | Contract |
|---|---|---|
| GET | `/api/github/repository-selections` | Safe installation/repository browse metadata from the live capability. |
| POST | `/api/github/repository-selections` | Verify `repository_full_name`; mint caller-bound, expiring, single-use code. |
| POST | `/api/projects` | Consume `repository_selection_code`; resolve clone metadata/credentials server-side. |

## See also

- [Repository blueprint suggestions — Deep Dive](../deep-dive/repo-blueprint-suggestions.md)
- [Repository blueprint suggestions — Experience](../experience/repo-blueprint-suggestions.md)
- [Project generation model settings](./project-generation-model-settings.md)
- [API reference](./api.md#blueprints)

HTTP failures, service timeouts and unavailable repository metadata return the template fallback; caller-requested cancellation propagates.
