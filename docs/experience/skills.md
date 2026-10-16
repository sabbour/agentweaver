# Skills and marketplace

The **Skills and marketplace** section is on the
[project configuration](./configuration.md) page. It imports versioned skill
content and assigns exact revisions to agents in the current cast.

## Browse and import a public skill

1. Under **Add a public source**, enter a source name, public GitHub repository
   in `owner/repository` form, requested ref, and optional repository subpath.
2. Add the source.
3. Select it under **Browse a source**, enter an optional search, and browse.
4. Select **Preview** for a candidate.
5. Review the returned name, description, digest, file count, and byte count.
6. Import the previewed skill.

Browsing pins the owner-reported source revision and resolved commit. Selected
imports send those values, the candidate path, and the preview digest. The owner
reads the files; the browser does not resubmit marketplace file bytes.

Source editing uses the current revision. Removing a source creates an owner
tombstone; it is not a request to delete the GitHub repository.

## Upload a local skill

1. Under **Upload a local skill**, choose a folder or files containing exactly
   one `SKILL.md`.
2. Preview the selected text resources.
3. Inspect the owner validation result.
4. Import the validated content.

The local upload and selected-marketplace import are separate paths. Preview
does not save an imported skill.

## Assign an imported skill

1. Save or reload any unsaved configuration changes.
2. Select agents from the project's current cast.
3. Submit the assignment for the imported skill revision and digest.
4. Read the saved configuration revision.

Assignment includes the current project configuration revision. It saves
configuration; it does not report that a running agent loaded the skill.

## Inspect inventory

**Imported skill inventory** shows stored revision metadata and current
assignments. Use the inventory refresh and paging controls to read more results.
The owner defaults inventory pages to 25 records and accepts sizes from 1 to 50.
The cursor is not a snapshot across pages.

## States and errors

- Browse, preview, import, and inventory each show their own loading state.
- No authorized actions means the section displays an unavailable message.
- A source, configuration, or digest conflict requires a fresh read and preview.
- Assignment is disabled during a configuration save, during another assignment,
  or while the JSON editor has unsaved changes.
- Validation failures and owner error codes remain visible. Failed imports are
  not displayed as saved skills.
- Import retries use the same operation's idempotency key.

## Requests

All paths below are Gateway routes owned by Projects & Config.

| Action | Request |
| --- | --- |
| List or add sources | `GET` or `POST /api/v1/projects/{projectId}/skill-marketplaces/sources` |
| Update source | `PUT /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}` |
| Tombstone source | `DELETE /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}?expectedRevision=...` |
| Browse | `GET /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/browse` |
| Preview selected content | `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview` |
| Import selected content | `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import` |
| Preview local content | `POST /api/v1/skills/preview` |
| Import local content | `POST /api/v1/projects/{projectId}/skills/import` |
| Read inventory | `GET /api/v1/projects/{projectId}/skills` |
| Assign | `PUT /api/v1/projects/{projectId}/skills/{skillId}/assignment` |

See [Skill marketplace sources](../architecture/skill-marketplace.md) for source,
content, and size constraints, and [the Gateway guide](../guide/gateway.md) for
body limits and forwarding.
