# Skills and marketplace

Use the project's **Skills** page to import validated skill content and assign
a skill revision to agents in the project's current cast. Open it from the
project navigation, or go to `/projects/:projectId/skills`.
An active marketplace source means that the source is configured; it does not
confirm GitHub availability.

The page has a stat strip (**Assigned**, **Enabled**, **Marketplace sources**,
and **Agents**), a toolbar with **Add skill**, **Import skill**, and **Browse marketplaces**,
and two tabs: **Catalog** and **Assignments**.

The Projects & Config implementation is an unpublished .NET service
candidate. Its routes and current browser controls describe source behavior,
not proof of deployment.

## Prerequisites

- Sign in and open the project's **Skills** page.
- Have current project Owner or tenant administrator authority for the
  marketplace and inventory operations.
- For marketplace imports, provide a public GitHub repository.

## Add a suggested marketplace

1. Select **Browse marketplaces**. **Suggested marketplaces** lists the
   marketplaces the service offers for this project.
2. Select **Add** next to one. It is saved as a marketplace source, and you
   can browse it right away.

A suggestion that is already a source shows as added. If the list can't be
read, the dialog shows the error, and you can still add a source by hand.

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

1. Select **Import skill**, and then choose a skill folder, its files, or one
   ZIP file.
2. Select **Check skill files**. Each folder with a `SKILL.md` becomes one
   skill.
3. Choose a skill from the list, and read its preview or its validation error.
4. Select **Import skill to project**.

## Create a skill

1. Select **Add skill**.
2. Enter a name (lowercase letters, numbers, and hyphens), an optional
   description, and instructions.
3. Select **Create skill**. The page packages the fields into a skill,
   previews it, and imports it.

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
skipped. An upload can contain up to 1,024 files and 2 MiB. The encoded
request must stay under 3 MiB. The page rejects a larger upload before it
sends it. To save a skill, preview its candidate with
`POST /api/v1/skills/preview`, then send the candidate and digest to the
import route.

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

## Remove a skill

Imported revisions are immutable. To stop using a skill:

1. On the **Catalog** tab, select **Remove skill** on the skill.
2. Read the confirmation. It names the current revision that will be revoked.
3. Select **Remove skill**.

The page revokes that revision and reloads the inventory. A revoked revision
stays in the inventory for audit, and agents can no longer load it. If the
project still assigns the revision, change the assignment before you start new
runs. Revoking needs project Owner or tenant administrator authority. Without
it, the dialog shows a permission message.

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
| List suggested marketplaces | `GET /api/v1/projects/{projectId}/skill-marketplaces/definitions` |
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
