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
| **Models** | Set **Model defaults** and **Models by task** using the owner's current model and reasoning choices when available. Until the owner has a model capability catalog, entries are typed and verified when a run starts. |
| **Platform components** | Read the provider choices selected by the platform. Projects cannot override non-model platform components. |
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

Model references, provider IDs, and credential references are opaque
identifiers. Enter secret references, not credential values. The owner-backed
choices are a current, tenant- and project-scoped view for configuration; they
do not provision or validate upstream resources. A model reference is checked
against the active runtime binding when a run starts. Accepted run selections
are immutable, so a settings change does not change a run that has already
started.

The project service does not yet have a model capability catalog, so eligible
model choices are reported as unavailable. **Models** says so explicitly, lists
the saved model references as **not verified as eligible**, and keeps model
references and reasoning levels as typed entries that are verified only when a
run starts. It does not present saved references as eligible choices, and it
does not silently replace a saved model or reasoning effort. Current platform
choices do not make an upstream model available, provision credentials, or
guarantee that a run can start.

Only model configuration can be overridden at project level. Provider
selections for other platform components come from platform configuration and
are displayed read-only. Legacy project provider-override values remain in
stored configuration, are labeled as ignored, and do not affect new run
selections. Saves send empty provider-override fields, and the owner keeps the
stored legacy values unchanged. Any non-empty provider override, including an
unchanged copy of the stored value, is rejected. This behavior does not
migrate or delete existing values. For provider configuration guidance, see the
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
| Model choices unavailable | The form states that the project service has no model capability catalog, lists saved references as not verified, and keeps typed model reference entry; it does not substitute another choice. |
| Invalid JSON or a nonobject value in **Advanced** | The app displays an error and does not send a write. |
| Stale `expectedRevision` | The owner returns a conflict. Reload, review your changes against the current document, and save again. |
| Invalid provider, model, or configuration | The owner's validation response remains visible. |
| Non-empty provider overrides in a write | The owner rejects the write with `project_provider_override_unsupported`. The app sends empty provider-override fields, so edits to them in **Advanced** are not saved; stored legacy values are kept and ignored. |
| Saved | The app displays the returned revision and accepted document. |
| Unknown `section` value | The page opens **General**. |

Project configuration is separate from web-host runtime settings. For runtime
settings, see the [runtime configuration reference](../reference/runtime-configuration.md).

## Requests

| Operation | Gateway request |
| --- | --- |
| Load or reload | `GET /api/v1/projects/{projectId}/configuration` |
| Load model and platform provider choices | `GET /api/v1/projects/{projectId}/configuration/choices` |
| Save changes | `PUT /api/v1/projects/{projectId}/configuration` with `expectedRevision` and `configuration` |

These requests go to Projects & Config with the current bearer token. The
choices request uses the current project read authorization, accepts no query
parameters, and returns `Cache-Control: no-store`. The app includes the tenant
selector only when its current authorization context confirms one. The
Projects & Config implementation is a source candidate; these paths are not
evidence of a deployed API. See
[Projects & Config](../architecture/projects-config.md) for document and
selection rules, and [contracts](../reference/contracts.md) for configuration
contracts.
