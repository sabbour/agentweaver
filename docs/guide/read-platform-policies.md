# Read and validate Policies

Use the Policies page to read saved Platform Policy source, compare it with a
local draft, or download, reopen, and validate a local Platform or Project
Policy draft. The page does not save, apply, preview, or report runtime
enforcement.

## Before you begin

- Sign in through the Identity Broker.
- Know the tenant ID.
- For Platform Policy reads, have current Platform Policy read access.
- For Project Policy validation, have `writeProjects` as the Project Owner for
  the exact Project or as a TenantAdmin in the tenant. PlatformAdmin access
  alone does not grant Project Policy validation.

## Read saved source

1. Select **Platform Policies** in the navigation rail, or open `/policies`.
2. Select **Platform** in **Policy scope**.
3. Check the **Tenant ID** field. The page fills it from the confirmed
   authorization context when that value is available. If it is empty, enter
   the tenant ID and select **Retry**.
4. Read the revision list. The row marked **snapshot head** is the revision
   named by the saved snapshot.
5. Select **View** beside a revision to read its manifest and files. Select
   **Load more** to read older revisions.
6. To export the loaded revision without changing your local draft, select
   **Download saved revision**.

An empty history means you have read access and no Platform Policy is saved
for that tenant.

**Download saved revision** appears only in authorized, ready **Platform**
scope when a revision is loaded. It rereads that exact revision from the
Gateway, checks its content hash and metadata, and downloads
`platform-policy-revision-<revision>.json` as compact UTF-8 JSON with a
trailing newline up to 5 MiB. The file has the seven CLI export keys:
`scopeType`, `scopeId`, `policyId`, `revision`, `contentSha256`, `createdAt`,
and `artifact`. Your local draft stays unchanged, and no write request runs.
If the read returns `403`, the page hides saved source. If a read or hash check
fails, the page shows an export error separate from draft errors.

## Compare a Platform Policy draft with a saved revision

1. In **Platform** scope, select **View** beside the revision to compare.
2. Edit the draft, or select **Load viewed revision into draft** and then edit
   the copy.
3. Select **Compare draft**.
4. Choose a changed manifest or file from **Changed item**.
5. Select **Show invisible characters** to expose spaces, tabs, line endings,
   Unicode outside printable ASCII, and the byte-order mark. Review the
   **Saved** and **Draft** source blocks.

The comparison uses the revision selected with **View**. It does not select
the current snapshot head, copy source into the draft, or send a request.
Files match by exact relative path. The manifest appears first when it
changes. File changes then appear in path order as **Added**, **Removed**, or
**Changed**. A rename appears as one removal and one addition. File order
changes alone do not appear. An empty file differs from a missing file.

The same draft path, UTF-8, and byte limits apply to comparison. Invalid
drafts keep their content and show a fixed error. Comparison does not check
Policy syntax or enforcement. Edit the draft or reload the page, and select
**Compare draft** again to refresh the result.

## Download and reopen a local draft

The same controls are available in authorized, ready Platform and Project
scopes.

1. Open or edit a local draft.
2. Select **Download draft**. The page names a Platform file
   `platform-policy-draft.json` and a Project file
   `project-policy-draft.json`.
3. To reopen a draft, select the downloaded file under **Validation request**.
   A successful import replaces the local manifest and files. A failed import
   leaves the current draft, validation, and comparison unchanged.

The file is compact JSON with one `artifact` field. The artifact contains
`manifestBytes`, `files`, and `contentSha256`. Each file contains
`relativePath` and `bytes`. Reopening checks the artifact hash before it
replaces the editor. It preserves the UTF-8 byte-order mark, mixed line
endings, and final newline in the source bytes.

The request file limit is 5 MiB. The artifact limit is 1 MiB, with the same
manifest and file limits described below. A download requires a non-empty
manifest and stays within the request, artifact, path, file-count, and file
size limits. It preserves the current UTF-8 bytes and content hash.

Downloading creates a browser file only. It sends no request and does not save,
apply, activate, or change a Platform or Project Policy. If the download fails,
the draft stays in the editor. A pending download or import stops when you
edit, change scope, tenant, Project, access, route, or sign-in session, or start
a newer local-file operation. A successful reopen clears old validation and
comparison results but keeps the selected viewed Platform revision.

## Validate a Project Policy draft

1. Open `/policies`.
2. Select **Project** in **Policy scope** and choose a Project.
3. Select a local JSON file under **Validation request**, then edit the
   manifest, file paths, and file content in the draft editor.
4. Select **Validate draft**. Review the result and any diagnostics.

## Raise the Project Skill minimum trust score

After a Project Policy draft passes validation and contains a single supported
`policy/skills/data.json` file, the **Project Skill trust score** form opens.
The file must contain only `schemaVersion` (`1`), `minimumScore` (`0` to
`1000`), and `requireCompletedEvidence` (`true` or `false`), with no duplicate
keys or ambiguous number tokens. Unsupported files stay in the raw editor.

1. In **New minimum trust score**, enter a whole number greater than the
   current score and no greater than `1000`.
2. Select **Update draft**.

**Update draft** replaces only the score token in `policy/skills/data.json`,
preserves all other bytes and files, clears the validation result, and moves
focus to **Validate draft**. It sends no request, and the Platform minimum and
built-in Skill exemption stay unchanged. Select **Validate draft** to validate
the updated draft. Editing the draft, importing another file, or changing
access or context clears the form.

The draft stays in browser memory. No Project Policy head or history request
runs, and validation does not save or apply the draft.

## Edit and validate a Platform Policy draft

1. Select **View** beside the saved revision you want to use.
2. Select **Load viewed revision into draft**. The editor copies its manifest
   and files into a local draft.
3. Edit the manifest and file contents. Select **Add file** or **Remove file**
   to change the file list. You can also start with an empty draft.
4. Select **Validate draft**. Review the result and any diagnostics.

The editor keeps the source text and line endings when you load and validate a
revision without editing it. The draft stays in browser memory and clears when
the app reloads. Validation checks the draft; it does not save it or change
the active policy.

When a diagnostic path exactly matches one current draft file, select
**Go to draft file** to focus that file's content editor. The action does not
change the draft or send another request. Diagnostics without a path, with no
matching file, or with more than one matching file remain text.

The draft limit is 64 files, 65,536 bytes for the manifest, 262,144 bytes per
file, and 1,048,576 bytes across the manifest and file contents. The editor
measures UTF-8 bytes.

## Verify access

The navigation item appears after the Gateway allows the Platform Policy
history read. The `/policies` route runs the same check. The Gateway checks
Project access against the selected tenant and exact Project. If the Gateway
denies access, the page clears the current draft and blocks validation. Select
**Retry** after your access changes.

If Platform access expires, the page refreshes the unbound session, checks the
Platform identity and history again, and opens the exact revision selected
before the check. The page hides saved source and the comparison during the
check. After the source opens, select **Compare draft** again. A newer
snapshot head does not replace the selected revision.

If Project validation returns `401`, the page refreshes the unbound session,
checks the same tenant and exact Project, and retries the unchanged draft once.
If a Project request is aborted while that scope is still active, the page
rechecks access but does not resend the artifact. After an edit, the page
clears old diagnostics and never retries the old draft.

## Next step

For browser routes and source boundaries, see the
[Web client reference](../reference/web.md) and
[Web UI parity reference](../reference/web-ui-parity.md).
