# Read Platform Policies

Use the Platform Policies page to read the saved platform policy and its
revision history. The page shows saved source. It does not report runtime
enforcement.

## Before you begin

- Sign in through the Identity Broker.
- Know the tenant ID for the policy you need to read.
- Your current tenant access must allow Platform Policy reads. The Gateway
  checks this access when the page loads.

## Read a saved policy

1. Select **Platform Policies** in the navigation rail, or open `/policies`.
2. Check the **Tenant ID** field. The page fills it from the confirmed
   authorization context when that value is available. If it is empty, enter
   the tenant ID and select **Retry**.
3. Read the revision list. The row marked **snapshot head** is the revision
   named by the saved snapshot.
4. Select **View** beside a revision to read its manifest and files. Select
   **Load more** to read older revisions.

The page displays source as text. It does not let you edit, approve, or save a
policy.

An empty history means you have read access and no Platform Policy is saved
for that tenant.

## Verify access

The navigation item appears after the Gateway allows the Platform Policy
history read. The `/policies` route runs the same check. If the Gateway denies
access, the page hides policy data and shows a permission message. Select
**Retry** after your tenant access changes.

## Next step

For browser routes and source boundaries, see the
[Web client reference](../reference/web.md) and
[Web UI parity reference](../reference/web-ui-parity.md).
