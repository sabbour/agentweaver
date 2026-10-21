/**
 * Project settings sections that are not part of the shared model form: General
 * (rename), Access (the caller's permissions and project members), Repository and Run limits
 * fields, and Danger zone (archive and restore). Each section reads or writes
 * real Projects & Config fields only.
 */
import { Badge, Button, Field, Input, Select, Text } from '@fluentui/react-components';
import { useState, type FormEvent } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { AppDialog } from '../components/ui/AppDialog';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectConfiguration, ProjectSummary, SourceControlAuthMode } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';
import { ProjectMembersPanel } from './ProjectMembers';
import { PERMISSION_LABELS, permissionsForProject, useAuthorizationContext } from './projectData';

/** Renames the project with the project revision as the concurrency check. */
export function GeneralSection({ project, onUpdated }: { project: ProjectSummary; onUpdated: (next: ProjectSummary) => void }) {
  const { apiCall } = useAuth();
  const [name, setName] = useState(project.name);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const trimmed = name.trim();
  const save = async (event: FormEvent) => {
    event.preventDefault();
    // Skip the request when the name is empty or did not change.
    if (!trimmed || trimmed === project.name) return;
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      // Send the revision we loaded. If someone else changed the project since,
      // the service rejects the update instead of overwriting their change.
      // The state is sent unchanged because the update replaces name and state together.
      const updated = await apiCall((token, tenant) => gatewayClient.updateProject(
        token, project.projectId, { expectedRevision: project.revision, name: trimmed, state: project.state }, tenant,
      ), null);
      // The parent stores the new project, including its new revision, so the next save uses it.
      onUpdated(updated);
      setSaved(true);
    } catch (reason) {
      setError(reason);
    } finally {
      setBusy(false);
    }
  };
  return (
    <PageSection title="General" description="The project name appears in navigation and breadcrumbs.">
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      <form className="v1-form" onSubmit={(event) => void save(event)}>
        <Field label="Project name" validationState={saved ? 'success' : 'none'} validationMessage={saved ? 'Name saved.' : undefined}>
          <Input value={name} maxLength={120} onChange={(_, data) => { setName(data.value); setSaved(false); }} />
        </Field>
        <div className="v1-actions">
          <Button appearance="primary" type="submit" disabled={busy || !trimmed || trimmed === project.name}>
            {busy ? 'Saving…' : 'Rename project'}
          </Button>
        </div>
      </form>
      <dl className="v1-detail-list">
        <div><dt>Project ID</dt><dd>{project.projectId}</dd></div>
        <div><dt>State</dt><dd>{project.state === 'active' ? 'Active' : 'Archived'}</dd></div>
        <div><dt>Created</dt><dd>{new Date(project.createdAt).toLocaleString()}</dd></div>
        <div><dt>Updated</dt><dd>{new Date(project.updatedAt).toLocaleString()}</dd></div>
      </dl>
    </PageSection>
  );
}

/**
 * Shows what the signed-in user can do in this project, followed by the project
 * members panel. Member change controls appear only for tenant administrators.
 */
export function AccessSection({ projectId }: { projectId: string }) {
  const context = useAuthorizationContext();
  // Keep only the permissions that apply to this project: project, tenant-wide, or platform grants.
  const permissions = context.data ? permissionsForProject(context.data, projectId) : [];
  // The service reports a tenant-wide "Change project settings" permission only for
  // a tenant administrator with admin scope. That is also the only role that can
  // add or remove members, so it decides whether the member controls appear.
  // The service still checks every change; this flag only hides controls.
  const canManageMembers = permissions.some((item) => item.scope === 'tenant' && item.permission === 'writeProjects');
  return (
    <>
    <PageSection title="Access" description="What you can do in this project. Ask a tenant administrator to change access.">
      {context.error != null && <ErrorNotice code={errorCode(context.error)}>{errorMessage(context.error)}</ErrorNotice>}
      {context.loading && <Loading label="Loading access" />}
      {context.data && (permissions.length === 0
        ? <Text>You have no permissions in this project.</Text>
        : (
          <ul className="v1-permission-list" aria-label="Your permissions">
            {permissions.map((item) => (
              <li key={`${item.scope}-${item.permission}`}>
                {PERMISSION_LABELS[item.permission] ?? item.permission}{' '}
                <Badge appearance="outline" size="small">
                  {item.scope === 'project' ? 'This project' : item.scope === 'tenant' ? 'All tenant projects' : 'Platform'}
                </Badge>
              </li>
            ))}
          </ul>
        ))}
    </PageSection>
    {/* Show members only after permissions load, so the panel knows whether to show change controls. */}
    {context.data && <ProjectMembersPanel projectId={projectId} canManage={canManageMembers} />}
    </>
  );
}

/** Edits the repository the project works in and how agents authenticate to it. */
export function RepositoryFields({ configuration, onChange, readOnly }: {
  configuration: ProjectConfiguration;
  onChange: (next: ProjectConfiguration) => void;
  readOnly: boolean;
}) {
  // Older configurations may have no source control block. Use empty values so the inputs stay controlled.
  const sourceControl = configuration.sourceControl ?? {};
  const repository = sourceControl.repository ?? { owner: '', name: '' };
  const setRepository = (part: 'owner' | 'name', value: string) => {
    const next = { ...repository, [part]: value.trim() };
    // When both owner and name are empty, drop the repository instead of saving
    // an empty one. Other source control fields, such as the sign-in method, stay.
    onChange({
      ...configuration,
      sourceControl: { ...sourceControl, repository: next.owner || next.name ? next : undefined },
    });
  };
  return (
    <PageSection title="Repository" description="The GitHub repository agents read and change. Saved changes apply to future runs.">
      <div className="v1-inline-form">
        <Field label="Repository owner">
          <Input value={repository.owner} readOnly={readOnly} onChange={(_, data) => setRepository('owner', data.value)} />
        </Field>
        <Field label="Repository name">
          <Input value={repository.name} readOnly={readOnly} onChange={(_, data) => setRepository('name', data.value)} />
        </Field>
        <Field label="Sign-in method">
          <Select
            value={sourceControl.authMode ?? ''}
            disabled={readOnly}
            // "Not set" has an empty value. Save it as undefined so the field is left out.
            onChange={(_, data) => onChange({
              ...configuration,
              sourceControl: { ...sourceControl, authMode: (data.value || undefined) as SourceControlAuthMode | undefined },
            })}
          >
            <option value="">Not set</option>
            <option value="githubApp">GitHub App</option>
            <option value="secret">Stored secret</option>
          </Select>
        </Field>
      </div>
    </PageSection>
  );
}

const LIMITS: ReadonlyArray<{ key: keyof ProjectConfiguration['runLimits']; label: string }> = [
  { key: 'maxModelTurns', label: 'Model turns' },
  { key: 'maxToolCalls', label: 'Tool calls' },
  { key: 'maxChildren', label: 'Child sessions' },
  { key: 'maxConcurrentChildren', label: 'Child sessions at once' },
  { key: 'maxWallTimeSeconds', label: 'Run time in seconds' },
  { key: 'maxPromptTokens', label: 'Prompt tokens' },
];

/** Edits per-run limits. An empty field uses the platform limit. */
export function RunLimitsFields({ configuration, onChange, readOnly }: {
  configuration: ProjectConfiguration;
  onChange: (next: ProjectConfiguration) => void;
  readOnly: boolean;
}) {
  // Network restrictions are only summarized here. They are edited in the Advanced section.
  const egress = configuration.egressNarrowing ?? [];
  return (
    <PageSection title="Run limits" description="Each run stops at these limits. Leave a field empty to use the platform limit.">
      <div className="v1-inline-form">
        {LIMITS.map(({ key, label }) => (
          <Field key={key} label={label}>
            <Input
              type="number"
              min={1}
              value={configuration.runLimits[key]?.toString() ?? ''}
              placeholder="Platform limit"
              readOnly={readOnly}
              onChange={(_, data) => {
                // Only positive whole numbers are kept. Empty or invalid input saves
                // null, which means "use the platform limit".
                const parsed = Number.parseInt(data.value, 10);
                onChange({
                  ...configuration,
                  runLimits: { ...configuration.runLimits, [key]: Number.isFinite(parsed) && parsed > 0 ? parsed : null },
                });
              }}
            />
          </Field>
        ))}
      </div>
      <Text size={200}>
        {egress.length === 0
          ? 'Network access: platform rules apply. No project restrictions are set.'
          : `Network access: ${egress.length} project restriction${egress.length === 1 ? '' : 's'}. Edit them in Advanced.`}
      </Text>
    </PageSection>
  );
}

/** Archives or restores the project after the user confirms in a dialog. */
export function DangerZoneSection({ project, onUpdated }: { project: ProjectSummary; onUpdated: (next: ProjectSummary) => void }) {
  const { apiCall } = useAuth();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  // The same button archives an active project or restores an archived one.
  const archiving = project.state === 'active';
  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      // Same update call as rename: keep the name, flip the state, and send the
      // loaded revision so a concurrent change is rejected instead of overwritten.
      const updated = await apiCall((token, tenant) => gatewayClient.updateProject(
        token, project.projectId,
        { expectedRevision: project.revision, name: project.name, state: archiving ? 'archived' : 'active' },
        tenant,
      ), null);
      onUpdated(updated);
      setOpen(false);
    } catch (reason) {
      // Keep the dialog open on failure so the user sees the error and can retry or cancel.
      setError(reason);
    } finally {
      setBusy(false);
    }
  };
  return (
    <PageSection title="Danger zone" description="Archiving keeps settings, memories, and history. You can restore the project later.">
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      <div className="v1-actions">
        <Button appearance={archiving ? 'primary' : 'secondary'} onClick={() => setOpen(true)}>
          {archiving ? 'Archive project' : 'Restore project'}
        </Button>
      </div>
      <Text size={200}>Deleting a project is not available in this version.</Text>
      <AppDialog open={open} onOpenChange={setOpen} title={archiving ? 'Archive this project?' : 'Restore this project?'}>
        <div className="v1-stack">
          <Text>
            {archiving
              ? `${project.name} moves to archived projects. Runs can't start in it until you restore it.`
              : `${project.name} becomes active again.`}
          </Text>
          <div className="v1-actions">
            <Button appearance="primary" disabled={busy} onClick={() => void confirm()}>
              {busy ? 'Saving…' : archiving ? 'Archive project' : 'Restore project'}
            </Button>
            <Button appearance="secondary" disabled={busy} onClick={() => setOpen(false)}>Cancel</Button>
          </div>
        </div>
      </AppDialog>
    </PageSection>
  );
}
