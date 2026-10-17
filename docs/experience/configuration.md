# Project configuration

Open **Configuration** from a project to edit its owner-defined JSON document.
The page also hosts [account connections](./connections.md) and
[Skills and marketplace](./skills.md).

## Read and save

1. Open `/projects/:projectId/settings`.
2. Read the displayed configuration revision.
3. Edit **Typed ProjectConfiguration JSON**.
4. Keep the complete document to preserve unrelated settings.
5. Select **Append configuration revision**.

The request includes the loaded revision as `expectedRevision`. The owner
validates the document and appends a revision. Existing accepted runs retain
their immutable selections.

Model references and provider IDs are opaque identifiers. Use IDs already in
the project or returned by owner APIs. Enter secret references, not credential
values.

## Reload

Select **Reload owner revision** to replace the editor with the current owner
document. This discards unsaved edits in the editor.

Skill assignment also updates project configuration. Save or reload edited JSON
before assigning a skill. The editor becomes read-only during an assignment
update, and configuration saving waits for that update.

## States and errors

| State | Result |
| --- | --- |
| Loading | The editor waits for the owner revision. |
| Invalid JSON or a nonobject value | The client displays an error without sending a write. |
| Stale `expectedRevision` | The owner returns a conflict. Reload, review your changes against the current document, and save again. |
| Invalid provider, model, or configuration | The owner's validation response remains visible. |
| Saved | The page displays the returned revision and replaces the editor with the accepted document. |

## Requests

| Step | Gateway request |
| --- | --- |
| Load or reload | `GET /api/v1/projects/{projectId}/configuration` |
| Append revision | `PUT /api/v1/projects/{projectId}/configuration` with `expectedRevision` and `configuration` |

Both requests go to Projects & Config with the current bearer and confirmed
tenant selector. See [Projects & Config](../architecture/projects-config.md) for
the document and selection rules, and [contracts](../reference/contracts.md) for
configuration contracts.
