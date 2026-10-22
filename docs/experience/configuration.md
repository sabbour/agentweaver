# Project configuration

Use **Project settings** to read and save the project configuration. Each save
adds a new configuration revision. The same page also has
[account connections](./connections.md). Skills have their own
[Skills page](./skills.md).

## Prerequisites

- Sign in through the Identity Broker.
- Open the project whose configuration you need to change.
- Have current owner-granted permission to update that project's configuration.

## Read and save configuration

1. Open the project, then select **Settings** > **Project settings**. The
   address is `/projects/:projectId/settings?section=<key>`.
2. Choose a section in the settings rail.
3. Edit the fields.
4. Select **Save changes**.

| Section | What you can do |
| --- | --- |
| **General** | Rename the project. Read its ID, state, and created and updated times. |
| **Access** | Read your permissions in this project. Manage members when the service allows it. See [Manage members](#manage-members). |
| **Repository** | Set the repository owner and name, and the **Sign-in method**: **Not set**, **GitHub App**, or **Stored secret**. |
| **Models** | Set **Model defaults** and **Models by task**. |
| **Platform components** | Change the implementations the project chose for each platform component. Enter one implementation ID, or a comma-separated list in priority order. If the project has no choices, platform defaults apply. |
| **Run limits** | Set model turns, tool calls, child sessions, child sessions at once, run time in seconds, and prompt tokens. Leave a field empty to use the platform limit. |
| **Connections** | Connect the GitHub Repo App, GitHub Copilot, and remote MCP servers. See [connections](./connections.md). |
| **History** | Enter a **Configuration revision** number and select **View revision** to read an earlier revision. History is read-only. |
| **Danger zone** | Archive or restore the project. Deleting a project is not available in this version. |
| **Advanced** | Edit the full **Configuration JSON**. Use it only when a section has no field for the setting. |

**Repository**, **Models**, **Platform components**, **Run limits**, and
**Advanced** share one form. The form shows the current revision, **Save
changes**, and **Reload**. The app submits the revision it loaded as
`expectedRevision`. The owner validates the document and adds a revision.

**Expected result:** The page shows **Configuration revision N saved. Runs that
already started keep their settings.**

Model references and provider IDs are opaque identifiers. Use IDs already in
the project or returned by owner APIs. Enter secret references, not credential
values. The form doesn't provide a provider or model catalog picker. For
provider selection and configuration guidance, see the
[provider configuration guide](../guide/configure-providers.md).

### Change the default model

In **Models**, a model credential belongs to one model. When you change
**Default model reference**, the form clears the saved credential and shows
**Choose a credential for this model.** **Save changes** stays disabled until
you enter a matching **Model credential ID** and **Model credential version**.
To keep the saved credential, change the model back. Hosted Copilot models
don't use a credential.

### Manage members

**Access** shows **Your permissions** and a **Members** panel. The panel lists
project members when the service offers the member list. A tenant
administrator or a project owner can read it.

A tenant administrator also sees **Add member** and **Remove**. To add a
member, enter the **Member ID** (the member's sign-in subject ID), choose a
**Role**, and select **Add member**. Roles are **Owner**, **Contributor**,
**Viewer**, and **Orchestrator**. A project needs at least one owner.

- If the service isn't ready for role changes, the panel shows **Role changes
  aren't available yet.**
- If this version of the service doesn't offer the member list, the panel says
  so, and **Access** shows only your permissions.
- Restore an archived project before you change its members.

## Reload the owner's revision

Select **Reload** to replace the form with the current owner document. This
discards unsaved edits.

If you edit **Configuration JSON** in **Advanced**, save or reload before you
use the other sections.

Skill assignment also updates project configuration. Save or reload your
changes before you assign a skill.

## Understand configuration states and errors

| State | Result |
| --- | --- |
| Loading | The form waits for the owner revision. |
| Invalid JSON or a nonobject value in **Advanced** | The app displays an error and does not send a write. |
| Stale `expectedRevision` | The owner returns a conflict. Reload, review your changes against the current document, and save again. |
| Invalid provider, model, or configuration | The owner's validation response remains visible. |
| Saved | The app displays the returned revision and accepted document. |
| Unknown `section` value | The page opens **General**. |

Project configuration is separate from web-host runtime settings. For runtime
settings, see the [runtime configuration reference](../reference/runtime-configuration.md).

## Requests

| Operation | Gateway request |
| --- | --- |
| Load or reload | `GET /api/v1/projects/{projectId}/configuration` |
| Save changes | `PUT /api/v1/projects/{projectId}/configuration` with `expectedRevision` and `configuration` |

Both requests go to Projects & Config with the current bearer token. The app
includes the tenant selector only when its current authorization context
confirms one. The Projects & Config implementation is a source candidate; these
paths are not evidence of a deployed API. See
[Projects & Config](../architecture/projects-config.md) for document and
selection rules, and [contracts](../reference/contracts.md) for configuration
contracts.
