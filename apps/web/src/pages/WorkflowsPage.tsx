import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular, ChevronDownRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project, WorkflowListResponse, WorkflowSummaryDto } from '../api/types';
import { PageHeader } from '../components/PageHeader';

// Spec 010 (FR-039/041) — project Workflows management page. Lists the workflows
// discovered from .agentweaver/workflows/ with their validation status, marks the
// project default, and offers a Sync action that re-reads from disk. A "Set as
// default" picker writes the project default via PUT .../workflows/default (a null
// selection clears back to the built-in default).

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  cardName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  cardId: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
  },
  badges: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    alignItems: 'center',
    marginLeft: 'auto',
  },
  meta: {
    display: 'flex',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalM,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    textAlign: 'center',
  },
});

function describeTrigger(workflow: WorkflowSummaryDto): string {
  const trigger = workflow.trigger;
  if (!trigger) return 'Trigger: unknown';
  if (trigger.event) return `Trigger: ${trigger.type} (${trigger.event})`;
  return `Trigger: ${trigger.type}`;
}

export function WorkflowsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [data, setData] = useState<WorkflowListResponse | null>(null);
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncMessage, setSyncMessage] = useState<string | null>(null);
  const [settingDefault, setSettingDefault] = useState(false);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    Promise.all([
      apiClient.listWorkflows(projectId),
      apiClient.getProject(projectId).catch(() => null as Project | null),
    ])
      .then(([list, proj]) => {
        if (!cancelled) {
          setData(list);
          setProject(proj);
        }
      })
      .catch((err) => {
        if (!cancelled) setError(formatError(err));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId]);

  const handleSync = useCallback(async () => {
    if (!projectId) return;
    setSyncing(true);
    setSyncMessage(null);
    setError(null);
    try {
      const refreshed = await apiClient.syncWorkflows(projectId);
      setData(refreshed);
      setSyncMessage(`Synced ${refreshed.workflows.length} workflow${refreshed.workflows.length === 1 ? '' : 's'} from .agentweaver/workflows/.`);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSyncing(false);
    }
  }, [projectId]);

  const handleSetDefault = useCallback(async (workflowId: string | null) => {
    if (!projectId) return;
    setSettingDefault(true);
    setSyncMessage(null);
    setError(null);
    try {
      const refreshed = await apiClient.setDefaultWorkflow(projectId, workflowId);
      setData(refreshed);
      const chosen = workflowId
        ? refreshed.workflows.find((w) => w.id === workflowId)
        : null;
      setSyncMessage(
        workflowId
          ? `Default workflow set to ${chosen?.name ?? workflowId}.`
          : 'Default workflow reset to the built-in default.',
      );
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSettingDefault(false);
    }
  }, [projectId]);

  if (!projectId) return null;

  const workflows = data?.workflows ?? [];

  return (
    <div className={styles.root}>
      <PageHeader
        title="Workflows"
        subtitle="Reusable pipeline definitions."
        breadcrumb={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {project?.name ?? projectId}
            </Link>
            <span>/</span>
            <span>Workflows</span>
          </div>
        }
        actions={
          <>
            <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                appearance="secondary"
                icon={settingDefault ? <Spinner size="extra-tiny" aria-hidden="true" /> : <ChevronDownRegular />}
                iconPosition="after"
                disabled={settingDefault || workflows.length === 0}
              >
                Set as default
              </Button>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {workflows
                  .filter((wf) => wf.valid && wf.id)
                  .map((wf) => (
                    <MenuItem
                      key={wf.id}
                      disabled={wf.is_default}
                      onClick={() => { void handleSetDefault(wf.id); }}
                    >
                      {wf.name ?? wf.id}{wf.is_default ? ' (current)' : ''}
                    </MenuItem>
                  ))}
                <MenuItem onClick={() => { void handleSetDefault(null); }}>
                  Reset to built-in default
                </MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
          <Button
            appearance="secondary"
            icon={syncing ? <Spinner size="extra-tiny" aria-hidden="true" /> : <ArrowSyncRegular />}
            disabled={syncing}
            onClick={() => { void handleSync(); }}
          >
            {syncing ? 'Syncing' : 'Sync'}
          </Button>
          </>
        }
      />

      {syncMessage && (
        <MessageBar intent="success">
          <MessageBarBody>{syncMessage}</MessageBarBody>
        </MessageBar>
      )}

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && <Spinner label="Loading workflows" />}

      {!loading && !error && workflows.length === 0 && (
        <div className={styles.emptyState}>
          <Title3>No workflows found</Title3>
          <Text>Sync to load from .agentweaver/workflows/.</Text>
          <Button
            appearance="primary"
            icon={<ArrowSyncRegular />}
            disabled={syncing}
            onClick={() => { void handleSync(); }}
          >
            Sync
          </Button>
        </div>
      )}

      {!loading && workflows.length > 0 && (
        <div className={styles.list}>
          {workflows.map((wf, index) => (
            <div key={wf.id ?? `invalid-${index}`} className={styles.card}>
              <div className={styles.cardHeader}>
                <span className={styles.cardName}>{wf.name ?? wf.id ?? 'Unnamed workflow'}</span>
                {wf.id && <span className={styles.cardId}>{wf.id}</span>}
                <div className={styles.badges}>
                  {wf.is_default && <Badge appearance="filled" color="brand">Default</Badge>}
                  {wf.is_built_in && <Badge appearance="outline" color="informative">Built-in</Badge>}
                  <Badge appearance="tint" color={wf.valid ? 'success' : 'danger'}>
                    {wf.valid ? 'Valid' : 'Invalid'}
                  </Badge>
                </div>
              </div>

              {wf.description && <Text>{wf.description}</Text>}

              <div className={styles.meta}>
                <span>{describeTrigger(wf)}</span>
                <span>Source: {wf.source}</span>
              </div>

              {!wf.valid && wf.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{wf.error}</MessageBarBody>
                </MessageBar>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
