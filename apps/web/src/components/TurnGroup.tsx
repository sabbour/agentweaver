import { memo, useState } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { ChevronDownRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { AgentMessageBubble } from './AgentMessageBubble';
import { ToolCallCard } from './ToolCallCard';
import { LifecycleEventCard } from './LifecycleEventCard';
import type { TurnGroupItem, TurnStep, ToolCallItem, ApprovalRequestItem } from '../timeline/types';
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
          // report_intent — show ⚠ if the immediately following tool cluster has failures
          const nextCluster = clusters[i + 1];
          const hasFollowingErrors =
            (step as ToolCallItem).toolName === 'report_intent' &&
            nextCluster?.kind === 'cluster' &&
            nextCluster.steps.some(s => s.error != null);
          return (
            <ToolCallCard
              key={(step as ToolCallItem).callId != null ? String((step as ToolCallItem).callId) : "ri-" + i}
              item={step as ToolCallItem}
              streamStatus={streamStatus}
              hasFollowingErrors={hasFollowingErrors}
            />
          );
        }

        // Tool cluster — collapse when turn is complete, expand when active
        // Auto-expand if any step has an error or non-zero exit code
        if (isComplete) {
          const hasErrors = cluster.steps.some(s =>
            s.error != null ||
            (s.result?.content?.match(/^exit_code:\s*(-?\d+)/m)?.[1] !== undefined &&
             s.result.content.match(/^exit_code:\s*(-?\d+)/m)?.[1] !== '0')
          );
          return (
            <ToolClusterRow
              key={"cluster-" + i + (isComplete ? "-done" : "-live")}
              cluster={cluster}
              defaultExpanded={hasErrors}
              streamStatus={streamStatus}
            />
          );
        }

        // Active turn: show tool calls inline
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