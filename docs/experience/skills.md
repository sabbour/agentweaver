# Skills and marketplace

Use the project's **Skills** page to import validated skill content and assign
a skill revision to agents in the project's current cast. Open it from the
project navigation, or go to `/projects/:projectId/skills`.
An active marketplace source means that the source is configured; it does not
confirm GitHub availability.

The page has a stat strip (**Assigned**, **Enabled**, **Marketplace sources**,
and **Agents**), a toolbar with **Import skill** and **Browse marketplaces**,
and two tabs: **Catalog** and **Assignments**.

The Projects & Config implementation is an unpublished .NET service
candidate. Its routes and current browser controls describe source behavior,
not proof of deployment.

## Prerequisites

- Sign in and open the project's **Skills** page.
- Have current project Owner or tenant administrator authority for the
  marketplace and inventory operations.
- For marketplace imports, provide a public GitHub repository.

## Add and browse a public source

1. Select **Browse marketplaces**. Under **Add a public source**, enter a public GitHub repository in
   `owner/repository` form. You can also provide a source name, requested ref
   (defaults to `main`), and repository subpath.
2. Select **Add source**.
3. Under **Browse a source**, select the source, enter an optional search, and
   select **Browse skills**.
4. Select **Preview source skill** for a candidate.
5. Review the validated skill name, resource count, content digest, and total
   bytes.
6. Select **Import selected source skill**.

Browse results pin the owner-reported source revision and resolved commit.
Preview and import use the candidate path and content at that commit. The owner
reads the repository files; the browser does not submit marketplace file
bytes.

You can update a source using its current revision. **Remove source** creates
an owner tombstone and preserves source history; it does not delete the GitHub
repository.

**Expected result:** The owner returns imported skill metadata, including its
revision and content digest. A preview alone does not save an imported skill.

## Upload a local skill

1. Select **Import skill**, and then choose a folder or files that contain
   exactly one `SKILL.md` file and its text resources.
2. Select **Validate skill files**.
3. Inspect the Skills owner's validation result.
4. Select **Import skill to project**.

The local upload and marketplace import are separate paths. Preview validates
the candidate without importing it.

To prepare candidates from local content, send one of these inputs to
`POST /api/v1/skills/package`:

- **Files or a folder**: each file's relative path and base64 content. Every
  `SKILL.md` marks one skill. Files under a nested `SKILL.md` belong only to
  that nested skill.
- **A ZIP archive**: the archive as base64. The owner applies the same path,
  file-count, and size rules while it reads the archive.
- **A new skill**: a name, an optional description, and instructions. The owner
  writes the `SKILL.md` frontmatter for you.

The response lists each skill with its candidate, preview, and digest, or a
validation error. It also lists binary or oversized files that the owner
skipped. An upload can contain up to 1,024 files and 2 MiB. To save a skill,
send its candidate and digest to the import route.

**Expected result:** The owner returns the validated skill name, resource
count, content digest, and byte count. After import, the owner returns the
saved skill revision. Close the dialog to assign the skill. The page opens
the **Assignments** tab after an import.

## Assign an imported skill

1. Save or reload any unsaved configuration changes.
2. Open the **Assignments** tab.
3. Select agents from the project's current cast.
4. Submit the assignment for the imported skill revision and digest.
5. Read the saved configuration revision.

The assignment includes the current project configuration revision and saves
the assignment in a new configuration revision. It does not report that a
running agent loaded the skill.

**Expected result:** The page displays the configuration revision returned by
the owner with the skill assignment.

## Remove a skill from use

Imported revisions are immutable. To stop using a skill, remove it from the
agents' assignment, and then revoke the revision with a reason. A revoked
revision stays in the inventory for audit, and the runtime does not load it.

## Inspect the inventory

On the **Catalog** tab, **Imported skill inventory** shows owner-provided revision metadata and current
project assignments. Select the inventory refresh and paging controls to read
more results. The owner defaults to 25 records per page and accepts page sizes
from 1 to 50. The cursor does not provide a snapshot across pages.

**Expected result:** The inventory page shows the revision and assignment
metadata returned by the owner. It does not show whether a runtime loaded a
skill.

## Understand states and errors

- Browse, preview, import, and inventory each show their own loading state.
- If the app has no authorized Gateway actions, the section displays an
  unavailable message.
- A source, configuration, or digest conflict requires a fresh read and
  preview.
- Assignment is disabled while configuration is saving, another assignment is
  in progress, or the JSON editor has unsaved changes.
- Validation failures and owner error codes remain visible. A failed import is
  not displayed as a saved skill.
- Import retries use the same operation's idempotency key.

## Requests

All paths below are versioned Gateway routes owned by Projects & Config in
source.

| Action | Request |
| --- | --- |
| List or add sources | `GET` or `POST /api/v1/projects/{projectId}/skill-marketplaces/sources` |
| Update source | `PUT /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}` |
| Tombstone source | `DELETE /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}?expectedRevision=...` |
| Browse | `GET /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/browse` |
| Preview selected content | `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview` |
| Import selected content | `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import` |
| Preview local content | `POST /api/v1/skills/preview` |
| Package uploads or a new skill | `POST /api/v1/skills/package` |
| Import local content | `POST /api/v1/projects/{projectId}/skills/import` |
| Read inventory | `GET /api/v1/projects/{projectId}/skills` |
| Assign a skill | `PUT /api/v1/projects/{projectId}/skills/{skillId}/assignment` |
| Revoke a revision | `POST /api/v1/projects/{projectId}/skills/{skillId}/revisions/{revision}/revoke` |

See [Skill marketplace sources](../architecture/skill-marketplace.md) for
source, content, and size constraints, and the
[Gateway guide](../guide/gateway.md) for body limits and forwarding.
