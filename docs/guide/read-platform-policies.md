# Read and validate Policies

Use the Policies page to read saved Platform Policy source, compare it with a
local draft, or validate a local Project Policy draft. The page does not save,
apply, preview, or report runtime enforcement.

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

An empty history means you have read access and no Platform Policy is saved
for that tenant.

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

## Validate a Project Policy draft

1. Open `/policies`.
2. Select **Project** in **Policy scope** and choose a Project.
3. Select a local JSON file under **Validation request**. The JSON object must
   contain only `artifact`. The artifact must contain `manifestBytes`, `files`,
   and `contentSha256`. Each file must contain `relativePath` and `bytes`.
4. Edit the imported manifest, file paths, and file content in the draft
   editor.
5. Select **Validate draft**. Review the result and any diagnostics.

The import checks the artifact hash and preserves the artifact bytes, including
the UTF-8 byte-order mark and line endings. A failed import leaves the current
draft unchanged. The draft stays in browser memory. No Project Policy head or
history request runs, and validation does not save or apply the draft.

The request file limit is 5 MiB. The artifact limit is 1 MiB, with the same
manifest and file limits described below.

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
