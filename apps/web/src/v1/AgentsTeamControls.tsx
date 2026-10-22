/**
 * 0.x Agents page controls on v1 casting: Add member, Sync (team export and
 * import), and the list of pending team proposals. Every change becomes a
 * pending proposal first; nothing changes until someone confirms it on the
 * Cast a team page.
 */
import { Button, Field, Select, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowDownloadRegular, ArrowUploadRegular, PersonAddRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AppDialog } from '../components/ui/AppDialog';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import { castableRoles, castingError, draftWithMember, isConflict, reviewPath } from './castingData';
import type { ProjectCastingProposal, ProjectCastingRoleDefinition, ProjectCastingTransfer, ProjectConfiguration } from './contracts';
import { ErrorNotice } from './pageParts';

/** The Gateway rejects casting imports larger than this many bytes. */
const MAX_IMPORT_BYTES = 1024 * 1024;

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

/** Starts a browser download of `transfer` as a JSON file. */
function downloadTransfer(projectId: string, transfer: ProjectCastingTransfer) {
  const blob = new Blob([JSON.stringify(transfer, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = `${projectId}-team-revision-${transfer.sourceConfigurationRevision}.json`;
  link.click();
  // Free the object URL once the click has started the download.
  URL.revokeObjectURL(url);
}

/**
 * 0.x "Sync" panel on v1: exports this project's team as a checked file and
 * imports a team file from another project as a pending proposal.
 */
export function TeamSyncPanel({ projectId, configurationRevision, onConflict }: {
  projectId: string;
  configurationRevision: number;
  onConflict?: () => void;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const navigate = useNavigate();
  const fileInput = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<'export' | 'import' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  /** Downloads the team at the configuration revision shown on the page. */
  const exportTeam = async () => {
    setBusy('export');
    setError(null);
    setNotice(null);
    try {
      const transfer = await apiCall((token, tenant) => gatewayClient.exportCastingTransfer(token, projectId, configurationRevision, tenant), null);
      downloadTransfer(projectId, transfer);
      setNotice(`Exported the team from configuration revision ${transfer.sourceConfigurationRevision}.`);
    } catch (reason) {
      setError(castingError(reason, true));
    } finally {
      setBusy(null);
    }
  };

  /** Reads the chosen file and stages it as a proposal, then opens it for review. */
  const importTeam = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Clear the input so choosing the same file again fires another change.
    event.target.value = '';
    if (!file) return;
    setError(null);
    setNotice(null);
    // Check the size here too, so a large file is not uploaded only to be refused.
    if (file.size > MAX_IMPORT_BYTES) {
      setError('The file is larger than 1 MiB.');
      return;
    }
    setBusy('import');
    try {
      let transfer: ProjectCastingTransfer;
      try {
        transfer = JSON.parse(await file.text()) as ProjectCastingTransfer;
      } catch {
        throw new Error('The file is not valid JSON.');
      }
      // The service checks the format version and digest; the browser only sends the file.
      const proposal = await apiCall((token, tenant) => gatewayClient.importCastingTransfer(token, projectId, {
        expectedConfigurationRevision: configurationRevision,
        transfer,
      }, tenant), null);
      navigate(reviewPath(projectId, proposal.proposalId));
    } catch (reason) {
      setError(castingError(reason, true));
      if (isConflict(reason)) onConflict?.();
    } finally {
      setBusy(null);
    }
  };

  return (
    <PageSection title="Sync" description="Export this team to a file, or import a team file from another project. An import becomes a proposal you review before it applies.">
      <div className={styles.stack}>
        <div className={styles.row} role="toolbar" aria-label="Sync actions">
          <Button icon={<ArrowDownloadRegular />} disabled={busy !== null} onClick={() => void exportTeam()}>
            {busy === 'export' ? 'Exporting…' : 'Export team'}
          </Button>
          <Button icon={<ArrowUploadRegular />} disabled={busy !== null} onClick={() => fileInput.current?.click()}>
            {busy === 'import' ? 'Importing…' : 'Import team'}
          </Button>
          <input
            ref={fileInput}
            type="file"
            accept="application/json,.json"
            hidden
            aria-label="Team file"
            onChange={(event) => void importTeam(event)}
          />
        </div>
        <Text size={200} className={styles.muted}>Team files hold agent charters and casting only. Other project settings are not included.</Text>
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
