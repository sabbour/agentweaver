import { memo, useState } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { ChevronDownRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { AgentMessageBubble } from './AgentMessageBubble';
import { ToolCallCard } from './ToolCallCard';
import { LifecycleEventCard } from './LifecycleEventCard';
import type { TurnGroupItem, TurnStep, ToolCallItem, ApprovalRequestItem, AgentMessageItem } from '../timeline/types';
import type { StreamStatus } from '../api/sse';

const useStyles = makeStyles({
  steps: {
    paddingLeft: tokens.spacingHorizontalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  toolsHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    cursor: 'pointer',
    background: 'none',
    border: 'none',
    padding: '1px 0',
    textAlign: 'left',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    ':hover': { opacity: 0.75 },
  },
  /** Button style for a report_intent line acting as a cluster toggle header. */
  intentToggleHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    cursor: 'pointer',
    background: 'none',
    border: 'none',
    padding: '1px 0',
    textAlign: 'left',
    color: tokens.colorNeutralForeground3,
    ':hover': { opacity: 0.75 },
  },
  chevron: {
    color: tokens.colorNeutralForeground4,
    fontSize: tokens.fontSizeBase100,
    flexShrink: 0,
  },
  toolsList: {
    paddingLeft: tokens.spacingHorizontalM,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  intentAnnotation: {
    padding: '1px 0',
    color: tokens.colorNeutralForeground3,
  },
});

/** A non-tool step rendered inline (agent message or report_intent). */
type InlineStep = { kind: 'inline'; step: TurnStep };
/** A consecutive cluster of tool calls (excluding report_intent). */
type ToolCluster = { kind: 'cluster'; steps: ToolCallItem[]; clusterIndex: number };
type Cluster = InlineStep | ToolCluster;

/** Split ordered steps into alternating inline/cluster segments. */
function splitClusters(steps: TurnStep[]): Cluster[] {
  const result: Cluster[] = [];
  let clusterIndex = 0;
  let i = 0;
  while (i < steps.length) {
    const step = steps[i];
    if (step.kind === 'tool-call' && step.toolName !== 'report_intent') {
      // Collect consecutive tool-call steps (excluding report_intent)
      const cluster: ToolCallItem[] = [];
      while (i < steps.length) {
        const s = steps[i];
        if (s.kind === 'tool-call' && s.toolName !== 'report_intent') {
          cluster.push(s);
          i++;
        } else {
          break;
        }
      }
      result.push({ kind: 'cluster', steps: cluster, clusterIndex: clusterIndex++ });
    } else {
      result.push({ kind: 'inline', step });
      i++;
    }
  }
  return result;
}

/** Returns true if any step in the cluster has an error or a non-zero exit code. */
function hasClusterErrors(cluster: ToolCluster): boolean {
  return cluster.steps.some(s =>
    s.error != null ||
    (s.result?.content?.match(/^exit_code:\s*(-?\d+)/m)?.[1] !== undefined &&
     s.result.content.match(/^exit_code:\s*(-?\d+)/m)?.[1] !== '0')
  );
}

interface ToolClusterRowProps {
  cluster: ToolCluster;
  defaultExpanded: boolean;
  streamStatus: StreamStatus;
}

const ToolClusterRow = memo(function ToolClusterRow({ cluster, defaultExpanded, streamStatus }: ToolClusterRowProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(defaultExpanded);
  const n = cluster.steps.length;

  return (
    <>
      <button
        className={styles.toolsHeader}
        onClick={() => setExpanded(e => !e)}
        aria-expanded={expanded}
      >
        {expanded
          ? <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
          : <ChevronRightRegular className={styles.chevron} aria-hidden="true" />}
        <Text size={100} style={{ color: 'inherit' }}>
          Used {n} tool{n === 1 ? '' : 's'}
        </Text>
      </button>
      {expanded && (
        <div className={styles.toolsList}>
          {cluster.steps.map((step, i) => (
            <ToolCallCard
              key={step.callId != null ? String(step.callId) : "tc-" + i}
              item={step}
              streamStatus={streamStatus}
            />
          ))}
        </div>
      )}
    </>
  );
});

interface HeaderedClusterRowProps {
  /** The inline step (agent-message or report_intent) that acts as the toggle header. */
  headerStep: TurnStep;
  cluster: ToolCluster;
  defaultExpanded: boolean;
  streamStatus: StreamStatus;
  isLiveRun: boolean;
}

/**
 * Renders an inline step (report_intent annotation or agent-message bubble) as a
 * clickable collapse/expand toggle for the tool cluster that immediately follows it.
 *
 * - report_intent: the intent text itself becomes the toggle button (chevron inline).
 * - agent-message: the bubble renders as-is; a compact "Used N tools" row below it
 *   serves as the toggle affordance.
 */
const HeaderedClusterRow = memo(function HeaderedClusterRow({
  headerStep,
  cluster,
  defaultExpanded,
  streamStatus,
  isLiveRun,
}: HeaderedClusterRowProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(defaultExpanded);
  const toggle = () => setExpanded(e => !e);
  const n = cluster.steps.length;

  const chevron = expanded
    ? <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
    : <ChevronRightRegular className={styles.chevron} aria-hidden="true" />;

  let headerNode: React.ReactNode;

  if (headerStep.kind === 'tool-call' && (headerStep as ToolCallItem).toolName === 'report_intent') {
    // report_intent: the compact intent text IS the toggle button.
    headerNode = (
      <button
        className={styles.intentToggleHeader}
        onClick={toggle}
        aria-expanded={expanded}
      >
        {chevron}
        <Text size={100} style={{ color: 'inherit' }}>
          {(headerStep as ToolCallItem).humanTitle}
        </Text>
      </button>
    );
  } else {
    // agent-message (or other inline): render the content normally, then a compact
    // toggle row immediately below it.
    const msg = headerStep as AgentMessageItem;
    headerNode = (
      <>
        <AgentMessageBubble
          content={msg.content}
          streaming={msg.streaming}
          isLiveRun={isLiveRun}
        />
        <button
          className={styles.toolsHeader}
          onClick={toggle}
          aria-expanded={expanded}
        >
          {chevron}
          <Text size={100} style={{ color: 'inherit' }}>
            Used {n} tool{n === 1 ? '' : 's'}
          </Text>
        </button>
      </>
    );
  }

  return (
    <>
      {headerNode}
      {expanded && (
        <div className={styles.toolsList}>
          {cluster.steps.map((step, i) => (
            <ToolCallCard
              key={step.callId != null ? String(step.callId) : 'tc-' + i}
              item={step}
              streamStatus={streamStatus}
            />
          ))}
        </div>
      )}
    </>
  );
});

interface TurnGroupProps {
  item: TurnGroupItem;
  isLiveRun: boolean;
  streamStatus: StreamStatus;
  runId?: string;
}

export const TurnGroup = memo(function TurnGroup({ item, isLiveRun, streamStatus, runId }: TurnGroupProps) {
  const styles = useStyles();

  if (item.steps.length === 0 && item.active === false) {
    return null;
  }

  const clusters = splitClusters(item.steps);
  const isComplete = !item.active;

  return (
    <div className={styles.steps}>
      {clusters.map((cluster, i) => {
        if (cluster.kind === 'inline') {
          const step = cluster.step;
          const nextItem = clusters[i + 1];

          // Completed turn: if this non-approval inline step immediately precedes a tool
          // cluster, use it as the collapsible toggle header for that cluster.
          if (isComplete && nextItem?.kind === 'cluster' && step.kind !== 'approval-request') {
            return (
              <HeaderedClusterRow
                key={'hc-' + i}
                headerStep={step}
                cluster={nextItem}
                defaultExpanded={hasClusterErrors(nextItem)}
                streamStatus={streamStatus}
                isLiveRun={isLiveRun}
              />
            );
          }

          // Normal inline rendering
          if (step.kind === 'agent-message') {
            return (
              <AgentMessageBubble
                key={step.messageId != null ? String(step.messageId) : "msg-" + i}
                content={step.content}
                streaming={step.streaming}
                isLiveRun={isLiveRun}
              />
            );
          }
          if (step.kind === 'approval-request') {
            const aprStep = step as ApprovalRequestItem;
            return (
              <LifecycleEventCard
                key={`approval-${aprStep.requestId}`}
                event={{
                  sequence: -1,
                  type: 'tool.approval_required',
                  payload: {
                    request_id: aprStep.requestId,
                    tool_name: aprStep.toolName,
                    url: aprStep.url ?? '',
                  },
                }}
                runId={runId}
                isResolved={aprStep.resolved}
                resolvedScope={aprStep.resolvedScope}
              />
            );
          }
          // report_intent — compact system annotation
          if ((step as ToolCallItem).toolName === 'report_intent') {
            return (
              <Text
                key={(step as ToolCallItem).callId != null ? String((step as ToolCallItem).callId) : "ri-" + i}
                size={100}
                className={styles.intentAnnotation}
              >
                {(step as ToolCallItem).humanTitle}
              </Text>
            );
          }
          // Fallback for any other inline tool-call step
          return (
            <ToolCallCard
              key={(step as ToolCallItem).callId != null ? String((step as ToolCallItem).callId) : "ri-" + i}
              item={step as ToolCallItem}
              streamStatus={streamStatus}
            />
          );
        }

        // Tool cluster — check if the preceding inline step already rendered it as a
        // HeaderedClusterRow (in which case we skip here to avoid double-rendering).
        const prevItem = clusters[i - 1];
        if (isComplete && prevItem?.kind === 'inline' && prevItem.step.kind !== 'approval-request') {
          return null;
        }

        // Completed cluster with no inline header: use the "Used N tools" fallback row.
        if (isComplete) {
          return (
            <ToolClusterRow
              key={"cluster-" + i + "-done"}
              cluster={cluster}
              defaultExpanded={hasClusterErrors(cluster)}
              streamStatus={streamStatus}
            />
          );
        }

        // Active turn: show tool calls inline (always expanded; users see streaming progress).
        return (
          <div key={"cluster-" + i} className={styles.toolsList}>
            {cluster.steps.map((step, j) => (
              <ToolCallCard
                key={step.callId != null ? String(step.callId) : "tc-" + j}
                item={step}
                streamStatus={streamStatus}
              />
            ))}
          </div>
        );
      })}
    </div>
  );
});