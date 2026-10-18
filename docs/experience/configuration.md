# Project configuration

Use this page to read and append revisions to the owner-defined project
configuration document. The same settings page also hosts
[account connections](./connections.md) and
[Skills and marketplace](./skills.md).

## Prerequisites

- Sign in through the Identity Broker.
- Open the project whose configuration you need to change.
- Have current owner-granted permission to update that project's configuration.

## Read and save configuration

1. Open `/projects/:projectId/settings`.
2. Read the displayed configuration revision.
3. Edit **Typed ProjectConfiguration JSON**.
4. Keep the complete JSON document so you do not remove settings you did not
   intend to change.
5. Select **Append configuration revision**.

The app submits the revision it loaded as `expectedRevision`. The owner
validates the document and appends a revision. Existing accepted runs keep
their immutable selections.

**Expected result:** The page displays the revision returned by the owner and
replaces the editor contents with the accepted document.

Model references and provider IDs are opaque identifiers. Use IDs already in
the project or returned by owner APIs. Enter secret references, not credential
values. The editor does not provide a provider or model catalog picker. For
provider selection and configuration guidance, see the planned
[provider configuration guide](../guide/configure-providers.md).

## Reload the owner's revision

Select **Reload owner revision** to replace the editor with the current owner
document. This discards unsaved edits.

Skill assignment also updates project configuration. Save or reload edited
JSON before assigning a skill. The editor becomes read-only while an
assignment update is in progress, and configuration saving waits for that
update.

## Understand configuration states and errors

| State | Result |
| --- | --- |
| Loading | The editor waits for the owner revision. |
| Invalid JSON or a nonobject value | The app displays an error and does not send a write. |
| Stale `expectedRevision` | The owner returns a conflict. Reload, review your changes against the current document, and save again. |
| Invalid provider, model, or configuration | The owner's validation response remains visible. |
| Saved | The app displays the returned revision and accepted document. |

Project configuration is separate from web-host runtime settings. For runtime
settings, see the planned [runtime configuration reference](../reference/runtime-configuration.md).

## Requests

| Operation | Gateway request |
| --- | --- |
| Load or reload | `GET /api/v1/projects/{projectId}/configuration` |
| Append a revision | `PUT /api/v1/projects/{projectId}/configuration` with `expectedRevision` and `configuration` |

Both requests go to Projects & Config with the current bearer token. The app
includes the tenant selector only when its current authorization context
confirms one. The Projects & Config implementation is a source candidate; these
paths are not evidence of a deployed API. See
[Projects & Config](../architecture/projects-config.md) for document and
selection rules, and [contracts](../reference/contracts.md) for configuration
contracts.
