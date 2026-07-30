import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
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
  Select,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowSyncRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  EditRegular,
  FlowRegular,
  PlayRegular,
  NetworkCheckRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { VisualWorkflowEditor } from '../components/VisualWorkflowEditor';
import { BLANK_TEMPLATE, WorkflowEditor } from '../components/WorkflowEditor';
import { WorkflowDefinitionInlinePanel } from '../components/WorkflowGraphPanel';
import {
  getEventTrigger,
  setEventTrigger,
  setHeaderField,
  setScheduleTrigger,
  WORKFLOW_EVENT_PREDICATES_BY_EVENT,
  WORKFLOW_EVENT_TYPES,
} from '../utils/workflowYaml';
import {
  EmptyState,
  Label,
  ListRow,
  LoadingState,
  PageContainer,
  PageHeader,
  PageSection,
  RichList,
} from '../components/ui';
import type { Project, WorkflowDetailDto, WorkflowListResponse, WorkflowSummaryDto } from '../api/types';
import type {
  WorkflowEventCondition,
  WorkflowEventPredicateType,
  WorkflowEventTrigger,
  WorkflowEventType,
} from '../utils/workflowYaml';

// Spec 010 (FR-039/041) — project Workflows management page, and the reference
// implementation for the shared UI pattern kit (components/ui). Lists the
// workflows discovered from .agentweaver/workflows/ with their validation
// status, marks the project default, and offers a Sync action that re-reads
// from disk. A "Set as default" picker writes the project default via
// PUT .../workflows/default (a null selection clears back to the built-in
// default). Composed entirely from PageHeader / PageSection / RichList.

const useStyles = makeStyles({
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecorationLine: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  breadcrumbSep: {
    color: tokens.colorNeutralForeground4,
  },
  aside: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
  },
  mono: {
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
  },
  rowWrap: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalXXS,
  },
  rowExtra: {
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingBottom: tokens.spacingVerticalS,
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
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  conditionCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  conditionHeader: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
  },
  conditionValues: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  conditionValueRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'flex-end',
  },
  grow: {
    flex: 1,
  },
  triggerHint: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

type SelectableWorkflow = WorkflowSummaryDto & { id: string };

function isSelectableWorkflow(workflow: WorkflowSummaryDto): workflow is SelectableWorkflow {
  return workflow.valid && Boolean(workflow.id);
}

const EVENT_LABELS: Record<WorkflowEventType, string> = {
  issues: 'Issues',
  issue_comment: 'Issue comment',
  pull_request: 'Pull request',
  pull_request_review: 'Pull request review',
  push: 'Push',
  release: 'Release',
  discussion: 'Discussion',
};

const EVENT_PREDICATE_LABELS: Record<WorkflowEventPredicateType, string> = {
  hasLabel: 'Has label',
  isNotLabeledWith: 'Does not have label',
  baseBranch: 'Base branch',
  reviewState: 'Review state',
  ref: 'Ref',
  category: 'Discussion category',
  commentMatches: 'Exact command match',
};

const REVIEW_STATES = ['approved', 'changes_requested', 'commented'] as const;

function defaultEventTrigger(): WorkflowEventTrigger {
  return {
    event: 'issues',
    eventName: 'github.issues',
    conditions: [],
  };
}

function defaultCondition(predicate: WorkflowEventPredicateType): WorkflowEventCondition {
  return {
    predicate,
    values: [predicate === 'reviewState' ? REVIEW_STATES[0] : ''],
    matchAny: false,
  };
}

function conditionValueLabel(predicate: WorkflowEventPredicateType): string {
  switch (predicate) {
    case 'hasLabel':
    case 'isNotLabeledWith':
      return 'Label';
    case 'baseBranch':
      return 'Base branch';
    case 'reviewState':
      return 'Review state';
    case 'ref':
      return 'Git ref';
    case 'category':
      return 'Category';
    case 'commentMatches':
      return 'Exact command match';
  }
}

function conditionValueHint(predicate: WorkflowEventPredicateType): string | undefined {
  switch (predicate) {
    case 'ref':
      return 'Use the full Git ref, for example refs/heads/main.';
    case 'commentMatches':
      return 'Matches the full comment exactly, for example /agentweaver:triage.';
    default:
      return undefined;
  }
}

function triggerBadgeCopy(workflow: WorkflowSummaryDto): string | null {
  if (workflow.trigger?.type === 'schedule') {
    return `${workflow.trigger.interval ?? 'scheduled'}${workflow.trigger.time_of_day ? ` · ${workflow.trigger.time_of_day} UTC` : ''}`;
  }
  if (workflow.trigger?.type === 'event') {
    const eventName = workflow.trigger.event_name?.replace(/^github\./, '') ?? 'event';
    return `event · ${eventName}`;
  }
  return null;
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
  const [runningWorkflowId, setRunningWorkflowId] = useState<string | null>(null);
  const [duplicatingWorkflowId, setDuplicatingWorkflowId] = useState<string | null>(null);
  const [scheduleWorkflow, setScheduleWorkflow] = useState<WorkflowSummaryDto | null>(null);
  const [scheduleInterval, setScheduleInterval] = useState<'daily' | 'weekly' | 'monthly'>('daily');
  const [scheduleTime, setScheduleTime] = useState('09:00');
  const [scheduleDayOfWeek, setScheduleDayOfWeek] = useState('monday');
  const [scheduleDayOfMonth, setScheduleDayOfMonth] = useState('1');
  const [savingSchedule, setSavingSchedule] = useState(false);
  const [eventWorkflow, setEventWorkflow] = useState<WorkflowSummaryDto | null>(null);
  const [eventTrigger, setEventTriggerState] = useState<WorkflowEventTrigger>(defaultEventTrigger);
  const [loadingEventTrigger, setLoadingEventTrigger] = useState(false);
  const [savingEventTrigger, setSavingEventTrigger] = useState(false);

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
    const loadWorkflows = async () => {
      setLoading(true);
      setError(null);
      try {
        const [list, proj] = await Promise.all([
          apiClient.listWorkflows(projectId),
          apiClient.getProject(projectId).catch(() => null as Project | null),
        ]);
        if (!cancelled) {
          setData(list);
          setProject(proj);
        }
      } catch (err) {
        if (!cancelled) setError(formatError(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    void loadWorkflows();
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
    setEditorState({ workflowId: 'my-workflow', initialYaml: BLANK_TEMPLATE, visual: true });
  }, []);

  const handleRunNow = useCallback(async (wf: WorkflowSummaryDto) => {
    if (!projectId || !wf.id) return;
    setRunningWorkflowId(wf.id);
    setError(null);
    try {
      await apiClient.runWorkflowNow(projectId, wf.id);
      setSyncMessage(`Queued a run for "${wf.name ?? wf.id}".`);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setRunningWorkflowId(null);
    }
  }, [projectId]);

  const handleOpenSchedule = useCallback((wf: WorkflowSummaryDto) => {
    const trigger = wf.trigger;
    setScheduleWorkflow(wf);
    setScheduleInterval(trigger?.type === 'schedule' && trigger.interval ? trigger.interval : 'daily');
    setScheduleTime(trigger?.type === 'schedule' && trigger.time_of_day ? trigger.time_of_day : '09:00');
    setScheduleDayOfWeek(trigger?.type === 'schedule' && trigger.day_of_week ? trigger.day_of_week : 'monday');
    setScheduleDayOfMonth(String(trigger?.type === 'schedule' && trigger.day_of_month ? trigger.day_of_month : 1));
  }, []);

  const handleSaveSchedule = useCallback(async (remove = false) => {
    if (!projectId || !scheduleWorkflow?.id) return;
    setSavingSchedule(true);
    setError(null);
    try {
      const yaml = await apiClient.getWorkflowYaml(projectId, scheduleWorkflow.id);
      const dayOfMonth = Number(scheduleDayOfMonth);
      const updatedYaml = setScheduleTrigger(yaml, remove ? null : {
        interval: scheduleInterval,
        timeOfDay: scheduleTime,
        dayOfWeek: scheduleDayOfWeek,
        dayOfMonth,
      });
      await apiClient.saveWorkflowYaml(projectId, scheduleWorkflow.id, updatedYaml);
      setData(await apiClient.listWorkflows(projectId));
      setScheduleWorkflow(null);
      setSyncMessage(remove ? 'Schedule trigger removed.' : 'Schedule trigger saved.');
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSavingSchedule(false);
    }
  }, [projectId, scheduleWorkflow, scheduleInterval, scheduleTime, scheduleDayOfWeek, scheduleDayOfMonth]);

  const handleOpenEvent = useCallback(async (wf: WorkflowSummaryDto) => {
    if (!projectId || !wf.id) return;
    setLoadingEventTrigger(true);
    setError(null);
    try {
      const yaml = await apiClient.getWorkflowYaml(projectId, wf.id);
      // TODO(#641): switch to Tank's structured trigger DTO once the workflows list/detail APIs expose event predicates.
      const parsed = getEventTrigger(yaml);
      setEventTriggerState(parsed ?? {
        ...defaultEventTrigger(),
        event: 'issues',
        eventName: wf.trigger?.type === 'event' && wf.trigger.event_name ? wf.trigger.event_name : 'github.issues',
      });
      setEventWorkflow(wf);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setLoadingEventTrigger(false);
    }
  }, [projectId]);

  const handleSaveEvent = useCallback(async (remove = false) => {
    if (!projectId || !eventWorkflow?.id) return;
    setSavingEventTrigger(true);
    setError(null);
    try {
      const yaml = await apiClient.getWorkflowYaml(projectId, eventWorkflow.id);
      const updatedYaml = setEventTrigger(yaml, remove ? null : eventTrigger);
      await apiClient.saveWorkflowYaml(projectId, eventWorkflow.id, updatedYaml);
      setData(await apiClient.listWorkflows(projectId));
      setEventWorkflow(null);
      setSyncMessage(remove ? 'Event trigger removed.' : 'Event trigger saved.');
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSavingEventTrigger(false);
    }
  }, [projectId, eventTrigger, eventWorkflow]);

  const handleDuplicateBuiltIn = useCallback(async (wf: WorkflowSummaryDto) => {
    if (!projectId || !wf.id) return;
    setDuplicatingWorkflowId(wf.id);
    setError(null);
    try {
      const yaml = await apiClient.getWorkflowYaml(projectId, wf.id);
      const existingIds = new Set((data?.workflows ?? []).map((workflow) => workflow.id));
      const baseId = `${wf.id}-copy`;
      let copyId = baseId;
      for (let suffix = 2; existingIds.has(copyId); suffix += 1) copyId = `${baseId}-${suffix}`;
      const copiedYaml = setHeaderField(setHeaderField(yaml, 'id', copyId), 'name', `Copy of ${wf.name ?? wf.id}`);
      await apiClient.saveWorkflowYaml(projectId, copyId, copiedYaml);
      setData(await apiClient.listWorkflows(projectId));
      setEditorState({ workflowId: copyId, initialYaml: copiedYaml, visual: true });
      setSyncMessage(`Created "${copyId}" from the built-in workflow.`);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setDuplicatingWorkflowId(null);
    }
  }, [projectId, data]);

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

  const updateEventForPicker = useCallback((nextEvent: WorkflowEventType) => {
    const allowedPredicates = new Set(WORKFLOW_EVENT_PREDICATES_BY_EVENT[nextEvent]);
    setEventTriggerState((prev) => ({
      event: nextEvent,
      eventName: `github.${nextEvent}`,
      conditions: prev.conditions
        .filter((condition) => allowedPredicates.has(condition.predicate))
        .map((condition) => ({
          ...condition,
          values: condition.values.length > 0 ? condition.values : [''],
        })),
    }));
  }, []);

  const addEventCondition = useCallback(() => {
    setEventTriggerState((prev) => {
      const [firstPredicate] = WORKFLOW_EVENT_PREDICATES_BY_EVENT[prev.event];
      if (!firstPredicate) return prev;
      return {
        ...prev,
        conditions: [...prev.conditions, defaultCondition(firstPredicate)],
      };
    });
  }, []);

  const updateEventCondition = useCallback((index: number, update: (condition: WorkflowEventCondition) => WorkflowEventCondition) => {
    setEventTriggerState((prev) => ({
      ...prev,
      conditions: prev.conditions.map((condition, conditionIndex) => (
        conditionIndex === index ? update(condition) : condition
      )),
    }));
  }, []);

  const removeEventCondition = useCallback((index: number) => {
    setEventTriggerState((prev) => ({
      ...prev,
      conditions: prev.conditions.filter((_, conditionIndex) => conditionIndex !== index),
    }));
  }, []);

  const setConditionMatchAny = useCallback((index: number, checked: boolean) => {
    setEventTriggerState((prev) => {
      const target = prev.conditions[index];
      if (!target) return prev;
      if (checked) {
        const nextConditions = prev.conditions.map((condition, conditionIndex) => (
          conditionIndex === index
            ? {
              ...condition,
              matchAny: true,
              values: condition.values.length > 1
                ? condition.values
                : [condition.values[0] ?? (condition.predicate === 'reviewState' ? REVIEW_STATES[0] : ''), condition.predicate === 'reviewState' ? REVIEW_STATES[0] : ''],
            }
            : condition
        ));
        return { ...prev, conditions: nextConditions };
      }

      const preservedValues = target.values.length > 0
        ? target.values
        : [target.predicate === 'reviewState' ? REVIEW_STATES[0] : ''];
      const splitConditions: WorkflowEventCondition[] = preservedValues.map((value) => ({
        predicate: target.predicate,
        matchAny: false,
        values: [value],
      }));
      return {
        ...prev,
        conditions: [
          ...prev.conditions.slice(0, index),
          ...splitConditions,
          ...prev.conditions.slice(index + 1),
        ],
      };
    });
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
          <Badge appearance="outline">{wf.is_built_in ? 'Built-in' : 'Project'}</Badge>
          {wf.is_default && <Badge appearance="filled" color="brand">Active</Badge>}
        </div>
        <span className={styles.menuItemDescription}>
          {wf.description || `Workflow id: ${wf.id}`}
        </span>
      </div>
    </MenuItem>
  );

  const renderWorkflowRow = (wf: WorkflowSummaryDto, section: 'active' | 'available' | 'invalid', index = 0) => {
    const key = wf.id ?? `${section}-${index}`;
    const expanded = Boolean(wf.id && expandedGraphId === wf.id);
    return (
      <div key={key} className={styles.rowWrap}>
        <ListRow
          media={<FlowRegular />}
          bubble
          primary={wf.name ?? wf.id ?? 'Unnamed workflow'}
          primaryAside={
            <span className={styles.aside}>
              {wf.id && <Label as="span" className={styles.mono}>{wf.id}</Label>}
              {section === 'active' && <Badge appearance="filled" color="brand">Active</Badge>}
              {wf.is_built_in && <Badge appearance="outline">Built-in</Badge>}
              {triggerBadgeCopy(wf) && (
                <Badge appearance="tint" color="informative">
                  {triggerBadgeCopy(wf)}
                </Badge>
              )}
              {!wf.is_built_in && !wf.trigger && <Badge appearance="outline">Manual only</Badge>}
              {section !== 'active' && (
                <Badge appearance="tint" color={wf.valid ? 'success' : 'danger'}>
                  {wf.valid ? 'Valid' : 'Invalid'}
                </Badge>
              )}
            </span>
          }
          secondary={wf.description || undefined}
          meta={<span>Source: {wf.source}</span>}
          actions={
            <>
              {wf.id && wf.valid && (
                <Button
                  appearance="subtle"
                  size="small"
                  icon={expanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
                  iconPosition="after"
                  onClick={() => { if (wf.id) toggleGraph(wf.id); }}
                >
                  <NetworkCheckRegular aria-hidden="true" /> View graph
                </Button>
              )}
              {wf.id && wf.valid && (
                <Button
                  appearance="secondary"
                  size="small"
                  icon={runningWorkflowId === wf.id ? <Spinner size="extra-tiny" aria-hidden="true" /> : <PlayRegular />}
                  disabled={runningWorkflowId !== null}
                  onClick={() => { void handleRunNow(wf); }}
                >
                  Run now
                </Button>
              )}
              {wf.id && wf.is_built_in && (
                <Button
                  appearance="primary"
                  size="small"
                  icon={duplicatingWorkflowId === wf.id ? <Spinner size="extra-tiny" aria-hidden="true" /> : <FlowRegular />}
                  disabled={duplicatingWorkflowId !== null}
                  onClick={() => { void handleDuplicateBuiltIn(wf); }}
                >
                  Duplicate to project
                </Button>
              )}
              {wf.id && !wf.is_built_in && (
                <Button
                  appearance="subtle"
                  size="small"
                  icon={editLoading ? <Spinner size="extra-tiny" aria-hidden="true" /> : <EditRegular />}
                  disabled={editLoading}
                  onClick={() => { void handleEdit(wf); }}
                >
                  Edit
                </Button>
              )}
              {wf.id && !wf.is_built_in && (
                <Button
                  appearance="primary"
                  size="small"
                  icon={editLoading ? <Spinner size="extra-tiny" aria-hidden="true" /> : <FlowRegular />}
                  disabled={editLoading}
                  onClick={() => { void handleEdit(wf, true); }}
                >
                  Edit visually
                </Button>
              )}
              {wf.id && !wf.is_built_in && (
                <Button appearance="subtle" size="small" onClick={() => handleOpenSchedule(wf)}>
                  {wf.trigger?.type === 'schedule' ? 'Edit schedule' : wf.trigger?.type === 'event' ? 'Replace with schedule' : 'Add schedule'}
                </Button>
              )}
              {wf.id && !wf.is_built_in && (
                <Button
                  appearance="subtle"
                  size="small"
                  disabled={loadingEventTrigger}
                  onClick={() => { void handleOpenEvent(wf); }}
                >
                  {wf.trigger?.type === 'event' ? 'Edit event' : wf.trigger?.type === 'schedule' ? 'Replace with event' : 'Add event'}
                </Button>
              )}
            </>
          }
        />
        {!wf.valid && wf.error && (
          <div className={styles.rowExtra}>
            <MessageBar intent="error">
              <MessageBarBody>{wf.error}</MessageBarBody>
            </MessageBar>
          </div>
        )}
        {expanded && wf.id && (
          <div className={styles.rowExtra}>
            <WorkflowDefinitionInlinePanel projectId={projectId} workflowId={wf.id} />
          </div>
        )}
      </div>
    );
  };

  const header = (
    <PageHeader
      title="Workflows"
      description="Reusable pipeline definitions."
      breadcrumbs={
        <>
          <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
          <span className={styles.breadcrumbSep}>/</span>
          <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
            {project?.name ?? projectId}
          </Link>
          <span className={styles.breadcrumbSep}>/</span>
          <span>Workflows</span>
        </>
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
            appearance="subtle"
            icon={<SparkleRegular />}
            onClick={handleOpenGenerate}
            disabled={editLoading}
          >
            Generate workflow
          </Button>
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                appearance="subtle"
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
            appearance="subtle"
            icon={syncing ? <Spinner size="extra-tiny" aria-hidden="true" /> : <ArrowSyncRegular />}
            disabled={syncing}
            onClick={() => { void handleSync(); }}
          >
            {syncing ? 'Syncing' : 'Sync'}
          </Button>
        </>
      }
    />
  );

  const generateDialog = (
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
            <Button appearance="subtle" disabled={generating} onClick={() => setGenerateOpen(false)}>
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
  );

  const eventPredicateOptions = WORKFLOW_EVENT_PREDICATES_BY_EVENT[eventTrigger.event];

  const eventDialog = (
    <Dialog open={eventWorkflow !== null} onOpenChange={(_, d) => { if (!savingEventTrigger && !loadingEventTrigger && !d.open) setEventWorkflow(null); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Event trigger</DialogTitle>
          <DialogContent>
            {loadingEventTrigger ? (
              <Spinner label="Loading event trigger" />
            ) : (
              <>
                <Field label="GitHub event" hint="Pick from the supported webhook events for workflow automation.">
                  <Select
                    value={eventTrigger.event}
                    onChange={(_, d) => updateEventForPicker(d.value as WorkflowEventType)}
                    disabled={savingEventTrigger}
                  >
                    {WORKFLOW_EVENT_TYPES.map((event) => <option key={event} value={event}>{EVENT_LABELS[event]}</option>)}
                  </Select>
                </Field>

                {eventPredicateOptions.length === 0 ? (
                  <MessageBar intent="info" style={{ marginTop: tokens.spacingVerticalS }}>
                    <MessageBarBody>Release triggers currently match on the selected event only.</MessageBarBody>
                  </MessageBar>
                ) : (
                  <>
                    <div style={{ marginTop: tokens.spacingVerticalS }}>
                      <Label>Conditions</Label>
                      <div className={styles.triggerHint}>Conditions are ANDed together. Turn on “Match any of” within a row to emit an OR group for that field.</div>
                    </div>

                    {eventTrigger.conditions.map((condition, conditionIndex) => (
                      <div key={`${condition.predicate}-${conditionIndex}`} className={styles.conditionCard}>
                        <div className={styles.conditionHeader}>
                          <Field label="Condition type" className={styles.grow}>
                            <Select
                              value={condition.predicate}
                              onChange={(_, d) => updateEventCondition(conditionIndex, () => defaultCondition(d.value as WorkflowEventPredicateType))}
                              disabled={savingEventTrigger}
                            >
                              {eventPredicateOptions.map((predicate) => (
                                <option key={predicate} value={predicate}>{EVENT_PREDICATE_LABELS[predicate]}</option>
                              ))}
                            </Select>
                          </Field>
                          <Button appearance="subtle" disabled={savingEventTrigger} onClick={() => removeEventCondition(conditionIndex)}>
                            Remove condition
                          </Button>
                        </div>

                        <Checkbox
                          label="Match any of"
                          checked={condition.matchAny}
                          disabled={savingEventTrigger}
                          onChange={(_, data) => setConditionMatchAny(conditionIndex, data.checked === true)}
                        />

                        <div className={styles.conditionValues}>
                          {condition.values.map((value, valueIndex) => (
                            <div key={valueIndex} className={styles.conditionValueRow}>
                              <Field
                                label={conditionValueLabel(condition.predicate)}
                                hint={conditionValueHint(condition.predicate)}
                                className={styles.grow}
                              >
                                {condition.predicate === 'reviewState' ? (
                                  <Select
                                    value={value}
                                    disabled={savingEventTrigger}
                                    onChange={(_, d) => updateEventCondition(conditionIndex, (current) => ({
                                      ...current,
                                      values: current.values.map((currentValue, currentIndex) => currentIndex === valueIndex ? d.value : currentValue),
                                    }))}
                                  >
                                    {REVIEW_STATES.map((state) => <option key={state} value={state}>{state}</option>)}
                                  </Select>
                                ) : (
                                  <Input
                                    value={value}
                                    disabled={savingEventTrigger}
                                    onChange={(_, d) => updateEventCondition(conditionIndex, (current) => ({
                                      ...current,
                                      values: current.values.map((currentValue, currentIndex) => currentIndex === valueIndex ? d.value : currentValue),
                                    }))}
                                  />
                                )}
                              </Field>
                              {condition.matchAny && condition.values.length > 1 && (
                                <Button
                                  appearance="subtle"
                                  disabled={savingEventTrigger}
                                  onClick={() => updateEventCondition(conditionIndex, (current) => ({
                                    ...current,
                                    values: current.values.filter((_, currentIndex) => currentIndex !== valueIndex),
                                  }))}
                                >
                                  Remove value
                                </Button>
                              )}
                            </div>
                          ))}
                        </div>

                        {condition.matchAny && (
                          <div>
                            <Button
                              appearance="secondary"
                              disabled={savingEventTrigger}
                              onClick={() => updateEventCondition(conditionIndex, (current) => ({
                                ...current,
                                values: [...current.values, ''],
                              }))}
                            >
                              Add another value
                            </Button>
                          </div>
                        )}
                      </div>
                    ))}

                    <div style={{ marginTop: tokens.spacingVerticalS }}>
                      <Button appearance="secondary" disabled={savingEventTrigger} onClick={addEventCondition}>
                        Add condition
                      </Button>
                    </div>
                  </>
                )}

                {eventWorkflow?.trigger?.type === 'schedule' && (
                  <MessageBar intent="warning" style={{ marginTop: tokens.spacingVerticalS }}>
                    <MessageBarBody>Saving an event trigger replaces this workflow’s existing schedule trigger.</MessageBarBody>
                  </MessageBar>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            {eventWorkflow?.trigger?.type === 'event' && (
              <Button appearance="subtle" disabled={savingEventTrigger || loadingEventTrigger} onClick={() => { void handleSaveEvent(true); }}>
                Remove event
              </Button>
            )}
            <Button appearance="subtle" disabled={savingEventTrigger || loadingEventTrigger} onClick={() => setEventWorkflow(null)}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={savingEventTrigger || loadingEventTrigger || eventTrigger.conditions.some((condition) => condition.values.some((value) => !value.trim()))}
              onClick={() => { void handleSaveEvent(); }}
            >
              {savingEventTrigger ? 'Saving…' : 'Save event'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );

  const scheduleDialog = (
    <Dialog open={scheduleWorkflow !== null} onOpenChange={(_, d) => { if (!savingSchedule && !d.open) setScheduleWorkflow(null); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Schedule workflow</DialogTitle>
          <DialogContent>
            <Field label="Cadence">
              <Select value={scheduleInterval} onChange={(_, d) => setScheduleInterval(d.value as typeof scheduleInterval)} disabled={savingSchedule}>
                <option value="daily">Daily</option>
                <option value="weekly">Weekly</option>
                <option value="monthly">Monthly</option>
              </Select>
            </Field>
            {scheduleInterval === 'weekly' && (
              <Field label="Day of week" style={{ marginTop: tokens.spacingVerticalS }}>
                <Select value={scheduleDayOfWeek} onChange={(_, d) => setScheduleDayOfWeek(d.value)} disabled={savingSchedule}>
                  {['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'].map((day) => <option key={day} value={day}>{day}</option>)}
                </Select>
              </Field>
            )}
            {scheduleInterval === 'monthly' && (
              <Field label="Day of month (1–28)" style={{ marginTop: tokens.spacingVerticalS }}>
                <Input type="number" min="1" max="28" value={scheduleDayOfMonth} onChange={(_, d) => setScheduleDayOfMonth(d.value)} disabled={savingSchedule} />
              </Field>
            )}
            <Field label="UTC time" hint="Schedules are evaluated in UTC." style={{ marginTop: tokens.spacingVerticalS }}>
              <Input type="time" value={scheduleTime} onChange={(_, d) => setScheduleTime(d.value)} disabled={savingSchedule} />
            </Field>
          </DialogContent>
          <DialogActions>
            {scheduleWorkflow?.trigger?.type === 'schedule' && <Button appearance="subtle" disabled={savingSchedule} onClick={() => { void handleSaveSchedule(true); }}>Remove schedule</Button>}
            <Button appearance="subtle" disabled={savingSchedule} onClick={() => setScheduleWorkflow(null)}>Cancel</Button>
            <Button appearance="primary" disabled={savingSchedule || !/^\d{2}:\d{2}$/.test(scheduleTime) || (scheduleInterval === 'monthly' && (Number(scheduleDayOfMonth) < 1 || Number(scheduleDayOfMonth) > 28))} onClick={() => { void handleSaveSchedule(); }}>
              {savingSchedule ? 'Saving…' : 'Save schedule'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );

  // Editor takes over the whole page when open.
  if (editorState) {
    return (
      <PageContainer>
        {header}
        {generateDialog}
        {eventDialog}
        {scheduleDialog}
        {editorState.visual ? (
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
        )}
      </PageContainer>
    );
  }

  return (
    <PageContainer>
      {header}
      {generateDialog}
      {eventDialog}
      {scheduleDialog}

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

      {loading && <LoadingState rows={3} label="Loading workflows" />}

      {!loading && !error && workflows.length === 0 && (
        <EmptyState
          icon={<FlowRegular />}
          title="No workflows found"
          description="Sync to load workflow definitions from .agentweaver/workflows/."
          action={
            <Button
              appearance="primary"
              icon={<ArrowSyncRegular />}
              disabled={syncing}
              onClick={() => { void handleSync(); }}
            >
              Sync
            </Button>
          }
        />
      )}

      {!loading && workflows.length > 0 && (
        <>
          {activeWorkflow && (
            <PageSection
              title="Active workflow"
              description="The workflow this project uses for new runs."
            >
              <RichList aria-label="Active workflow">
                {renderWorkflowRow(activeWorkflow, 'active')}
              </RichList>
            </PageSection>
          )}

          {availableWorkflows.length > 0 && (
            <PageSection
              title="Available workflows"
              description={'Valid workflows you can set as active using "Set as default".'}
            >
              <RichList aria-label="Available workflows">
                {availableWorkflows.map((wf, index) => renderWorkflowRow(wf, 'available', index))}
              </RichList>
            </PageSection>
          )}

          {invalidWorkflows.length > 0 && (
            <PageSection
              title="Invalid workflows"
              description="These workflows have errors and cannot run or be set as active."
            >
              <RichList aria-label="Invalid workflows">
                {invalidWorkflows.map((wf, index) => renderWorkflowRow(wf, 'invalid', index))}
              </RichList>
            </PageSection>
          )}
        </>
      )}
    </PageContainer>
  );
}
