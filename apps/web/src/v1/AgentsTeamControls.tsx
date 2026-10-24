/**
 * 0.x Agents page controls on v1 casting: Add member, Sync (team export and
 * import), and the list of pending team proposals. Every change becomes a
 * pending proposal first; nothing changes until someone confirms it on the
 * Cast a team page.
 */
import { Button, Field, Input, Select, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowDownloadRegular, ArrowUploadRegular, PersonAddRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AppDialog } from '../components/ui/AppDialog';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { AgentweaverGatewayClient, GatewayError, gatewayClient } from './api';
import { inspectAgentSuiteArchive, type InspectedAgentSuiteArchive } from './agentSuiteArchive';
import { repoAppBrokerClient, type IdentityBrokerRepoAppClient } from './repoApp';
import { castableRoles, castingError, draftWithMember, draftWithRole, draftWithoutMember, isConflict, reviewPath } from './castingData';
import type {
  AgentSuiteExport,
  AgentSuiteImportRequest,
  ProjectCastingProposal,
  ProjectCastingRoleDefinition,
  ProjectConfiguration,
  ProjectTeamSyncStatus,
  RemoteMcpConnectionDirectoryEntry,
} from './contracts';
import { ErrorNotice } from './pageParts';

const CONNECTION_DIRECTORY_PAGE_SIZE = 50;

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  row: { display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { margin: 0, paddingLeft: tokens.spacingHorizontalL, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
});

/**
 * 0.x "Add member" dialog. Picks one catalog role and stages a proposal that
 * adds it to the current team, then opens that proposal for review.
 */
export function AddMemberDialog({ projectId, configuration, configurationRevision, disabled, onConflict }: {
  projectId: string;
  configuration: ProjectConfiguration;
  configurationRevision: number;
  disabled?: boolean;
  /** Called after a revision conflict so the page can re-read the configuration. */
  onConflict?: () => void;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [roles, setRoles] = useState<ProjectCastingRoleDefinition[] | null>(null);
  const [roleId, setRoleId] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // Load the role catalog the first time the dialog opens.
    if (!open || roles) return;
    let cancelled = false;
    apiCall((token, tenant) => gatewayClient.listCatalogRoles(token, tenant), null)
      .then((result) => { if (!cancelled) setRoles(castableRoles(result)); })
      .catch((reason: unknown) => { if (!cancelled) setError(castingError(reason, false)); });
    return () => { cancelled = true; };
  }, [apiCall, open, roles]);

  const role = roles?.find((item) => item.id === roleId) ?? null;

  /** Stages the one-member proposal against the configuration revision on screen. */
  const add = async () => {
    if (!role) return;
    setSaving(true);
    setError(null);
    try {
      const proposal = await apiCall((token, tenant) => gatewayClient.createCastingDraftProposal(token, projectId, {
        expectedConfigurationRevision: configurationRevision,
        draft: draftWithMember(configuration, role),
      }, tenant), null);
      setOpen(false);
      navigate(reviewPath(projectId, proposal.proposalId));
    } catch (reason) {
      setError(castingError(reason, true));
      // A stale revision means someone saved meanwhile; ask the page to re-read it.
      if (isConflict(reason)) onConflict?.();
    } finally {
      setSaving(false);
    }
  };

  return (
    <AppDialog
      open={open}
      onOpenChange={(next) => { setOpen(next); if (!next) { setRoleId(''); setError(null); } }}
      trigger={<Button appearance="secondary" icon={<PersonAddRegular />} disabled={disabled}>Add member</Button>}
      title="Add team member"
      description="The new member is added as a proposal. Review and confirm it before it changes the team."
      primaryAction={{ label: saving ? 'Adding…' : 'Cast member', onClick: () => void add(), disabled: !role || saving, loading: saving }}
      modalType="alert"
    >
      <div className={styles.stack}>
        <Field label="Role" required>
          <Select value={roleId} disabled={!roles} onChange={(_, data) => setRoleId(data.value)}>
            <option value="">{roles ? 'Select a role' : 'Loading roles…'}</option>
            {roles?.map((item) => <option key={item.id} value={item.id}>{item.title}</option>)}
          </Select>
        </Field>
        {role && <Text size={200} className={styles.muted}>{role.summary}</Text>}
        {error && <ErrorNotice>{error}</ErrorNotice>}
      </div>
    </AppDialog>
  );
}

/** Stages a reviewed role change or removal for a current team member. */
export function TeamMemberActions({ projectId, configuration, configurationRevision, agentId, currentRoleId, disabled, onConflict }: {
  projectId: string;
  configuration: ProjectConfiguration;
  configurationRevision: number;
  agentId: string;
  currentRoleId: string;
  disabled?: boolean;
  onConflict?: () => void;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const navigate = useNavigate();
  const [roles, setRoles] = useState<ProjectCastingRoleDefinition[] | null>(null);
  const [roleId, setRoleId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiCall((token, tenant) => gatewayClient.listCatalogRoles(token, tenant), null)
      .then((result) => { if (!cancelled) setRoles(castableRoles(result)); })
      .catch((reason: unknown) => { if (!cancelled) setError(castingError(reason, false)); });
    return () => { cancelled = true; };
  }, [apiCall]);

  /** Stages a role change without changing the live configuration. */
  const rerole = async () => {
    const role = roles?.find((item) => item.id === roleId);
    if (!role || role.id === currentRoleId) return;
    setBusy(true);
    setError(null);
    try {
      const proposal = await apiCall((token, tenant) => gatewayClient.createCastingDraftProposal(token, projectId, {
        expectedConfigurationRevision: configurationRevision,
        draft: draftWithRole(configuration, agentId, role),
      }, tenant), null);
      navigate(reviewPath(projectId, proposal.proposalId));
    } catch (reason) {
      setError(castingError(reason, true));
      // A stale revision means the page must refresh before another proposal is staged.
      if (isConflict(reason)) onConflict?.();
    } finally {
      setBusy(false);
    }
  };

  /** Stages removal; the member remains until the proposal is confirmed. */
  const remove = async () => {
    setBusy(true);
    setError(null);
    try {
      const proposal = await apiCall((token, tenant) => gatewayClient.createCastingDraftProposal(token, projectId, {
        expectedConfigurationRevision: configurationRevision,
        draft: draftWithoutMember(configuration, agentId),
      }, tenant), null);
      navigate(reviewPath(projectId, proposal.proposalId));
    } catch (reason) {
      setError(castingError(reason, true));
      // Do not retry a proposal against the stale revision; refresh it first.
      if (isConflict(reason)) onConflict?.();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.stack}>
      <Field label="New team role">
        <Select value={roleId} disabled={!roles || disabled || busy} onChange={(_, data) => setRoleId(data.value)}>
          <option value="">{roles ? 'Select a role' : 'Loading roles…'}</option>
          {roles?.map((role) => <option key={role.id} value={role.id}>{role.title}</option>)}
        </Select>
      </Field>
      <div className={styles.row}>
        <Button appearance="secondary" disabled={!roleId || roleId === currentRoleId || disabled || busy} onClick={() => void rerole()}>
          {busy ? 'Saving…' : 'Re-role'}
        </Button>
        <Button appearance="secondary" disabled={disabled || busy} onClick={() => void remove()}>
          {busy ? 'Saving…' : 'Remove from team'}
        </Button>
        {busy && <Spinner size="extra-tiny" aria-hidden="true" />}
      </div>
      {error && <ErrorNotice>{error}</ErrorNotice>}
    </div>
  );
}

/** Starts a browser download of the exported AgentSuite content archive. */
function downloadAgentSuite(projectId: string, result: AgentSuiteExport) {
  // Convert the response's base64 archive in bounded chunks to avoid a large argument list.
  const binary = atob(result.archive);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  const blob = new Blob([bytes], { type: 'application/gzip' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  const safeName = result.suiteName.replace(/[^a-zA-Z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || projectId;
  link.download = `${safeName}-team.tar.gz`;
  link.click();
  // Release the temporary URL after the browser starts the download.
  URL.revokeObjectURL(url);
}

/** Encodes the chosen archive without spreading a large byte array into one call. */
async function encodeArchive(file: File): Promise<string> {
  // Convert slices so the browser's argument limit stays well above each call's size.
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

/** Maps native import failures to the same permission and conflict copy as other team actions. */
function agentSuiteError(reason: unknown): string {
  if (reason instanceof GatewayError && reason.status === 422) {
    return 'The AgentSuite package is invalid or incompatible with this project.';
  }
  if (reason instanceof GatewayError && reason.status === 413) {
    return 'Choose an AgentSuite archive no larger than 16 MiB.';
  }
  if (reason instanceof GatewayError) return castingError(reason, true);
  if (reason instanceof Error) return reason.message;
  return castingError(reason, true);
}

/** Maps repository team-sync failures to actionable, non-sensitive page copy. */
function projectTeamSyncError(reason: unknown): string {
  if (reason instanceof GatewayError && reason.status === 403)
    return 'Repository team sync requires Owner access and a configured GitHub App repository.';
  if (reason instanceof GatewayError && reason.status === 409)
    return 'The project team or repository changed after this preview. Preview again before committing.';
  return castingError(reason, true);
}

/**
 * 0.x "Sync" panel on v1: explicitly previews and commits repository team files,
 * and keeps digest-checked AgentSuite export/import available as a separate transfer.
 */
export function TeamSyncPanel({
  projectId,
  configurationRevision,
  repositoryFullName = null,
  disabled = false,
  onConflict,
  brokerClient = repoAppBrokerClient,
  gateway = gatewayClient,
}: {
  projectId: string;
  configurationRevision: number;
  repositoryFullName?: string | null;
  disabled?: boolean;
  onConflict?: () => void;
  brokerClient?: Pick<IdentityBrokerRepoAppClient, 'getRepositories' | 'createRepositorySelection'>;
  gateway?: Pick<AgentweaverGatewayClient, 'previewProjectTeamSync' | 'commitProjectTeamSync'>;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const navigate = useNavigate();
  const fileInput = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<'export' | 'inspect' | 'import' | 'repository-preview' | 'repository-commit' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [repositoryPreview, setRepositoryPreview] = useState<ProjectTeamSyncStatus | null>(null);
  const [commitMessage, setCommitMessage] = useState('');
  const [selectedArchive, setSelectedArchive] = useState<(InspectedAgentSuiteArchive & { file: File }) | null>(null);
  const [connections, setConnections] = useState<RemoteMcpConnectionDirectoryEntry[]>([]);
  const [providerBindings, setProviderBindings] = useState<Record<string, string>>({});
  const automaticPreviewKey = useRef<string | null>(null);

  /** Resolves the configured repository from Broker discovery before requesting its one-time selection code. */
  const issueRepositorySelection = useCallback(async (): Promise<string> => {
    if (!repositoryFullName) throw new Error('Configure a GitHub App repository before syncing team files.');
    // Use the cookie-backed Broker selection route and its provider IDs; Gateway accepts only the resulting opaque code.
    const { repositories } = await brokerClient.getRepositories();
    const matches = repositories.filter((repository) =>
      repository.fullName.toLowerCase() === repositoryFullName.toLowerCase());
    // Do not guess when discovery returns zero or multiple installations for the configured repository.
    if (matches.length !== 1)
      throw new Error('The configured repository is not available through the connected GitHub App.');
    const repository = matches[0];
    const selection = await brokerClient.createRepositorySelection(
      repository.installationId,
      repository.repositoryId,
    );
    return selection.code;
  }, [brokerClient, repositoryFullName]);

  /** Reads the current repository tree and stores the reviewable change set. */
  const loadRepositoryPreview = useCallback(async (): Promise<ProjectTeamSyncStatus | null> => {
    const selectionCode = await issueRepositorySelection();
    const result = await apiCall((token, tenant) => gateway.previewProjectTeamSync(
        token, projectId, selectionCode, tenant,
      ), null);
    // Keep the page and repository snapshots aligned before showing a preview as commit-ready.
    if (result.configurationRevision !== configurationRevision) {
      setRepositoryPreview(null);
      setError('The project team changed while the preview was loading. Reload the team and preview again.');
      onConflict?.();
      return null;
    }
    setRepositoryPreview(result);
    return result;
  }, [apiCall, configurationRevision, gateway, issueRepositorySelection, onConflict, projectId]);

  const previewRepository = useCallback(async () => {
    setBusy('repository-preview');
    setError(null);
    setNotice(null);
    setRepositoryPreview(null);
    try {
      await loadRepositoryPreview();
    } catch (reason) {
      setError(projectTeamSyncError(reason));
      if (isConflict(reason)) onConflict?.();
    } finally {
      setBusy(null);
    }
  }, [loadRepositoryPreview, onConflict]);

  useEffect(() => {
    if (!repositoryFullName) return;
    const previewKey = `${projectId}\n${configurationRevision}\n${repositoryFullName.toLowerCase()}`;
    if (automaticPreviewKey.current === previewKey) return;
    // The Sync panel mounts only while open, so this read restores the prior panel's status-on-open behavior.
    automaticPreviewKey.current = previewKey;
    void previewRepository();
  }, [configurationRevision, previewRepository, projectId, repositoryFullName]);

  /** Reissues the repository selection code and commits only the exact displayed preview. */
  const commitRepositoryPreview = async () => {
    if (!repositoryPreview) return;
    setBusy('repository-commit');
    setError(null);
    setNotice(null);
    try {
      // Repo App selection codes are short-lived and single-use, so mint a fresh one for the write request.
      const selectionCode = await issueRepositorySelection();
      const result = await apiCall((token, tenant) => gateway.commitProjectTeamSync(
        token,
        projectId,
        selectionCode,
        {
          expectedChangeSetHash: repositoryPreview.changeSetHash,
          expectedConfigurationRevision: repositoryPreview.configurationRevision,
          message: commitMessage.trim() || undefined,
        },
        tenant,
      ), null);
      setRepositoryPreview(null);
      setNotice(`Committed the team sync to ${result.branch} (${result.commitId}).`);
      try {
        // Refresh after the write so the open panel reflects the repository's new state.
        await loadRepositoryPreview();
      } catch (refreshReason) {
        // Preserve the successful commit notice while reporting that status could not be refreshed.
        setError(projectTeamSyncError(refreshReason));
        if (isConflict(refreshReason)) onConflict?.();
      }
    } catch (reason) {
      setError(projectTeamSyncError(reason));
      if (isConflict(reason)) {
        setRepositoryPreview(null);
        onConflict?.();
      }
    } finally {
      setBusy(null);
    }
  };

  /** Downloads the team at the configuration revision shown on the page. */
  const exportTeam = async () => {
    setBusy('export');
    setError(null);
    setNotice(null);
    try {
      const result = await apiCall((token, tenant) => gatewayClient.exportProjectAgentSuite(token, projectId, configurationRevision, tenant), null);
      downloadAgentSuite(projectId, result);
      setNotice(`Exported ${result.suiteName} from configuration revision ${result.sourceConfigurationRevision}.`);
    } catch (reason) {
      setError(castingError(reason, true));
    } finally {
      setBusy(null);
    }
  };

  /** Loads every usable target Environment connection without exposing endpoint data in the UI. */
  const loadUsableConnections = async () => {
    // Follow the opaque cursor and reject a repeated cursor instead of looping on a bad response.
    const result: RemoteMcpConnectionDirectoryEntry[] = [];
    const seenCursors = new Set<string>();
    let after: string | undefined;
    while (true) {
      const page = await apiCall((token, tenant) => gatewayClient.listRemoteMcpConnections(
        token,
        projectId,
        after ? { pageSize: CONNECTION_DIRECTORY_PAGE_SIZE, after } : { pageSize: CONNECTION_DIRECTORY_PAGE_SIZE },
        tenant,
      ), null);
      result.push(...page.connections.filter((entry) =>
        entry.state === 'enabled' && (entry.authenticationMode === 'none' || entry.identityBound)));
      const next = page.nextConnectionId;
      if (!next) return result;
      if (seenCursors.has(next)) throw new Error('The Remote MCP connection list could not be paged.');
      seenCursors.add(next);
      after = next;
    }
  };

  /** Validates and records the selected package, then opens its casting proposal for review. */
  const stageImport = async (
    archive: File,
    inspected: InspectedAgentSuiteArchive,
    toolConnections: AgentSuiteImportRequest['toolConnections'],
  ) => {
    setBusy('import');
    setError(null);
    try {
      // Bind the import to the revision shown when it started so the server rejects stale project state.
      const request: AgentSuiteImportRequest = {
        expectedConfigurationRevision: configurationRevision,
        format: inspected.format,
        archive: await encodeArchive(archive),
        toolConnections,
      };
      const result = await apiCall((token, tenant) => gatewayClient.importProjectAgentSuite(token, projectId, request, tenant), null);
      setSelectedArchive(null);
      if (result.proposal?.proposalId) {
        // A successful import stages a proposal; only an explicit confirmation applies the team.
        navigate(reviewPath(projectId, result.proposal.proposalId));
      } else {
        // No proposal means the imported package caused no configuration changes to review.
        setNotice('The package was imported. No team changes need review.');
      }
    } catch (reason) {
      setError(agentSuiteError(reason));
      if (isConflict(reason)) onConflict?.();
    } finally {
      setBusy(null);
    }
  };

  /** Inspects the archive for remote providers and collects target-environment bindings. */
  const importTeam = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Clear the input so choosing the same archive again fires another change.
    event.target.value = '';
    if (!file) return;
    setSelectedArchive(null);
    setProviderBindings({});
    setError(null);
    setNotice(null);
    setBusy('inspect');
    try {
      const inspected = await inspectAgentSuiteArchive(file);
      if (inspected.toolProviders.length === 0) {
        await stageImport(file, inspected, []);
        return;
      }
      const usable = await loadUsableConnections();
      setConnections(usable);
      setSelectedArchive({ ...inspected, file });
    } catch (reason) {
      setError(agentSuiteError(reason));
      if (isConflict(reason)) onConflict?.();
    } finally {
      setBusy(null);
    }
  };

  const bindingsComplete = selectedArchive !== null &&
    selectedArchive.toolProviders.every((provider) => Boolean(providerBindings[provider]));

  return (
    <PageSection title="Sync" description="Preview and explicitly commit this project team to its configured repository, or transfer team content with an AgentSuite package.">
      <div className={styles.stack}>
        <div className={styles.stack}>
          {repositoryFullName
            ? <Text>Repository sync writes the current cast and charters under .squad/ in {repositoryFullName}. It does not mirror changes automatically.</Text>
            : <Text size={200} className={styles.muted}>Configure a GitHub App repository in project settings to enable repository sync.</Text>}
          {repositoryFullName && (
            <div className={styles.row}>
              <Button
                appearance="secondary"
                disabled={disabled || busy !== null}
                onClick={() => void previewRepository()}
              >
                {busy === 'repository-preview' ? 'Previewing…' : 'Preview repository sync'}
              </Button>
              {repositoryPreview && repositoryPreview.changes.length > 0 && (
                <>
                  <Field label="Commit message (optional)">
                    <Input
                      value={commitMessage}
                      maxLength={200}
                      disabled={disabled || busy !== null}
                      onChange={(_, data) => setCommitMessage(data.value)}
                    />
                  </Field>
                  <Button
                    appearance="primary"
                    disabled={disabled || busy !== null}
                    onClick={() => void commitRepositoryPreview()}
                  >
                    {busy === 'repository-commit' ? 'Committing…' : 'Commit reviewed changes'}
                  </Button>
                </>
              )}
            </div>
          )}
          {repositoryPreview && (
            <div className={styles.stack} aria-live="polite">
              {repositoryPreview.changes.length === 0
                ? <Text>No repository changes are needed for configuration revision {repositoryPreview.configurationRevision}.</Text>
                : <>
                  <Text>Review these changes for configuration revision {repositoryPreview.configurationRevision} before committing:</Text>
                  <Text>Preview hash: <code>{repositoryPreview.changeSetHash}</code></Text>
                  <ul className={styles.list} aria-label="Repository team sync preview">
                    {repositoryPreview.changes.map((change) => (
                      <li key={`${change.kind}:${change.path}`}>{change.kind}: {change.path}</li>
                    ))}
                  </ul>
                </>}
            </div>
          )}
        </div>
        <Text size={200} className={styles.muted}>AgentSuite export and import use a digest-checked portable archive. They transfer team content; they do not read or write repository files.</Text>
        <div className={styles.row} role="toolbar" aria-label="Sync actions">
          <Button icon={<ArrowDownloadRegular />} disabled={disabled || busy !== null} onClick={() => void exportTeam()}>
            {busy === 'export' ? 'Exporting…' : 'Export team'}
          </Button>
          <Button icon={<ArrowUploadRegular />} disabled={disabled || busy !== null || selectedArchive !== null} onClick={() => fileInput.current?.click()}>
            {busy === 'inspect' ? 'Checking archive…' : 'Import team'}
          </Button>
          <input
            ref={fileInput}
            type="file"
            accept=".tar.gz,.tar,application/gzip,application/x-tar"
            hidden
            aria-label="AgentSuite archive"
            onChange={(event) => void importTeam(event)}
          />
        </div>
        {selectedArchive && (
          <div className={styles.stack}>
            <Text>Choose a target connection for each remote tool provider.</Text>
            {selectedArchive.toolProviders.map((provider) => (
              <Field key={provider} label={provider} required>
                <Select
                  value={providerBindings[provider] ?? ''}
                  disabled={disabled || busy !== null}
                  onChange={(_, data) => setProviderBindings((current) => ({ ...current, [provider]: data.value }))}
                >
                  <option value="">Select a connection</option>
                  {connections.map((connection) => (
                    <option key={connection.connectionId} value={connection.connectionId}>{connection.displayName}</option>
                  ))}
                </Select>
              </Field>
            ))}
            {connections.length === 0 && (
              <Text size={200} className={styles.muted}>Enable a Remote MCP connection in this project before importing.</Text>
            )}
            <div className={styles.row}>
              <Button
                disabled={!bindingsComplete || disabled || busy !== null}
                onClick={() => void stageImport(
                  selectedArchive.file,
                  selectedArchive,
                  selectedArchive.toolProviders.map((toolProvider) => ({
                    toolProvider,
                    connectionId: providerBindings[toolProvider],
                  })),
                )}
              >
                {busy === 'import' ? 'Importing…' : 'Review import'}
              </Button>
              <Button appearance="secondary" disabled={disabled || busy !== null} onClick={() => setSelectedArchive(null)}>Cancel</Button>
            </div>
          </div>
        )}
        <Text size={200} className={styles.muted}>The package includes team agents and their tool dependencies. Project settings and connection endpoints are not included.</Text>
        {error && <ErrorNotice>{error}</ErrorNotice>}
        {notice && <Text>{notice}</Text>}
      </div>
    </PageSection>
  );
}

/** Lists the project's pending team proposals with a link to review each one. */
export function PendingProposals({ projectId }: { projectId: string }) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const [proposals, setProposals] = useState<ProjectCastingProposal[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const result = await apiCall((token, tenant) => gatewayClient.listCastingProposals(token, projectId, 20, tenant), null);
      // Only pending proposals need action. Ignore any entry for another project.
      setProposals(result.filter((item) => item.state === 'pending' && item.projectId === projectId));
      setError(null);
    } catch (reason) {
      setProposals(null);
      setError(castingError(reason, false));
    }
  }, [apiCall, projectId]);

  useEffect(() => {
    // Load on the next tick so state updates stay out of the effect body.
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => window.clearTimeout(timer);
  }, [load]);

  if (error) return <ErrorNotice>{error}</ErrorNotice>;
  if (!proposals || proposals.length === 0) return null;
  return (
    <PageSection title="Pending team proposals" description="These changes are not applied yet. Review each one, then confirm or reject it.">
      <ul className={styles.list} aria-label="Pending team proposals">
        {proposals.map((proposal) => (
          <li key={proposal.proposalId}>
            <Link to={reviewPath(projectId, proposal.proposalId)}>
              Review proposal with {proposal.draft.casting.length} member{proposal.draft.casting.length === 1 ? '' : 's'}
            </Link>
            <Text size={200} className={styles.muted}> · created {new Date(proposal.createdAt).toLocaleString()} by {proposal.createdByActorId}</Text>
          </li>
        ))}
      </ul>
    </PageSection>
  );
}
