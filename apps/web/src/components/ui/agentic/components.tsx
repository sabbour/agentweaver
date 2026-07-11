import type { ReactNode } from 'react';
import { useState } from 'react';
import {
  Button,
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
    return <span className={styles.runningDot} aria-label="Running" />;
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
}

export function AgentStepItem({ step, onApprove, onDeny }: AgentStepProps) {
  const styles = useAgenticStyles();
  const autoOpen =
    step.defaultOpen !== undefined
      ? step.defaultOpen
      : step.needsInput === true || step.status === 'running';
  const [open, setOpen] = useState(autoOpen);
  const hasContent = Boolean(step.body || step.needsInput || (step.artifacts && step.artifacts.length > 0));
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
        <Text className={styles.stepTitle}>{step.title}</Text>
        <Text className={styles.stepStatusLabel} aria-live="polite">
          {statusLabel(step)}
        </Text>
        {hasContent && (
          <ChevronRightRegular
            className={mergeClasses(styles.stepChevron, open && styles.stepChevronOpen)}
            aria-hidden="true"
          />
        )}
      </div>

      {hasContent && open && (
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
  'aria-label'?: string;
}

export function AgentStepList({ steps, onApprove, onDeny, className, 'aria-label': ariaLabel }: AgentStepListProps) {
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
        />
      ))}
    </ol>
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
