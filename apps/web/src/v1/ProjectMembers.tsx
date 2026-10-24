/**
 * Project members for Settings > Access, on the role-assignment API
 * (`listProjectRoleAssignments`, `assignProjectRole`,
 * `revokeProjectRoleAssignment`). Reading needs TenantAdmin or the project
 * Owner. Adding and removing need TenantAdmin with admin scope, which the
 * authorization context reports as a tenant-wide "Change project settings"
 * permission; other callers see the list without change controls. Adding sends
 * exactly a principal ID and a role; removing sends the assignment revision that
 * was shown, so a concurrent change is rejected instead of overwritten.
 */
import { Badge, Button, Field, Input, Select, Text } from '@fluentui/react-components';
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { AppDialog } from '../components/ui/AppDialog';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type { ProjectRole, ProjectRoleAssignment, ProjectRoleAssignmentList } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';

const ROLE_LABELS: Record<ProjectRole, string> = {
  owner: 'Owner',
  contributor: 'Contributor',
  viewer: 'Viewer',
  orchestrator: 'Orchestrator',
};

/** Plain text for role-assignment failures the user can act on. */
function changeError(reason: unknown): string {
  // Map known service answers to plain advice. Check specific error codes before
  // the generic 403, because a more exact reason is more useful to the user.
  if (reason instanceof GatewayError) {
    // 503 means the member store cannot be reached or is not configured.
    if (reason.status === 503) return 'Member changes are not available right now. Try again later.';
    if (reason.code === 'email_principal_not_supported')
      return "Email addresses cannot identify project members. Use the member's Agentweaver sign-in subject ID.";
    if (reason.code === 'principal_not_found') return 'No user or service account has this ID. Check the ID and try again.';
    if (reason.code === 'invalid_principal_id') return 'Enter a valid user or service account ID.';
    if (reason.code === 'invalid_project_role') return 'Pick one of the listed roles.';
    if (reason.code === 'not_found') return 'This project or member no longer exists. Reload the list.';
    if (reason.code === 'role_assignment_exists') return 'This member already has that role.';
    if (reason.code === 'last_project_owner') return 'A project needs at least one owner. Add another owner first.';
    if (reason.code === 'revision_conflict') return 'Someone else changed this member. Reload and try again.';
    if (reason.code === 'project_archived') return 'Restore the project before you change its members.';
    // Check the specific codes above first. A plain 403 means the caller lacks
    // the tenant administrator role that member changes need.
    if (reason.status === 403) return 'Only a tenant administrator can change project members.';
  }
  return errorMessage(reason);
}

/** Lists project members and, for tenant administrators, adds and removes them. */
export function ProjectMembersPanel({ projectId, canManage }: { projectId: string; canManage: boolean }) {
  const { apiCall } = useAuth();
  const [list, setList] = useState<ProjectRoleAssignmentList | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [principalId, setPrincipalId] = useState('');
  const [role, setRole] = useState<ProjectRole>('contributor');
  const [busy, setBusy] = useState(false);
  const [changeMessage, setChangeMessage] = useState<string | null>(null);
  // The assignment waiting for confirmation in the remove dialog. Null closes the dialog.
  const [removing, setRemoving] = useState<ProjectRoleAssignment | null>(null);

  /** Reads the member list. Used on first render and after every change. */
  const load = useCallback(async () => {
    setLoading(true);
    try {
      setList(await apiCall((token, tenant) => gatewayClient.listProjectRoleAssignments(token, projectId, tenant), null));
      setLoadError(null);
    } catch (reason) {
      // Drop the old list so the panel shows the error (for example "not
      // allowed" or "not available") instead of members that may be stale.
      setList(null);
      setLoadError(reason);
    } finally {
      setLoading(false);
    }
  }, [apiCall, projectId]);

  useEffect(() => {
    // Start the load in a timer instead of directly in the effect, so state is
    // not set synchronously during the effect. Clearing the timer on cleanup
    // skips the load if the panel unmounts or the project changes first.
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  /** Assigns the chosen role to the typed member ID, then reloads the list. */
  const add = async (event: FormEvent) => {
    event.preventDefault();
    const subject = principalId.trim();
    // Enter can submit even when the button is disabled, so check again here.
    if (!subject) return;
    setBusy(true);
    setChangeMessage(null);
    try {
      await apiCall((token, tenant) => gatewayClient.assignProjectRole(token, projectId, { principalId: subject, role }, tenant), null);
      // Clear the input only on success, so a failed add keeps the typed ID.
      setPrincipalId('');
      // Reload from the server instead of adding the row locally, so the table
      // shows the server's assignment ID, revision, and created time.
      await load();
    } catch (reason) {
      setChangeMessage(changeError(reason));
    } finally {
      setBusy(false);
    }
  };

  /** Revokes one role assignment after the user confirms, then reloads the list. */
  const remove = async (assignment: ProjectRoleAssignment) => {
    setBusy(true);
    setChangeMessage(null);
    try {
      // Send the revision the user saw. The server rejects the revoke if the
      // assignment changed since then.
      await apiCall((token, tenant) => gatewayClient.revokeProjectRoleAssignment(
        token, projectId, assignment.assignmentId, assignment.revision, tenant,
      ), null);
      setRemoving(null);
      await load();
    } catch (reason) {
      // Close the dialog on failure too, so the error message above the list is visible.
      setRemoving(null);
      setChangeMessage(changeError(reason));
    } finally {
      setBusy(false);
    }
  };

  // Pick one panel body. The order matters:
  // 1. Show the spinner only on the first load. Reloads after a change keep the
  //    current table on screen so it does not flicker.
  // 2. 403: the caller may not read members. Explain who can, not a raw error.
  // 3. 404, 405, or 501: the service does not offer this API yet. Say so plainly.
  // 4. Any other error: show the error notice with its code.
  // 5. Otherwise show the table and, for administrators, the add form.
  let body;
  if (loading && !list) body = <Loading label="Loading members" />;
  else if (loadError instanceof GatewayError && loadError.status === 403)
    body = <Text>Only a tenant administrator or a project owner can see the member list.</Text>;
  // 404 means the project does not exist or the caller cannot see it.
  else if (loadError instanceof GatewayError && loadError.status === 404)
    body = <Text>This project was not found. It may have been deleted.</Text>;
  else if (loadError != null)
    body = <ErrorNotice code={errorCode(loadError)}>{errorMessage(loadError)}</ErrorNotice>;
  else if (list) body = (
    <>
      {list.assignments.length === 0 ? <Text>This project has no members.</Text> : (
        <div className="v1-table-wrap">
          <table className="v1-table">
            <caption>Project members</caption>
            <thead>
              <tr>
                <th scope="col">Member</th>
                <th scope="col">Role</th>
                <th scope="col">Added</th>
                {canManage && <th scope="col"><span className="v1-visually-hidden">Actions</span></th>}
              </tr>
            </thead>
            <tbody>
              {list.assignments.map((assignment) => (
                <tr key={assignment.assignmentId}>
                  <th scope="row"><code>{assignment.principalId}</code></th>
                  <td><Badge appearance="outline" size="small">{ROLE_LABELS[assignment.role] ?? assignment.role}</Badge></td>
                  <td>{new Date(assignment.createdAt).toLocaleDateString()}</td>
                  {canManage && (
                    <td>
                      <Button appearance="subtle" size="small" disabled={busy} onClick={() => setRemoving(assignment)}>Remove</Button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {canManage && (
        <form className="v1-form v1-inline-form" onSubmit={(event) => void add(event)}>
          <Field label="Member ID" hint="Use the member's Agentweaver sign-in subject ID, not an email address.">
            <Input value={principalId} onChange={(_, data) => setPrincipalId(data.value)} />
          </Field>
          <Field label="Role">
            <Select value={role} onChange={(_, data) => setRole(data.value as ProjectRole)}>
              {(Object.keys(ROLE_LABELS) as ProjectRole[]).map((key) => <option key={key} value={key}>{ROLE_LABELS[key]}</option>)}
            </Select>
          </Field>
          <Button appearance="primary" type="submit" disabled={busy || !principalId.trim()}>{busy ? 'Saving…' : 'Add member'}</Button>
        </form>
      )}
    </>
  );

  return (
    <PageSection title="Members" description={canManage ? 'People who can work in this project, and their roles.' : 'People who can work in this project. Ask a tenant administrator to change members.'}>
      {changeMessage && <ErrorNotice>{changeMessage}</ErrorNotice>}
      {body}
      <AppDialog
        open={removing !== null}
        // Closing the dialog (Escape, outside click, or Cancel) forgets the pending removal.
        onOpenChange={(open) => { if (!open) setRemoving(null); }}
        title="Remove member"
        primaryAction={removing ? { label: 'Remove', onClick: () => void remove(removing), disabled: busy, loading: busy } : undefined}
      >
        {removing && <Text>Remove {ROLE_LABELS[removing.role]} role from <code>{removing.principalId}</code>? They lose that access right away.</Text>}
      </AppDialog>
    </PageSection>
  );
}
