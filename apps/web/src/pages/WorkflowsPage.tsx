import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Menu,
  MenuDivider,
  MenuGroup,
  MenuGroupHeader,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArrowSyncRegular, ChevronDownRegular, ChevronRightRegular, EditRegular, FlowRegular, NetworkCheckRegular, SparkleRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project, WorkflowDetailDto, WorkflowListResponse, WorkflowSummaryDto } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { WorkflowEditor, BLANK_TEMPLATE } from '../components/WorkflowEditor';
import { VisualWorkflowEditor } from '../components/VisualWorkflowEditor';
import { WorkflowDefinitionInlinePanel } from '../components/WorkflowGraphPanel';

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
  menuItemContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    maxWidth: '360px',
    whiteSpace: 'normal',
  },
  menuItemTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    fontWeight: tokens.fontWeightSemibold,
  },
  menuItemDescription: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  sections: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXL,
  },
  sectionGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  sectionHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  activeCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorBrandBackground2,
    border: `1.5px solid ${tokens.colorBrandStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  invalidCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
    borderRadius: tokens.borderRadiusMedium,
  },
});

function describeTrigger(workflow: WorkflowSummaryDto): string {
  const trigger = workflow.trigger;
  if (!trigger) return 'Trigger: unknown';
  if (trigger.event) return `Trigger: ${trigger.type} (${trigger.event})`;
  return `Trigger: ${trigger.type}`;
}

type SelectableWorkflow = WorkflowSummaryDto & { id: string };

function isSelectableWorkflow(workflow: WorkflowSummaryDto): workflow is SelectableWorkflow {
  return workflow.valid && Boolean(workflow.id);
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

  // Editor state: null = list view, non-null = editor open.
  const [editorState, setEditorState] = useState<{
    workflowId: string;
    initialYaml: string;
    visual?: boolean;
  } | null>(null);
  const [editLoading, setEditLoading] = useState(false);

  // Graph expansion: one graph open at a time (null = all collapsed).
  const [expandedGraphId, setExpandedGraphId] = useState<string | null>(null);

  const toggleGraph = useCallback((workflowId: string) => {
    setExpandedGraphId((prev) => (prev === workflowId ? null : workflowId));
  }, []);

  // Generate-workflow dialog state (US10).
  const [generateOpen, setGenerateOpen] = useState(false);
  const [generateDescription, setGenerateDescription] = useState('');
  const [generating, setGenerating] = useState(false);
  const [generateError, setGenerateError] = useState<string | null>(null);

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

  const handleEdit = useCallback(async (wf: WorkflowSummaryDto, visual = false) => {
    if (!wf.id || !projectId) return;
    setEditLoading(true);
    setError(null);
    try {
      const yamlContent = await apiClient.getWorkflowYaml(projectId, wf.id);
      setEditorState({ workflowId: wf.id, initialYaml: yamlContent, visual });
    } catch (err) {
      setError(formatError(err));
    } finally {
      setEditLoading(false);
    }
  }, [projectId]);

  const handleNewWorkflow = useCallback(() => {
    setEditorState({ workflowId: 'my-workflow', initialYaml: BLANK_TEMPLATE });
  }, []);

  const handleOpenGenerate = useCallback(() => {
    setGenerateDescription('');
    setGenerateError(null);
    setGenerateOpen(true);
  }, []);

  const handleGenerate = useCallback(async () => {
    if (!projectId || !generateDescription.trim()) return;
    setGenerating(true);
    setGenerateError(null);
    try {
      const result = await apiClient.generateWorkflow(projectId, generateDescription.trim());
      setGenerateOpen(false);
      setEditorState({ workflowId: result.workflowId, initialYaml: result.yaml });
      setSyncMessage(
        result.wasCorrected
          ? 'Workflow generated (one correction pass applied). Review and save the draft.'
          : 'Workflow generated. Review and save the draft.',
      );
    } catch (err) {
      setGenerateError(formatError(err));
    } finally {
      setGenerating(false);
    }
  }, [projectId, generateDescription]);

  const handleEditorSave = useCallback((saved: WorkflowDetailDto) => {
    // Refresh the workflow list so the saved workflow is visible.
    if (!projectId) return;
    setSyncMessage(`Workflow "${saved.name}" saved.`);
    void apiClient.listWorkflows(projectId).then(setData).catch(() => undefined);
    setEditorState(null);
  }, [projectId]);

  const handleEditorClose = useCallback(() => {
    setEditorState(null);
  }, []);

  if (!projectId) return null;

  const workflows = data?.workflows ?? [];
  const selectableWorkflows = workflows.filter(isSelectableWorkflow);
  const projectWorkflows = selectableWorkflows.filter((wf) => !wf.is_built_in);
  const builtInWorkflows = selectableWorkflows.filter((wf) => wf.is_built_in);

  // Presentation grouping: Active (current default, valid) / Available (valid, not default) / Invalid
  const activeWorkflow = workflows.find((wf) => wf.is_default && wf.valid) ?? null;
  const availableWorkflows = workflows.filter((wf) => !wf.is_default && wf.valid);
  const invalidWorkflows = workflows.filter((wf) => !wf.valid);

  const renderDefaultWorkflowItem = (wf: SelectableWorkflow) => (
    <MenuItem
      key={wf.id}
      disabled={wf.is_default}
      onClick={() => { void handleSetDefault(wf.id); }}
    >
      <div className={styles.menuItemContent}>
        <div className={styles.menuItemTitle}>
          <span>{wf.name ?? wf.id}</span>
          <Badge appearance="outline" color={wf.is_built_in ? 'informative' : 'brand'}>
            {wf.is_built_in ? 'Built-in' : 'Project'}
          </Badge>
          {wf.is_default && <Badge appearance="filled" color="brand">Active</Badge>}
        </div>
        <span className={styles.menuItemDescription}>
          {wf.description || `Workflow id: ${wf.id}`}
        </span>
      </div>
    </MenuItem>
  );

  const renderWorkflowCard = (wf: WorkflowSummaryDto, section: 'active' | 'available' | 'invalid', index = 0) => {
    const cardClass = section === 'active' ? styles.activeCard : section === 'invalid' ? styles.invalidCard : styles.card;
    return (
      <div key={wf.id ?? `${section}-${index}`} className={cardClass}>
        <div className={styles.cardHeader}>
          <span className={styles.cardName}>{wf.name ?? wf.id ?? 'Unnamed workflow'}</span>
          {wf.id && <span className={styles.cardId}>{wf.id}</span>}
          <div className={styles.badges}>
            {section === 'active' && <Badge appearance="filled" color="brand">Active</Badge>}
            {wf.is_built_in && <Badge appearance="outline" color="informative">Built-in</Badge>}
            {section !== 'active' && (
              <Badge appearance="tint" color={wf.valid ? 'success' : 'danger'}>
                {wf.valid ? 'Valid' : 'Invalid'}
              </Badge>
            )}
          </div>
          {wf.id && wf.valid && (
            <Button
              appearance="subtle"
              icon={expandedGraphId === wf.id ? <ChevronDownRegular /> : <ChevronRightRegular />}
              iconPosition="after"
              size="small"
              onClick={() => { if (wf.id) toggleGraph(wf.id); }}
            >
              <NetworkCheckRegular aria-hidden="true" />
              View graph
            </Button>
          )}
          {wf.id && !wf.is_built_in && (
            <Button
              appearance="subtle"
              icon={editLoading ? <Spinner size="extra-tiny" aria-hidden="true" /> : <EditRegular />}
              size="small"
              disabled={editLoading}
              onClick={() => { void handleEdit(wf); }}
            >
              Edit
            </Button>
          )}
          {wf.id && !wf.is_built_in && (
            <Button
              appearance="subtle"
              icon={editLoading ? <Spinner size="extra-tiny" aria-hidden="true" /> : <FlowRegular />}
              size="small"
              disabled={editLoading}
              onClick={() => { void handleEdit(wf, true); }}
            >
              Edit visually
            </Button>
          )}
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

        {wf.id && expandedGraphId === wf.id && (
          <WorkflowDefinitionInlinePanel
            projectId={projectId}
            workflowId={wf.id}
          />
        )}
      </div>
    );
  };

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
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={handleNewWorkflow}
              disabled={editLoading}
            >
              New workflow
            </Button>
            <Button
              appearance="secondary"
              icon={<SparkleRegular />}
              onClick={handleOpenGenerate}
              disabled={editLoading}
            >
              Generate workflow
            </Button>
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
                {projectWorkflows.length > 0 && (
                  <MenuGroup>
                    <MenuGroupHeader>Project workflows</MenuGroupHeader>
                    {projectWorkflows.map(renderDefaultWorkflowItem)}
                  </MenuGroup>
                )}
                {projectWorkflows.length > 0 && builtInWorkflows.length > 0 && <MenuDivider />}
                {builtInWorkflows.length > 0 && (
                  <MenuGroup>
                    <MenuGroupHeader>Built-in workflows</MenuGroupHeader>
                    {builtInWorkflows.map(renderDefaultWorkflowItem)}
                  </MenuGroup>
                )}
                <MenuDivider />
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

      <Dialog open={generateOpen} onOpenChange={(_, d) => { if (!generating) setGenerateOpen(d.open); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Generate workflow</DialogTitle>
            <DialogContent>
              <Field label="Describe the workflow you need" hint="A complete YAML draft will be generated for you to review and edit before saving.">
                <Textarea
                  value={generateDescription}
                  onChange={(_, d) => { setGenerateDescription(d.value); setGenerateError(null); }}
                  placeholder="e.g. A workflow that triages incoming bugs, fixes them, runs QA verification, then merges and records the outcome."
                  rows={5}
                  disabled={generating}
                />
              </Field>
              {generateError && (
                <MessageBar intent="error" style={{ marginTop: tokens.spacingVerticalS }}>
                  <MessageBarBody>{generateError}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={generating} onClick={() => setGenerateOpen(false)}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                disabled={generating || !generateDescription.trim()}
                icon={generating ? <Spinner size="extra-tiny" aria-hidden="true" /> : <SparkleRegular />}
                onClick={() => { void handleGenerate(); }}
              >
                {generating ? 'Generating…' : 'Generate'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {editLoading && <Spinner label="Loading workflow" />}

      {editorState && (
        editorState.visual ? (
          <VisualWorkflowEditor
            projectId={projectId}
            workflowId={editorState.workflowId}
            initialYaml={editorState.initialYaml}
            onSave={handleEditorSave}
            onClose={handleEditorClose}
          />
        ) : (
          <WorkflowEditor
            projectId={projectId}
            workflowId={editorState.workflowId}
            initialYaml={editorState.initialYaml}
            onSave={handleEditorSave}
            onClose={handleEditorClose}
          />
        )
      )}

      {!editorState && (
        <>
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
            <div className={styles.sections}>
              {/* Active workflow — the one this project runs with */}
              {activeWorkflow && (
                <div className={styles.sectionGroup}>
                  <div className={styles.sectionHeader}>
                    <Text weight="semibold" size={400}>Active workflow</Text>
                    <Text size={200}>The workflow this project uses for new runs</Text>
                  </div>
                  {renderWorkflowCard(activeWorkflow, 'active')}
                </div>
              )}

              {/* Available workflows — valid, can be switched to */}
              {availableWorkflows.length > 0 && (
                <div className={styles.sectionGroup}>
                  <div className={styles.sectionHeader}>
                    <Text weight="semibold" size={400}>Available workflows</Text>
                    <Text size={200}>Valid workflows you can set as active using "Set as default"</Text>
                  </div>
                  <div className={styles.list}>
                    {availableWorkflows.map((wf, index) => renderWorkflowCard(wf, 'available', index))}
                  </div>
                </div>
              )}

              {/* Invalid workflows — have errors, cannot run */}
              {invalidWorkflows.length > 0 && (
                <div className={styles.sectionGroup}>
                  <div className={styles.sectionHeader}>
                    <Text weight="semibold" size={400}>Invalid workflows</Text>
                    <Text size={200}>These workflows have errors and cannot run or be set as active</Text>
                  </div>
                  <div className={styles.list}>
                    {invalidWorkflows.map((wf, index) => renderWorkflowCard(wf, 'invalid', index))}
                  </div>
                </div>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}
