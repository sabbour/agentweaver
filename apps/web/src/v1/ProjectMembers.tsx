/**
 * Project members for Settings > Access, on the role-assignment API
 * (`listProjectRoleAssignments`, `assignProjectRole`,
 * `revokeProjectRoleAssignment`). Reading needs TenantAdmin or the project
 * Owner. Adding and removing need TenantAdmin with admin scope, which the
 * authorization context reports as a tenant-wide "Change project settings"
 * permission; other callers see the list without change controls. When the
 * service does not offer the list yet, the panel says so and Access shows only
 * the caller's own permissions.
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
    // 503 means the role-assignment service is not configured yet.
    if (reason.status === 503) return 'Role changes aren\'t available yet. An operator must finish setting up the service.';
    if (reason.code === 'role_assignment_exists') return 'This member already has that role.';
    if (reason.code === 'last_project_owner') return 'A project needs at least one owner. Add another owner first.';
    if (reason.code === 'revision_conflict') return 'Someone else changed this member. Reload and try again.';
    if (reason.code === 'project_archived') return 'Restore the project before you change its members.';
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
  const [removing, setRemoving] = useState<ProjectRoleAssignment | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setList(await apiCall((token, tenant) => gatewayClient.listProjectRoleAssignments(token, projectId, tenant), null));
      setLoadError(null);
    } catch (reason) {
      // Clear the old list so the panel shows the error state, not stale members.
      setList(null);
      setLoadError(reason);
    } finally {
      setLoading(false);
    }
  }, [apiCall, projectId]);

  useEffect(() => {
    // Start the first load on the next tick instead of inside the effect body.
    // This keeps state updates out of the effect itself, and the cleanup cancels
    // the load if the component unmounts or the project changes first.
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  const add = async (event: FormEvent) => {
    event.preventDefault();
    const subject = principalId.trim();
    if (!subject) return;
    setBusy(true);
    setChangeMessage(null);
    try {
      await apiCall((token, tenant) => gatewayClient.assignProjectRole(token, projectId, { principalId: subject, role }, tenant), null);
      setPrincipalId('');
      // Reload from the service instead of adding the row locally, so the list shows
      // the saved assignment with its real ID and revision.
      await load();
    } catch (reason) {
      setChangeMessage(changeError(reason));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (assignment: ProjectRoleAssignment) => {
    setBusy(true);
    setChangeMessage(null);
    try {
      // Send the assignment revision we displayed. If someone changed it since,
      // the service rejects the removal instead of removing a different version.
      await apiCall((token, tenant) => gatewayClient.revokeProjectRoleAssignment(
        token, projectId, assignment.assignmentId, assignment.revision, tenant,
      ), null);
      setRemoving(null);
      await load();
    } catch (reason) {
      // Close the confirm dialog either way; the error shows above the list.
      setRemoving(null);
      setChangeMessage(changeError(reason));
    } finally {
      setBusy(false);
    }
  };

  // Pick one body to show. Order matters: first load, then known access and
  // availability errors with plain messages, then any other error, then the list.
  // While a reload runs, the current list stays on screen instead of a spinner.
  let body;
  if (loading && !list) body = <Loading label="Loading members" />;
  else if (loadError instanceof GatewayError && loadError.status === 403)
    body = <Text>Only a tenant administrator or a project owner can see the member list.</Text>;
  // These statuses mean the service does not offer the member list endpoint yet.
  else if (loadError instanceof GatewayError && (loadError.status === 404 || loadError.status === 405 || loadError.status === 501))
    body = <Text>The member list isn&apos;t available in this version of the service.</Text>;
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
          <Field label="Member ID" hint="The member's sign-in subject ID.">
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
        onOpenChange={(open) => { if (!open) setRemoving(null); }}
        title="Remove member"
        primaryAction={removing ? { label: 'Remove', onClick: () => void remove(removing), disabled: busy, loading: busy } : undefined}
      >
        {removing && <Text>Remove {ROLE_LABELS[removing.role]} role from <code>{removing.principalId}</code>? They lose that access right away.</Text>}
      </AppDialog>
    </PageSection>
  );
}
