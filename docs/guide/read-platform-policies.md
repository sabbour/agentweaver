# Read and validate Platform Policies

Use the Platform Policies page to read saved source and revisions, edit a raw
manifest and file draft, and validate that draft. The page does not report
runtime enforcement.

## Before you begin

- Sign in through the Identity Broker.
- Know the tenant ID for the policy you need to read or validate.
- Your current tenant access must allow Platform Policy reads. The Gateway
  checks this access when the page loads and when you validate a draft.

## Read saved source

1. Select **Platform Policies** in the navigation rail, or open `/policies`.
2. Check the **Tenant ID** field. The page fills it from the confirmed
   authorization context when that value is available. If it is empty, enter
   the tenant ID and select **Retry**.
3. Read the revision list. The row marked **snapshot head** is the revision
   named by the saved snapshot.
4. Select **View** beside a revision to read its manifest and files. Select
   **Load more** to read older revisions.

An empty history means you have read access and no Platform Policy is saved
for that tenant.

## Edit and validate a draft

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
history read. The `/policies` route runs the same check. The Gateway also
checks access when you validate a draft. If the Gateway denies access, the
page hides policy data and the navigation item. Select **Retry** after your
tenant access changes. If access expires during validation, the page checks
access again; select **Validate draft** after the check completes.

## Next step

For browser routes and source boundaries, see the
[Web client reference](../reference/web.md) and
[Web UI parity reference](../reference/web-ui-parity.md).
