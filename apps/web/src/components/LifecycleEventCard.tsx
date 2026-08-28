import {
  apiClient } from '../api/apiClient';
import { Badge,
  Button,
  Text,
  Tooltip } from '@fluentui/react-components';
import { makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  BranchRegular,
  CheckmarkCircleFilled,
  CheckmarkRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  ClockRegular,
  CodeRegular,
  CopyRegular,
  DismissCircleFilled,
  ErrorCircleFilled,
  ShieldLockRegular,
  ShieldRegular,
  TaskListSquareLtrRegular,
  WarningFilled,
  ArrowRoutingRegular,
  ArrowSyncRegular,
  RocketRegular,
  ArrowForwardRegular,
  InfoRegular,
} from '@fluentui/react-icons';
import { memo, useEffect, useState } from 'react';
import type { RunStreamEvent } from '../api/sse';
const useStyles = makeStyles({
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusSmall,
  },
  // Generic flex row helper
  rowS: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  rowXS: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  rowWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
  },
  // Inline status indicator (replaces StatusIconText wrapper)
  statusRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    flexShrink: 0,
  },
  statusIcon: {
    display: 'inline-flex',
    flexShrink: 0,
    alignItems: 'center',
  },
  successIcon: { color: tokens.colorPaletteGreenForeground1, flexShrink: 0 },
  errorIcon: { color: tokens.colorPaletteRedForeground1, flexShrink: 0 },
  warningIcon: { color: tokens.colorPaletteYellowForeground1, flexShrink: 0 },
  subtleIcon: { color: tokens.colorNeutralForeground3, flexShrink: 0 },
  summary: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
    wordBreak: 'break-word',
    flexGrow: 1,
  },
  badge: { flexShrink: 0 },
  // agent.intent renders with the SAME subtle treatment as the "Used N tools"
  // cluster header (muted foreground, compact) — no card, icon, or badge chrome.
  // paddingLeft matches TurnGroup's `steps` container so intent lines align with
  // the "Used N tools" rows that live inside that container.
  intentAnnotation: {
    padding: '1px 0',
    paddingLeft: tokens.spacingHorizontalM,
    color: tokens.colorNeutralForeground3,
    wordBreak: 'break-word',
  },

  // truncated comment text (max 3 lines)
  changesRequestedComment: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    marginLeft: '24px',
    marginTop: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalS,
    borderLeft: `2px solid ${tokens.colorNeutralStroke1}`,
    display: '-webkit-box',
    WebkitBoxOrient: 'vertical',
    WebkitLineClamp: '3',
    overflow: 'hidden',
    wordBreak: 'break-word',
  },
  terminalLine: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    marginTop: '1px',
    marginBottom: '1px',
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusSmall,
  },
  terminalStderrLine: {
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  streamPrefix: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
    userSelect: 'none',
  },
  stderrPrefix: {
    color: tokens.colorPaletteRedForeground1,
  },
  terminalContent: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
    color: tokens.colorNeutralForeground1,
    flexGrow: 1,
  },
  stderrContent: {
    color: tokens.colorPaletteRedForeground1,
  },

  // shell.approval_required
  approvalCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorStatusDangerBorder1}`,
    backgroundColor: tokens.colorStatusDangerBackground2,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    boxShadow: tokens.shadow8,
  },
  approvalHeading: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  approvalMeta: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  approvalCommand: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground3,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-all',
  },

  approvalActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalXS,
  },
  approvalResolved: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },

  // sandbox.warning banner
  sandboxWarning: {
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
  },

  // --- ToolApprovalCard redesign styles ---
  approvalCardRedesign: {
    display: 'flex',
    flexDirection: 'column',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalXS,
    overflow: 'hidden',
    boxShadow: tokens.shadow8,
  },
  approvalHeaderRedesign: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorStatusWarningBackground2,
    borderBottom: `1px solid ${tokens.colorStatusWarningBorder1}`,
  },
  approvalBodyRedesign: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  approvalToolBadgeRedesign: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    backgroundColor: tokens.colorNeutralBackground3,
    padding: `2px ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    color: tokens.colorNeutralForeground1,
  },
  approvalUrlRowRedesign: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'flex-start',
  },
  approvalUrlBlockRedesign: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusSmall,
    padding: tokens.spacingHorizontalS,
    flex: '1',
    margin: '0',
    whiteSpace: 'nowrap',
    overflowX: 'auto',
    color: tokens.colorNeutralForeground1,
  },
  approvalCopyBtnRedesign: {
    flexShrink: '0',
  },
  approvalIntentionRedesign: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  approvalActionsRedesign: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    marginTop: tokens.spacingVerticalXS,
  },
  approvalResolvedRedesign: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  approvalRequestIdRedesign: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    marginTop: tokens.spacingVerticalXS,
  },
});

type BadgeColor = 'success' | 'warning' | 'danger' | 'subtle' | 'informative';

// Human-readable label for the ORIGIN of a steering signal so every steering
// action names WHO/WHAT triggered it (Ahmed: nothing should feel like a glitch).
function steeringSourceLabel(source: string): string {
  switch (source) {
    case 'human-review':
    case 'human_review':
    case 'human': return 'human review';
    case 'rai': return 'RAI review';
    case 'rubberduck':
    case 'rubber-duck': return 'rubber-duck review';
    case 'build-test':
    case 'build_test': return 'build/test gate';
    case 'agent': return 'an agent';
    case 'coordinator': return 'the coordinator';
    case 'step': return 'a workflow step';
    default: return source || 'an unknown source';
  }
}

// Plain-language rendering of the coordinator's conscious A/B/C/D choice. The
// `decision` strings are the EXACT backend SteeringDirection constants emitted on
// coordinator.steering_decision: 'in_place_steer' | 'dispatch_fresh' | 'proceed' |
// 'advisory'. `dispatch_fresh` (B) is deliberately loud + rocket-iconed so a fresh
// re-dispatch reads as an explained decision, never an unexplained "glitch".
function steeringDecisionMeta(decision: string): { icon: React.ReactNode; label: string; verb: string; badgeColor: BadgeColor } {
  switch (decision) {
    case 'in_place_steer':
      return { icon: <ArrowSyncRegular aria-hidden="true" />, label: 'steered in place', verb: 'Steered in place (context preserved)', badgeColor: 'informative' };
    case 'dispatch_fresh':
      return { icon: <RocketRegular aria-hidden="true" />, label: 'fresh dispatch', verb: 'Dispatched fresh subtask', badgeColor: 'warning' };
    case 'proceed':
      return { icon: <ArrowForwardRegular aria-hidden="true" />, label: 'proceeded', verb: 'Proceeded to review', badgeColor: 'informative' };
    case 'advisory':
      return { icon: <InfoRegular aria-hidden="true" />, label: 'advisory noted', verb: 'Advisory noted (no action taken)', badgeColor: 'subtle' };
    default:
      return { icon: <ArrowRoutingRegular aria-hidden="true" />, label: 'steering decision', verb: decision ? `Decision: ${decision}` : 'Steering decision', badgeColor: 'informative' };
  }
}

// Builds the "→ subtask a, b (attempt N)" tail shown on a decision so the target
// and attempt are always visible.
function steeringTargetSuffix(p: Record<string, unknown>): string {
  const rawIds = p['subtaskIds'] ?? p['targetSubtaskIds'] ?? p['redispatchedSubtaskIds'] ?? p['redispatchSubtaskIds'];
  const ids = Array.isArray(rawIds) ? (rawIds as unknown[]).map((v) => String(v)).filter(Boolean) : [];
  let suffix = '';
  if (ids.length > 0) {
    const shown = ids.slice(0, 3).join(', ');
    suffix += ` → subtask${ids.length === 1 ? '' : 's'} ${shown}${ids.length > 3 ? ` +${ids.length - 3} more` : ''}`;
  }
  const attempt = p['attempt'] ?? p['attemptNumber'] ?? p['iteration'];
  if (attempt !== undefined && attempt !== null && String(attempt).length > 0) {
    suffix += ` (attempt ${String(attempt)})`;
  }
  return suffix;
}

function lifecycleProps(event: RunStreamEvent, runOutcome?: { achieved: boolean; reason: string }): {
  icon: React.ReactNode;
  label: string;
  summary: string;
  badgeColor: BadgeColor;
} {
  // SECURITY (Y-3): all string values extracted and rendered as React text — no HTML
  const p = event.payload;
  switch (event.type) {
    case 'run.completed':
      if (runOutcome?.achieved === false) {
        return {
          icon: <WarningFilled aria-hidden="true" />,
          label: 'run.completed',
          summary: `Not achieved · ${runOutcome.reason}`,
          badgeColor: 'warning',
        };
      }
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'run.completed',
        summary: String(p['summary'] ?? 'Run completed'),
        badgeColor: 'success',
      };
    case 'run.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'run.failed',
        summary: String(p['message'] ?? p['summary'] ?? 'Run failed'),
        badgeColor: 'danger',
      };
    case 'run.error':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'run.error',
        summary: String(p['reason'] ?? p['message'] ?? 'Retryable error — run is awaiting review'),
        badgeColor: 'warning',
      };
    case 'review.requested':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review.requested',
        summary: `Tree ${String(p['tree_hash'] ?? '')}`,
        badgeColor: 'warning',
      };
    case 'review.approved':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'review.approved',
        summary: `Approved by ${String(p['approved_by'] ?? '')}`,
        badgeColor: 'success',
      };
    case 'review.declined':
      return {
        icon: <DismissCircleFilled aria-hidden="true" />,
        label: 'review.declined',
        summary: `Declined by ${String(p['declined_by'] ?? '')}`,
        badgeColor: 'subtle',
      };
    case 'review.changes_requested':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review.changes_requested',
        summary: String(p['comment'] ?? 'Changes requested'),
        badgeColor: 'warning',
      };
    case 'revision.started':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'revision.started',
        summary: String(p['message'] ?? 'Revision started'),
        badgeColor: 'subtle',
      };
    case 'merge.completed':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'merge.completed',
        summary: `Merged at ${String(p['merged_commit_hash'] ?? '')}`,
        badgeColor: 'success',
      };
    case 'merge.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'merge.failed',
        summary: String(p['reason'] ?? 'Merge failed'),
        badgeColor: 'danger',
      };
    case 'merge.conflicted': {
      const rawFiles = p['conflictingFiles'] ?? p['conflicting_files'];
      const files = Array.isArray(rawFiles)
        ? rawFiles.map((file) => String(file)).join(', ')
        : '';
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'merge conflicted',
        summary: files ? `Needs resolution: ${files}` : String(p['reason'] ?? 'Merge needs resolution'),
        badgeColor: 'warning',
      };
    }
    case 'sandbox.selected':
      return {
        icon: <ShieldRegular aria-hidden="true" />,
        label: 'sandbox.selected',
        summary: `${String(p['backend'] ?? '')}${p['isRealIsolation'] === false ? ' \u2014 no isolation' : ''}`,
        badgeColor: p['isRealIsolation'] === false ? 'warning' : 'subtle',
      };
    case 'agent.system_prompt': {
      const provider = String(p['provider'] ?? 'unknown');
      const prompt = p['prompt'] ? String(p['prompt']) : null;
      const note = p['note'] ? String(p['note']) : null;
      const chars = prompt ? ` (${prompt.length} chars)` : '';
      return {
        icon: <CodeRegular aria-hidden="true" />,
        label: `system_prompt:${provider}`,
        summary: prompt ? `${prompt.slice(0, 120).replace(/\n/g, ' ')}…${chars}` : (note ?? ''),
        badgeColor: 'subtle',
      };
    }

    // -----------------------------------------------------------------------
    // Coordinator orchestration milestones — friendly, scannable narrative cards.
    // -----------------------------------------------------------------------
    case 'coordinator.recovered':
      return {
        icon: <ShieldRegular aria-hidden="true" />,
        label: 'recovered',
        summary: `Resumed after restart (${String(p['status'] ?? 'in progress')})`,
        badgeColor: 'subtle',
      };
    case 'coordinator.outcome_spec':
      return {
        icon: <TaskListSquareLtrRegular aria-hidden="true" />,
        label: 'outcome plan',
        summary: p['desiredOutcome']
          ? `Drafted: ${String(p['desiredOutcome']).slice(0, 140).replace(/\n/g, ' ')}`
          : 'Drafted outcome plan for review',
        badgeColor: 'subtle',
      };
    case 'coordinator.outcome_spec.confirmed':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'outcome confirmed',
        summary: p['confirmedBy'] ? `Confirmed by ${String(p['confirmedBy'])}` : 'Outcome plan confirmed',
        badgeColor: 'success',
      };
    case 'coordinator.work_plan': {
      const subtasks = Array.isArray(p['subtasks']) ? (p['subtasks'] as unknown[]).length : 0;
      const warnings = Array.isArray(p['warnings']) ? p['warnings'].map(String) : [];
      return {
        icon: warnings.length > 0
          ? <WarningFilled aria-hidden="true" />
          : <TaskListSquareLtrRegular aria-hidden="true" />,
        label: 'work plan',
        summary: warnings[0] ?? `Decomposed into ${subtasks} subtask${subtasks === 1 ? '' : 's'}`,
        badgeColor: warnings.length > 0 ? 'warning' : 'subtle',
      };
    }
    case 'subtask.dispatched':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'subtask dispatched',
        summary: `${String(p['assignedAgent'] ?? 'agent')}${p['selectedModelId'] ? ` · ${String(p['selectedModelId'])}` : ''}`,
        badgeColor: 'subtle',
      };
    case 'subtask.pending_capacity':
      return {
        icon: <ClockRegular aria-hidden="true" />,
        label: 'subtask waiting for capacity',
        summary: String(p['reason'] ?? p['message'] ?? 'Waiting for agent capacity'),
        badgeColor: 'warning',
      };
    case 'subtask.rai_flagged':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'subtask rai',
        summary: `${String(p['assignedAgent'] ?? 'Subtask')} flagged by RAI`,
        badgeColor: 'warning',
      };
    case 'subtask.assemble_ready':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'subtask ready',
        summary: `${String(p['assignedAgent'] ?? 'Subtask')} ready to assemble`,
        badgeColor: 'success',
      };
    case 'subtask.completed':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'subtask completed',
        summary: `${String(p['assignedAgent'] ?? 'Subtask')} completed (no changes)`,
        badgeColor: 'success',
      };
    case 'subtask.failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'subtask failed',
        summary: `${String(p['assignedAgent'] ?? 'Subtask')} failed`,
        badgeColor: 'danger',
      };
    case 'coordinator.children_complete':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'subtasks done',
        summary: `${Number(p['assembleReady'] ?? 0)} ready · ${Number(p['completed'] ?? 0)} no-change · ${Number(p['failed'] ?? 0)} failed of ${Number(p['total'] ?? 0)}`,
        badgeColor: Number(p['failed'] ?? 0) > 0 ? 'warning' : 'subtle',
      };
    case 'coordinator.steering':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'steering',
        summary: `${String(p['kind'] ?? 'directive')}${p['instruction'] ? `: ${String(p['instruction']).slice(0, 120)}` : ''} (${String(p['status'] ?? '')})`,
        badgeColor: 'subtle',
      };
    case 'coordinator.assembly_started':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'assembly',
        summary: `Collective assembly started${p['integrationBranch'] ? ` on ${String(p['integrationBranch'])}` : ''}`,
        badgeColor: 'subtle',
      };
    case 'coordinator.integration_conflict_auto_resolved': {
      const files = Array.isArray(p['conflictingFiles'])
        ? (p['conflictingFiles'] as unknown[]).map((file) => String(file)).join(', ')
        : '';
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'auto-resolved merge conflict',
        summary: `Accepted changes from ${String(p['conflictingBranch'] ?? 'child branch')}${files ? ` for files: ${files}` : ''}`,
        badgeColor: 'subtle',
      };
    }
    case 'coordinator.assembly_rai_started':
      return {
        icon: <ShieldRegular aria-hidden="true" />,
        label: 'assembly rai',
        summary: 'Collective RAI review started',
        badgeColor: 'subtle',
      };
    case 'coordinator.assembly_rai_completed':
      return {
        icon: <ShieldRegular aria-hidden="true" />,
        label: 'assembly rai',
        summary: p['raiSafetyFlagged'] ? 'RAI review complete — safety flagged' : 'RAI review complete',
        badgeColor: p['raiSafetyFlagged'] ? 'warning' : 'subtle',
      };
    case 'coordinator.assembly_review_requested':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review requested',
        summary: 'Collective output ready for your review',
        badgeColor: 'warning',
      };
    case 'coordinator.assembly_review_approved':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'review approved',
        summary: p['reviewer'] ? `Approved by ${String(p['reviewer'])}` : 'Review approved',
        badgeColor: 'success',
      };
    case 'coordinator.assembly_review_preserved':
      return {
        icon: <WarningFilled aria-hidden="true" />,
        label: 'review preserved',
        summary: String(p['reason'] ?? 'Coordinator failed; review remains available'),
        badgeColor: 'warning',
      };
    case 'coordinator.steering_received': {
      const source = steeringSourceLabel(String(p['source'] ?? ''));
      const severity = String(p['severity'] ?? '');
      const feedback = String(p['feedback'] ?? p['reason'] ?? p['summary'] ?? '');
      const scope = String(p['targetScope'] ?? '');
      let summary = `Steering signal received from ${source}`;
      if (severity) summary += ` · ${severity}`;
      if (scope) summary += ` · ${scope}`;
      if (feedback) summary += `: ${feedback.slice(0, 160)}`;
      return {
        icon: <ArrowRoutingRegular aria-hidden="true" />,
        label: 'steering received',
        summary,
        badgeColor: severity === 'blocking' ? 'warning' : 'informative',
      };
    }
    case 'coordinator.steering_decision': {
      const meta = steeringDecisionMeta(String(p['decision'] ?? ''));
      const rationale = String(p['rationale'] ?? p['reason'] ?? p['summary'] ?? '');
      let summary = meta.verb + steeringTargetSuffix(p);
      if (rationale) summary += ` — because: ${rationale.slice(0, 200)}`;
      return {
        icon: meta.icon,
        label: meta.label,
        summary,
        badgeColor: meta.badgeColor,
      };
    }
    case 'coordinator.assembly_changes_requested': {
      // COMPAT ALIAS for the legacy re-dispatch event. Render it with the SAME
      // steering visual treatment as coordinator.steering_decision{dispatch_fresh}
      // so the old re-dispatch reads as an explained decision, not a glitch.
      const meta = steeringDecisionMeta('dispatch_fresh');
      const rationale = String(p['reason'] ?? p['feedback'] ?? p['summary'] ?? '');
      let summary = meta.verb + steeringTargetSuffix(p);
      if (rationale) summary += ` — because: ${rationale.slice(0, 200)}`;
      return {
        icon: meta.icon,
        label: meta.label,
        summary,
        badgeColor: meta.badgeColor,
      };
    }
    case 'coordinator.assembly_merge_started':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'merge',
        summary: `Merging${p['integrationBranch'] ? ` ${String(p['integrationBranch'])}` : ''}`,
        badgeColor: 'subtle',
      };
    case 'coordinator.assembly_merge_completed':
      return {
        icon: <BranchRegular aria-hidden="true" />,
        label: 'merge completed',
        summary: `Merged${p['commitHash'] ? ` at ${String(p['commitHash']).slice(0, 10)}` : ''}`,
        badgeColor: 'success',
      };
    case 'coordinator.assembly_merge_failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'merge failed',
        summary: String(p['reason'] ?? 'Merge failed'),
        badgeColor: 'danger',
      };
    case 'coordinator.assembly_scribe_started':
      return {
        icon: <CodeRegular aria-hidden="true" />,
        label: 'scribe',
        summary: 'Documentation pass started',
        badgeColor: 'subtle',
      };
    case 'coordinator.assembly_scribe_completed':
      return {
        icon: <CodeRegular aria-hidden="true" />,
        label: 'scribe',
        summary: 'Documentation pass complete',
        badgeColor: 'subtle',
      };
    case 'coordinator.assembly_completed':
      return {
        icon: <CheckmarkCircleFilled aria-hidden="true" />,
        label: 'assembly complete',
        summary: `Complete${p['commitHash'] ? ` — merged at ${String(p['commitHash']).slice(0, 10)}` : ''}`,
        badgeColor: 'success',
      };
    case 'coordinator.assembly_blocked':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'assembly blocked',
        summary: String(p['reason'] ?? 'Assembly blocked'),
        badgeColor: 'danger',
      };
    case 'coordinator.assembly_declined':
      return {
        icon: <DismissCircleFilled aria-hidden="true" />,
        label: 'assembly declined',
        summary: String(p['reason'] ?? 'Assembly declined'),
        badgeColor: 'subtle',
      };
    case 'coordinator.assembly_failed':
      return {
        icon: <ErrorCircleFilled aria-hidden="true" />,
        label: 'assembly failed',
        summary: String(p['reason'] ?? 'Assembly failed'),
        badgeColor: 'danger',
      };
    default:
      return {
        icon: null,
        label: event.type,
        summary: JSON.stringify(p),
        badgeColor: 'subtle',
      };
  }
}

interface ToolApprovalCardProps {
  styles: ReturnType<typeof useStyles>;
  requestId: string;
  displayId: string;
  toolName: string;
  url: string | null;
  intention: string | null;
  expiresAt?: string | null;
  runId?: string;
  isResolved?: boolean;
  resolvedScope?: string | null;
}

function scopeLabel(scope: string): string {
  switch (scope) {
    case 'once': return 'Allowed (once)';
    case 'run': return 'Allowed (this session)';
    case 'always': return 'Allowed (always, this project)';
    case 'tool': return 'Allowed (all calls to tool)';
    case 'approved': return 'Allowed';
    default: return `Allowed (${scope})`;
  }
}

function payloadString(payload: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (value != null && String(value).trim() !== '') return String(value);
  }
  return undefined;
}

function ToolApprovalCard({ styles, requestId, displayId, toolName, url, intention, expiresAt, runId, isResolved, resolvedScope: resolvedScopeProp }: ToolApprovalCardProps) {
  const [resolvedScope, setResolvedScope] = useState<string | null>(
    isResolved ? (resolvedScopeProp ?? 'expired') : null,
  );
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [retryRequested, setRetryRequested] = useState(false);

  // Sync server-driven resolution (e.g. tool.approval_resolved expiry) into local state.
  // Runs when isResolved or resolvedScopeProp change — covers the case where the SSE event
  // arrives after the card is already mounted with isResolved=false.
  useEffect(() => {
    const syncResolvedScope = async () => {
      if (isResolved && resolvedScope === null) {
        setResolvedScope(resolvedScopeProp ?? 'expired');
      }
    };
    void syncResolvedScope();
  // resolvedScope is intentionally omitted: we only want to fire once when isResolved flips.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isResolved, resolvedScopeProp]);

  const handleAllow = async (scope: 'once' | 'run' | 'always' | 'tool') => {
    if (!runId || resolvedScope !== null || busy) return;
    setBusy(true);
    setActionError(null);
    try {
      await apiClient.approveTool(runId, requestId, scope);
      setResolvedScope(scope);
    } catch (err) {
      setActionError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const handleDeny = async () => {
    if (!runId || resolvedScope !== null || busy) return;
    setBusy(true);
    setActionError(null);
    try {
      await apiClient.denyTool(runId, requestId);
      setResolvedScope('deny');
    } catch (err) {
      setActionError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const handleCopy = () => {
    if (!url) return;
    void navigator.clipboard.writeText(url);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleRetryPreviewApproval = async () => {
    if (!runId || toolName !== 'start_preview' || busy || retryRequested) return;
    setBusy(true);
    setActionError(null);
    try {
      await apiClient.retryPreviewApproval(runId, requestId);
      setRetryRequested(true);
    } catch (err) {
      setActionError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  // Collapsed inline view — shown after action or when pre-resolved from reducer
  if (resolvedScope !== null) {
    let label: string;
    let color: string;
    if (resolvedScope === 'expired') {
      label = `This approval request expired \u00b7 ${toolName}`;
      color = tokens.colorNeutralForeground3;
    } else if (resolvedScope === 'deny') {
      label = `\u2717 Denied \u00b7 ${toolName}`;
      color = tokens.colorStatusDangerForeground1;
    } else {
      label = `\u2713 ${scopeLabel(resolvedScope)} \u00b7 ${toolName}`;
      color = tokens.colorStatusSuccessForeground1;
    }
    return (
      <div
        className={styles.rowS}
        style={{ padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}` }}
        role={resolvedScope === 'expired' ? 'status' : undefined}
        aria-live={resolvedScope === 'expired' ? 'polite' : undefined}
      >
        <span className={styles.statusRow}>
          <span className={styles.statusIcon}>
            {resolvedScope === 'deny' ? <DismissCircleFilled aria-hidden="true" /> : resolvedScope === 'expired' ? <ClockRegular aria-hidden="true" /> : <CheckmarkCircleFilled aria-hidden="true" />}
          </span>
          <Text as="span" style={{ color }}>{label}</Text>
          {resolvedScope === 'expired' && toolName === 'start_preview' && (
            <Button
              appearance="outline"
              size="small"
              disabled={busy || !runId || retryRequested}
              onClick={() => void handleRetryPreviewApproval()}
              aria-label="Retry expired preview approval"
            >
              {retryRequested ? 'Fresh approval requested' : busy ? 'Retrying' : 'Retry approval'}
            </Button>
          )}
        </span>
        {actionError && (
          <Text size={200} style={{ color: tokens.colorStatusDangerForeground1 }}>
            Retry failed: {actionError}
          </Text>
        )}
      </div>
    );
  }

  return (
    // SECURITY (Y-3): all values rendered as text — no HTML
    <div className={styles.approvalCardRedesign} role="alert">
      <div className={styles.approvalHeaderRedesign}>
        <ShieldLockRegular
          style={{ fontSize: '18px', color: tokens.colorStatusWarningForeground1 }}
          aria-hidden="true"
        />
        <Text weight="semibold" size={300} style={{ color: tokens.colorStatusWarningForeground1 }}>
          Tool Approval Required
        </Text>
      </div>

      <div className={styles.approvalBodyRedesign}>
        <div className={styles.rowS}>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>Tool</Text>
          <Badge appearance="filled" color="subtle" shape="rounded">
            {toolName}
          </Badge>
        </div>

        {url && (
          <div className={styles.approvalUrlRowRedesign}>
            <pre className={styles.approvalUrlBlockRedesign}>{url}</pre>
            <Tooltip content={copied ? 'Copied!' : 'Copy URL'} relationship="label">
              <Button
                appearance="subtle"
                size="small"
                icon={copied ? <CheckmarkRegular /> : <CopyRegular />}
                onClick={handleCopy}
                aria-label="Copy URL"
              />
            </Tooltip>
          </div>
        )}

        {intention && (
          <Text className={styles.approvalIntentionRedesign}>{intention}</Text>
        )}
        {expiresAt && (
          <Text
            size={200}
            role="status"
            aria-label={`Approval expires at ${new Date(expiresAt).toLocaleString()}`}
          >
            Approval expires {new Date(expiresAt).toLocaleString()}
          </Text>
        )}

        <div className={styles.approvalActionsRedesign}>
          <Button
            appearance="primary"
            size="small"
            disabled={busy || !runId}
            onClick={() => void handleAllow('once')}
          >
            Allow once
          </Button>
          <Tooltip
            content="Allows all calls to this tool for the rest of this orchestration session."
            relationship="description"
          >
            <Button
              appearance="outline"
              size="small"
              disabled={busy || !runId}
              onClick={() => void handleAllow('run')}
            >
              Allow for session
            </Button>
          </Tooltip>
          <Tooltip
            content="Allows all calls to this tool (any URL) for the rest of this run."
            relationship="description"
          >
            <Button
              appearance="outline"
              size="small"
              disabled={busy || !runId}
              onClick={() => void handleAllow('tool')}
            >
              Allow tool
            </Button>
          </Tooltip>
          <Tooltip
            content="Allows this eligible tool in future runs of this project. Other projects and tools still ask."
            relationship="description"
          >
            <Button
              appearance="outline"
              size="small"
              disabled={busy || !runId}
              onClick={() => void handleAllow('always')}
            >
              Always allow
            </Button>
          </Tooltip>
          <Button
            appearance="outline"
            size="small"
            disabled={busy || !runId}
            onClick={() => void handleDeny()}
            style={{
              marginLeft: 'auto',
              borderColor: tokens.colorStatusDangerBorder1,
              color: tokens.colorStatusDangerForeground1,
            }}
          >
            Deny
          </Button>
        </div>

        <Text className={styles.approvalRequestIdRedesign}>ID: {displayId}</Text>
        {actionError && (
          <Text size={200} style={{ color: tokens.colorStatusDangerForeground1 }}>
            Approval failed: {actionError}
          </Text>
        )}
      </div>
    </div>
  );
}

interface ShellApprovalCardProps {
  styles: ReturnType<typeof useStyles>;
  requestId: string;
  commandHash: string;
  command: string | null;
  runId?: string;
  isResolved?: boolean;
  resolvedOutcome?: 'approved' | 'denied' | null;
}

function ShellApprovalCard({ styles, requestId, commandHash, command, runId, isResolved, resolvedOutcome: resolvedOutcomeProp }: ShellApprovalCardProps) {
  const [resolvedOutcome, setResolvedOutcome] = useState<'approved' | 'denied' | null>(
    isResolved ? (resolvedOutcomeProp ?? 'approved') : null,
  );
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const handleApprove = async () => {
    if (!runId || resolvedOutcome !== null || busy) return;
    setBusy(true);
    setActionError(null);
    try {
      await apiClient.approveShell(runId, commandHash);
      setResolvedOutcome('approved');
    } catch (err) {
      setActionError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const handleDeny = async () => {
    if (!runId || resolvedOutcome !== null || busy) return;
    setBusy(true);
    setActionError(null);
    try {
      await apiClient.denyShell(runId, commandHash);
      setResolvedOutcome('denied');
    } catch (err) {
      setActionError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  if (resolvedOutcome !== null) {
    const label = resolvedOutcome === 'denied'
      ? `\u2717 Denied \u00b7 run_command`
      : `\u2713 Approved \u00b7 run_command`;
    return (
      <div className={styles.rowS} style={{ padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}` }}>
        <span className={styles.statusRow}>
          <span className={styles.statusIcon}>
            {resolvedOutcome === 'denied' ? <DismissCircleFilled aria-hidden="true" /> : <CheckmarkCircleFilled aria-hidden="true" />}
          </span>
          <Text as="span">{label}</Text>
        </span>
      </div>
    );
  }

  return (
    // SECURITY (Y-3): command and requestId rendered as text — no HTML
    <div className={styles.approvalCard} role="alert" data-testid="shell-approval-gate">
      <div className={styles.approvalHeading}>
        <WarningFilled className={styles.warningIcon} aria-hidden="true" />
        <Text weight="semibold">Dangerous command — approval required</Text>
      </div>
      {command && (
        <Text as="pre" className={styles.approvalCommand} style={{ margin: 0 }}>
          {command}
        </Text>
      )}
      <Text className={styles.approvalMeta}>Request ID: {requestId}</Text>
      <div className={styles.approvalActions}>
        <Button
          appearance="primary"
          size="small"
          disabled={busy || !runId}
          onClick={() => void handleApprove()}
        >
          Approve
        </Button>
        <Button
          appearance="outline"
          size="small"
          disabled={busy || !runId}
          onClick={() => void handleDeny()}
          style={{ borderColor: tokens.colorStatusDangerBorder1, color: tokens.colorStatusDangerForeground1 }}
        >
          Deny
        </Button>
      </div>
      {actionError && (
        <Text size={200} style={{ color: tokens.colorStatusDangerForeground1 }}>
          Approval failed: {actionError}
        </Text>
      )}
    </div>
  );
}

interface LifecycleEventCardProps {
  event: RunStreamEvent;
  runId?: string;
  isResolved?: boolean;
  resolvedScope?: string | null;
  runOutcome?: { achieved: boolean; reason: string };
}

export const LifecycleEventCard = memo(function LifecycleEventCard({ event, runId, isResolved, resolvedScope, runOutcome }: LifecycleEventCardProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

  // sandbox.warning is suppressed — shown inline only if genuinely blocking.
  if (event.type === 'sandbox.warning') {
    return null;
  }

  // --- agent.system_prompt: expandable full-text card ---
  if (event.type === 'agent.system_prompt') {
    const p = event.payload;
    const provider = String(p['provider'] ?? 'unknown');
    const prompt = p['prompt'] ? String(p['prompt']) : null;
    const note = p['note'] ? String(p['note']) : null;
    const preview = prompt
      ? prompt.slice(0, 120).replace(/\n/g, ' ') + `… (${prompt.length} chars)`
      : (note ?? '');
    return (
      <div className={styles.card} style={{ flexDirection: 'column', alignItems: 'flex-start', cursor: prompt ? 'pointer' : undefined }}
        onClick={prompt ? () => setExpanded(e => !e) : undefined}
        role={prompt ? 'button' : undefined}
        aria-expanded={prompt ? expanded : undefined}>
        <div className={styles.rowS} style={{ width: '100%' }}>
          {prompt && (expanded
            ? <ChevronDownRegular style={{ flexShrink: 0, fontSize: '10px', color: tokens.colorNeutralForeground3 }} aria-hidden="true" />
            : <ChevronRightRegular style={{ flexShrink: 0, fontSize: '10px', color: tokens.colorNeutralForeground3 }} aria-hidden="true" />)}
          <CodeRegular className={styles.subtleIcon} aria-hidden="true" />
          <Badge className={styles.badge} color="subtle" shape="rounded" size="small">system_prompt:{provider}</Badge>
          <Text className={styles.summary}>{preview}</Text>
        </div>
        {expanded && prompt && (
          <Text as="pre" style={{ margin: `${tokens.spacingVerticalS} 0 0 24px`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: tokens.colorNeutralForeground2 }}>
            {prompt}
          </Text>
        )}
      </div>
    );
  }

  // --- agent.task: expandable task prompt card ---
  if (event.type === 'agent.task') {
    const taskText = event.payload['task'] ? String(event.payload['task']) : null;
    const preview = taskText
      ? taskText.slice(0, 120).replace(/\n/g, ' ') + (taskText.length > 120 ? `… (${taskText.length} chars)` : '')
      : '';
    return (
      <div className={styles.card} style={{ flexDirection: 'column', alignItems: 'flex-start', cursor: taskText ? 'pointer' : undefined }}
        onClick={taskText ? () => setExpanded(e => !e) : undefined}
        role={taskText ? 'button' : undefined}
        aria-expanded={taskText ? expanded : undefined}>
        <div className={styles.rowS} style={{ width: '100%' }}>
          {taskText && (expanded
            ? <ChevronDownRegular style={{ flexShrink: 0, fontSize: '10px', color: tokens.colorNeutralForeground3 }} aria-hidden="true" />
            : <ChevronRightRegular style={{ flexShrink: 0, fontSize: '10px', color: tokens.colorNeutralForeground3 }} aria-hidden="true" />)}
          <TaskListSquareLtrRegular className={styles.subtleIcon} aria-hidden="true" />
          <Badge className={styles.badge} color="subtle" shape="rounded" size="small">task</Badge>
          <Text className={styles.summary}>{preview}</Text>
        </div>
        {expanded && taskText && (
          <Text as="pre" style={{ margin: `${tokens.spacingVerticalS} 0 0 24px`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: tokens.colorNeutralForeground2 }}>
            {taskText}
          </Text>
        )}
      </div>
    );
  }

  // --- terminal output line (tool.output) ---
  if (event.type === 'tool.output') {
    const stream = String(event.payload['stream'] ?? 'stdout');
    const content = String(event.payload['content'] ?? '');
    const isStderr = stream === 'stderr';
    return (
      // SECURITY (Y-3): content rendered as text — no HTML
      <div
        className={mergeClasses(styles.terminalLine, isStderr && styles.terminalStderrLine)}
        role="log"
        aria-label={`${stream} output`}
      >
        <Text
          as="span"
          className={`${styles.streamPrefix}${isStderr ? ` ${styles.stderrPrefix}` : ''}`}
        >
          [{stream}]
        </Text>
        <Text
          as="pre"
          className={`${styles.terminalContent}${isStderr ? ` ${styles.stderrContent}` : ''}`}
          style={{ margin: 0, fontFamily: 'inherit', fontSize: 'inherit' }}
        >
          {content}
        </Text>
      </div>
    );
  }

  // --- exit code (tool.exec_result) ---
  if (event.type === 'tool.exec_result') {
    const exitCode = Number(event.payload['exitCode'] ?? event.payload['exit_code'] ?? 0);
    const isFailure = exitCode !== 0;
    return (
      <div className={styles.card}>
        <CodeRegular className={styles.subtleIcon} aria-hidden="true" />
        <Badge
          className={styles.badge}
          color={isFailure ? 'danger' : 'success'}
          shape="rounded"
          size="small"
        >
          Exit code: {exitCode}
        </Badge>
      </div>
    );
  }

  // --- shell approval required ---
  if (event.type === 'shell.approval_required') {
    const requestId = payloadString(event.payload, ['requestId', 'request_id']) ?? '';
    const commandHash = payloadString(event.payload, ['commandHash', 'command_hash']) ?? '';
    const command = event.payload['command'] ? String(event.payload['command']) : null;
    const approvalRunId = payloadString(event.payload, ['childRunId', 'child_run_id']) ?? runId;
    return (
      <ShellApprovalCard
        styles={styles}
        requestId={requestId}
        commandHash={commandHash}
        command={command}
        runId={approvalRunId}
      />
    );
  }

  // --- tool approval required ---
  if (event.type === 'tool.approval_required' || event.type === 'coordinator.child_approval_required') {
    const requestId = payloadString(event.payload, ['requestId', 'request_id']) ?? '';
    const displayId = event.payload['displayId'] ? String(event.payload['displayId']) : requestId.slice(0, 8);
    const toolName = payloadString(event.payload, ['toolName', 'tool_name']) ?? 'unknown';
    const rawUrl = event.payload['url'] ? String(event.payload['url']) : null;
    const url = rawUrl && rawUrl.length > 80 ? rawUrl.slice(0, 80) + '...' : rawUrl;
    const intention = payloadString(event.payload, ['intention', 'message']) ?? null;
    const expiresAt = payloadString(event.payload, ['expiresAt', 'expires_at']) ?? null;
    const approvalRunId = payloadString(event.payload, ['childRunId', 'child_run_id']) ?? runId;

    return (
      <ToolApprovalCard
        styles={styles}
        requestId={requestId}
        displayId={displayId}
        toolName={toolName}
        url={url}
        intention={intention}
        expiresAt={expiresAt}
        runId={approvalRunId}
        isResolved={isResolved}
        resolvedScope={resolvedScope}
      />
    );
  }

  // --- agent.intent: inline intent step (text only, no HTML — Y-3) ---
  if (event.type === 'agent.intent') {
    const intent = event.payload['intent'] ? String(event.payload['intent']) : '';
    if (!intent) return null;
    // SECURITY (Y-3): intent rendered as escaped text — no HTML interpretation.
    // Styled to match the muted "Used N tools" cluster header, per user request.
    return (
      <Text size={100} className={styles.intentAnnotation}>{intent}</Text>
    );
  }

  // --- tool.auto_approved: muted audit line — a tool HITL was auto-granted by the run option.
  // Rendered with the same muted treatment as agent.intent (not a prominent action card).
  if (event.type === 'tool.auto_approved') {
    const p = event.payload;
    const toolName = String(p['toolName'] ?? p['tool_name'] ?? 'unknown');
    const rawUrl = p['url'] ? String(p['url']) : '';
    const url = rawUrl.length > 80 ? rawUrl.slice(0, 80) + '…' : rawUrl;
    return (
      <Text size={100} className={styles.intentAnnotation}>
        Tool auto-approved: {toolName}{url ? ` ${url}` : ''}
      </Text>
    );
  }

  // --- coordinator.autopilot_answered: muted audit line — Autopilot auto-answered a (child)
  // question via the coordinator model. Notes the child/subtask when childRunId is present.
  if (event.type === 'coordinator.autopilot_answered') {
    const p = event.payload;
    const question = String(p['question'] ?? '');
    const answer = String(p['answer'] ?? '');
    const childRunId = p['childRunId'] ?? p['child_run_id'];
    const child = childRunId ? ` (child ${String(childRunId).slice(0, 8)})` : '';
    return (
      <Text size={100} className={styles.intentAnnotation}>
        Autopilot answered{child}: {question} → {answer}
      </Text>
    );
  }

  // --- agent.tools: separate flat card listing registered tools ---
  if (event.type === 'agent.tools') {
    const tools = event.payload['tools'] as string[] | undefined;
    if (!tools || tools.length === 0) return null;
    return (
      <div className={styles.card} style={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <CodeRegular className={styles.subtleIcon} aria-hidden="true" style={{ marginTop: '2px' }} />
        <Badge className={styles.badge} color="subtle" shape="rounded" size="small">tools</Badge>
        <div className={styles.rowWrap}>
          {tools.map(tool => (
            <Badge key={tool} appearance="outline" size="small" shape="rounded" style={{ fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase100 }}>{tool}</Badge>
          ))}
        </div>
      </div>
    );
  }

  // --- review.changes_requested: show label + optional comment blockquote ---
  if (event.type === 'review.changes_requested') {
    const comment = event.payload['comment'] ? String(event.payload['comment']) : null;
    return (
      <div className={styles.card} style={{ flexDirection: 'column', alignItems: 'flex-start' }}>
        <div className={styles.rowS} style={{ width: '100%' }}>
          <span className={styles.warningIcon}><WarningFilled aria-hidden="true" /></span>
          <Badge className={styles.badge} color="warning" shape="rounded" size="small">review.changes_requested</Badge>
          <Text className={styles.summary}>Changes requested</Text>
        </div>
        {comment && (
          <Text as="p" className={styles.changesRequestedComment}>{comment}</Text>
        )}
      </div>
    );
  }

  // --- default card layout ---
  const { icon, label, summary, badgeColor } = lifecycleProps(event, runOutcome);

  const iconClass =
    badgeColor === 'success' ? styles.successIcon :
    badgeColor === 'danger' ? styles.errorIcon :
    badgeColor === 'warning' ? styles.warningIcon :
    styles.subtleIcon;

  return (
    <div className={styles.card}>
      {icon && <span className={mergeClasses(styles.statusIcon, iconClass)} aria-hidden="true">{icon}</span>}
      <Badge className={styles.badge} color={badgeColor} shape="rounded" size="small">
        {label}
      </Badge>
      {/* SECURITY (Y-3): summary rendered as escaped text — no HTML interpretation */}
      <Text className={styles.summary}>{summary}</Text>
    </div>
  );
});
