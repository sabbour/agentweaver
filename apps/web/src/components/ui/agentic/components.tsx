import type { ReactNode } from 'react';
import { useState } from 'react';
import {
  Button,
  Spinner,
  Text,
  mergeClasses,
} from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  ChevronRightRegular,
  CircleRegular,
  DocumentRegular,
  ErrorCircleRegular,
  OpenRegular,
  WarningRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { useAgenticStyles } from './styles';
import type { AgentArtifact, AgentStep, ToolCall } from './types';

// ─── ArtifactChip ────────────────────────────────────────────────────────────

export interface ArtifactChipProps {
  artifact: AgentArtifact;
  className?: string;
}

export function ArtifactChip({ artifact, className }: ArtifactChipProps) {
  const styles = useAgenticStyles();
  return (
    <button
      type="button"
      className={mergeClasses(styles.artifactChip, className)}
      onClick={artifact.onOpen}
      aria-label={`Open ${typeof artifact.title === 'string' ? artifact.title : 'artifact'}`}
      disabled={!artifact.onOpen}
    >
      <span className={styles.artifactChipIcon} aria-hidden="true">
        {artifact.icon ?? <DocumentRegular />}
      </span>
      <span className={styles.artifactChipTitle}>{artifact.title}</span>
      {artifact.type && (
        <span className={styles.artifactChipType}>{artifact.type}</span>
      )}
    </button>
  );
}

// ─── ApprovalGate ─────────────────────────────────────────────────────────────

export interface ApprovalGateProps {
  stepId: string;
  riskText?: ReactNode;
  body?: ReactNode;
  disclaimer?: ReactNode;
  approveLabel?: ReactNode;
  denyLabel?: ReactNode;
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
}

export function ApprovalGate({
  stepId,
  riskText,
  body,
  disclaimer,
  approveLabel = 'Approve',
  denyLabel = 'Deny',
  onApprove,
  onDeny,
}: ApprovalGateProps) {
  const styles = useAgenticStyles();
  return (
    <div className={styles.approvalGate} role="region" aria-label="Approval required">
      {body && (
        <Text className={styles.approvalRiskText}>{body}</Text>
      )}
      {riskText && (
        <Text className={styles.approvalRiskText}>{riskText}</Text>
      )}
      {disclaimer && (
        <Text className={styles.approvalDisclaimer}>{disclaimer}</Text>
      )}
      <div className={styles.approvalActions}>
        <Button appearance="primary" onClick={() => onApprove?.(stepId)}>
          {approveLabel}
        </Button>
        <Button appearance="secondary" onClick={() => onDeny?.(stepId)}>
          {denyLabel}
        </Button>
      </div>
    </div>
  );
}

// ─── AgentStep ────────────────────────────────────────────────────────────────

function StatusIcon({ step, styles }: { step: AgentStep; styles: ReturnType<typeof useAgenticStyles> }) {
  if (step.status === 'complete') {
    return <CheckmarkCircleRegular className={styles.statusComplete} aria-label="Complete" />;
  }
  if (step.status === 'warning' || step.needsInput) {
    return <WarningRegular className={styles.statusWarning} aria-label="Needs attention" />;
  }
  if (step.status === 'blocked') {
    return <ErrorCircleRegular className={styles.statusDanger} aria-label="Blocked" />;
  }
  if (step.status === 'running') {
    return <Spinner size="extra-tiny" aria-label="Running" />;
  }
  return <CircleRegular className={styles.statusPending} aria-label="Pending" />;
}

function statusLabel(step: AgentStep): string {
  if (step.needsInput) return 'Needs input';
  if (step.status === 'complete') return 'Complete';
  if (step.status === 'warning') return 'Warning';
  if (step.status === 'blocked') return 'Blocked';
  if (step.status === 'running') return 'Running';
  return 'Pending';
}

export interface AgentStepProps {
  step: AgentStep;
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
  /** When true, forces the step (and its descendants) open regardless of local toggle state. */
  forceOpen?: boolean;
}

export function AgentStepItem({ step, onApprove, onDeny, forceOpen = false }: AgentStepProps) {
  const styles = useAgenticStyles();
  const autoOpen =
    step.defaultOpen !== undefined
      ? step.defaultOpen
      : step.needsInput === true || step.status === 'running';
  const [open, setOpen] = useState(autoOpen);
  const isOpen = forceOpen || open;
  const hasChildren = Boolean(step.children && step.children.length > 0);
  const hasContent = Boolean(step.body || step.needsInput || (step.artifacts && step.artifacts.length > 0) || hasChildren);
  const headerId = `agent-step-header-${step.id}`;
  const panelId = `agent-step-panel-${step.id}`;

  return (
    <li className={styles.stepItem} data-status={step.status ?? 'pending'}>
      <div className={styles.stepIconSlot} aria-hidden="true">
        <StatusIcon step={step} styles={styles} />
      </div>
      <div
        className={styles.stepHeader}
        id={headerId}
        role={hasContent ? 'button' : undefined}
        tabIndex={hasContent ? 0 : undefined}
        aria-expanded={hasContent ? isOpen : undefined}
        aria-controls={hasContent ? panelId : undefined}
        onClick={() => hasContent && setOpen((v) => !v)}
        onKeyDown={(e) => {
          if (hasContent && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            setOpen((v) => !v);
          }
        }}
      >
        <span className={styles.stepTitleWrap}>
          <Text className={styles.stepTitle}>{step.title}</Text>
          {step.statusBadge && (
            <span className={styles.stepBadge}>{step.statusBadge}</span>
          )}
        </span>
        <Text className={styles.stepStatusLabel} aria-live="polite">
          {statusLabel(step)}
        </Text>
        {hasContent && (
          <ChevronRightRegular
            className={mergeClasses(styles.stepChevron, isOpen && styles.stepChevronOpen)}
            aria-hidden="true"
          />
        )}
      </div>

      {hasContent && isOpen && (
        <div id={panelId} className={styles.stepPanel} role="region" aria-labelledby={headerId}>
          {step.body && !step.needsInput && (
            <Text className={styles.stepBody}>{step.body}</Text>
          )}
          {step.needsInput && (
            <ApprovalGate
              stepId={step.id}
              body={step.body}
              riskText={step.riskText}
              disclaimer={step.disclaimer}
              approveLabel={step.approveLabel}
              denyLabel={step.denyLabel}
              onApprove={onApprove}
              onDeny={onDeny}
            />
          )}
          {step.artifacts && step.artifacts.length > 0 && (
            <div className={styles.stepArtifacts} aria-label="Artifacts">
              {step.artifacts.map((artifact) => (
                <ArtifactChip key={artifact.id} artifact={artifact} />
              ))}
            </div>
          )}
          {/* Nested children — indented sub-tree */}
          {hasChildren && (
            <ol className={styles.stepChildrenList} aria-label={`Sub-steps of ${typeof step.title === 'string' ? step.title : 'step'}`}>
              {step.children!.map((child) => (
                <AgentStepItem
                  key={child.id}
                  step={child}
                  onApprove={onApprove}
                  onDeny={onDeny}
                  forceOpen={forceOpen}
                />
              ))}
            </ol>
          )}
        </div>
      )}
    </li>
  );
}

// ─── AgentStepList ────────────────────────────────────────────────────────────

export interface AgentStepListProps {
  steps: AgentStep[];
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
  className?: string;
  forceOpen?: boolean;
  'aria-label'?: string;
}

export function AgentStepList({ steps, onApprove, onDeny, className, forceOpen = false, 'aria-label': ariaLabel }: AgentStepListProps) {
  const styles = useAgenticStyles();
  return (
    <ol
      className={mergeClasses(styles.stepList, className)}
      aria-label={ariaLabel ?? 'Agent steps'}
    >
      {steps.map((step) => (
        <AgentStepItem
          key={step.id}
          step={step}
          onApprove={onApprove}
          onDeny={onDeny}
          forceOpen={forceOpen}
        />
      ))}
    </ol>
  );
}

// ─── AgentActivitySession ─────────────────────────────────────────────────────
// A "Run activity" panel: a titled, collapsible session header + an
// "{N} actions completed · Show all" summary line above a nested AgentStepList.

function countCompleted(steps: AgentStep[]): number {
  let total = 0;
  for (const step of steps) {
    if (step.status === 'complete') total += 1;
    if (step.children && step.children.length > 0) total += countCompleted(step.children);
  }
  return total;
}

export interface AgentActivitySessionProps {
  steps: AgentStep[];
  title?: ReactNode;
  /** Muted subline, e.g. "42 updates captured". */
  updatesLabel?: ReactNode;
  /** Overrides the auto-derived completed-actions count in the summary line. */
  completedCount?: number;
  onApprove?: (stepId: string) => void;
  onDeny?: (stepId: string) => void;
  defaultCollapsed?: boolean;
  className?: string;
  'aria-label'?: string;
}

export function AgentActivitySession({
  steps,
  title = 'Run activity',
  updatesLabel,
  completedCount,
  onApprove,
  onDeny,
  defaultCollapsed = false,
  className,
  'aria-label': ariaLabel,
}: AgentActivitySessionProps) {
  const styles = useAgenticStyles();
  const [collapsed, setCollapsed] = useState(defaultCollapsed);
  const [showAll, setShowAll] = useState(false);
  const completed = completedCount ?? countCompleted(steps);

  return (
    <section className={mergeClasses(styles.activitySession, className)} aria-label={ariaLabel ?? 'Run activity'}>
      <header className={styles.activityHeader}>
        <div className={styles.activityHeaderTitles}>
          <Text className={styles.activityTitle}>{title}</Text>
          {updatesLabel && <Text className={styles.activitySubline}>{updatesLabel}</Text>}
        </div>
        <Button
          appearance="subtle"
          size="small"
          onClick={() => setCollapsed((v) => !v)}
          aria-expanded={!collapsed}
        >
          {collapsed ? 'Show activity' : 'Hide activity'}
        </Button>
      </header>

      {!collapsed && (
        <>
          <button
            type="button"
            className={styles.activitySummary}
            onClick={() => setShowAll((v) => !v)}
            aria-expanded={showAll}
          >
            <Text className={styles.activitySummaryText}>
              {completed} action{completed === 1 ? '' : 's'} completed
            </Text>
            <span className={styles.activitySummaryDivider} aria-hidden="true">·</span>
            <span className={styles.activitySummaryAction}>
              {showAll ? 'Collapse all' : 'Show all'}
            </span>
            <ChevronRightRegular
              className={mergeClasses(styles.stepChevron, showAll && styles.stepChevronOpen)}
              aria-hidden="true"
            />
          </button>
          <AgentStepList
            steps={steps}
            onApprove={onApprove}
            onDeny={onDeny}
            forceOpen={showAll}
            aria-label={ariaLabel ?? 'Run activity steps'}
          />
        </>
      )}
    </section>
  );
}

// ─── ToolCallRow ──────────────────────────────────────────────────────────────

export interface ToolCallRowProps {
  toolCall: ToolCall;
  className?: string;
}

export function ToolCallRow({ toolCall, className }: ToolCallRowProps) {
  const styles = useAgenticStyles();
  const [open, setOpen] = useState(toolCall.status === 'running');
  const hasContent = Boolean(
    toolCall.inputSummary ||
      toolCall.resultSummary ||
      (toolCall.artifacts && toolCall.artifacts.length > 0),
  );
  const headerId = `tool-call-header-${toolCall.id}`;
  const panelId = `tool-call-panel-${toolCall.id}`;

  const statusIcon =
    toolCall.status === 'complete' ? (
      <CheckmarkCircleRegular className={mergeClasses(styles.toolCallStatusIcon, styles.statusComplete)} aria-hidden="true" />
    ) : toolCall.status === 'error' ? (
      <ErrorCircleRegular className={mergeClasses(styles.toolCallStatusIcon, styles.statusDanger)} aria-hidden="true" />
    ) : toolCall.status === 'running' ? (
      <span className={styles.runningDot} aria-hidden="true" />
    ) : (
      <WrenchRegular className={mergeClasses(styles.toolCallStatusIcon, styles.statusPending)} aria-hidden="true" />
    );

  return (
    <div className={mergeClasses(styles.toolCallRow, className)} data-status={toolCall.status ?? 'complete'}>
      <div
        className={styles.toolCallHeader}
        id={headerId}
        role={hasContent ? 'button' : undefined}
        tabIndex={hasContent ? 0 : undefined}
        aria-expanded={hasContent ? open : undefined}
        aria-controls={hasContent ? panelId : undefined}
        onClick={() => hasContent && setOpen((v) => !v)}
        onKeyDown={(e) => {
          if (hasContent && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            setOpen((v) => !v);
          }
        }}
      >
        {statusIcon}
        <span className={styles.toolCallName}>{toolCall.name}</span>
        {hasContent && (
          <OpenRegular
            className={mergeClasses(styles.toolCallStatusIcon, styles.statusPending)}
            aria-hidden="true"
            style={{ marginLeft: 'auto' }}
          />
        )}
      </div>

      {hasContent && open && (
        <div id={panelId} role="region" aria-labelledby={headerId}>
          {toolCall.inputSummary && (
            <Text className={styles.toolCallSummary} block>
              {toolCall.inputSummary}
            </Text>
          )}
          {toolCall.resultSummary && (
            <Text className={styles.toolCallSummary} block>
              {toolCall.resultSummary}
            </Text>
          )}
          {toolCall.artifacts && toolCall.artifacts.length > 0 && (
            <div className={styles.toolCallArtifacts} aria-label="Tool artifacts">
              {toolCall.artifacts.map((artifact) => (
                <ArtifactChip key={artifact.id} artifact={artifact} />
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
