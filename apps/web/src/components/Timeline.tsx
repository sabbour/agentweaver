import {
  Spinner,
  Text } from '@fluentui/react-components';
import { LifecycleEventCard } from './LifecycleEventCard';
import { QuestionAnswerCard } from './QuestionAnswerCard';
import { TurnGroup } from './TurnGroup';
import { WorkflowStepCard } from './WorkflowStepCard';
import { makeStyles,
  tokens,
} from '@fluentui/react-components';
import { memo, useMemo } from 'react';
import type { StreamStatus } from '../api/sse';
import type { TimelineItem } from '../timeline/types';
const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  connecting: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    fontSize: tokens.fontSizeBase200,
    paddingTop: tokens.spacingVerticalS,
    color: tokens.colorNeutralForeground3,
  },
  skipped: {
    display: 'block',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusSmall,
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
  },
});

interface TimelineProps {
  items: TimelineItem[];
  streamStatus: StreamStatus;
  isLiveRun: boolean;
  runId?: string;
  runOutcome?: { achieved: boolean; reason: string };
  skippedEventCount?: number;
}

export const Timeline = memo(function Timeline({ items, streamStatus, isLiveRun, runId, runOutcome, skippedEventCount = 0 }: TimelineProps) {
  const styles = useStyles();

  // Pair inline approval cards (tool / shell / child approvals rendered as lifecycle
  // items) with their resolution event so a resolved gate disables immediately. The
  // reducer already pairs approvals that live inside an open turn; this covers the
  // lifecycle-fallback path AND bubbled coordinator child approvals.
  const resolvedApprovals = useMemo(() => {
    const map = new Map<string, string>();
    for (const item of items) {
      if (item.kind !== 'lifecycle') continue;
      const t = item.event.type;
      if (t === 'tool.approval_resolved' || t === 'coordinator.child_approval_resolved') {
        const p = item.event.payload;
        const requestId = String(p['requestId'] ?? p['request_id'] ?? '');
        if (!requestId) continue;
        const scope = p['expired'] ? 'expired' : p['approved'] ? String(p['scope'] ?? 'once') : 'deny';
        map.set(requestId, scope);
      }
    }
    return map;
  }, [items]);

  return (
    // role="log" announces new items; aria-live="polite" only when live (fix #6)
    <div
      className={styles.root}
      role="log"
      aria-label="Run timeline"
      aria-live={isLiveRun ? 'polite' : undefined}
    >
      {skippedEventCount > 0 && (
        <Text className={styles.skipped}>
          {skippedEventCount} older events not shown.
        </Text>
      )}
      {items.map((item, i) => {
        if (item.kind === 'turn-group') {
          return (
            <TurnGroup
              key={item.turnId != null ? String(item.turnId) : `turn-${i}`}
              item={item}
              isLiveRun={isLiveRun}
              streamStatus={streamStatus}
              runId={runId}
            />
          );
        }
        if (item.kind === 'workflow_step') {
          return (
            <WorkflowStepCard
              key={`ws-${item.step}-${i}`}
              item={item}
            />
          );
        }
        if (item.kind === 'question-request') {
          // Answers POST to the ASKING run: childRunId for a bubbled child question,
          // else the watched run (BLOCKING #1 — mirror LifecycleEventCard childRunId routing).
          return (
            <QuestionAnswerCard
              key={`q-${item.requestId}`}
              runId={item.askingRunId ?? runId ?? ''}
              requestId={item.requestId}
              question={item.question}
              answer={item.resolved ? (item.answer ?? '') : undefined}
              timedOut={item.timedOut}
              sourceLabel={item.sourceLabel}
            />
          );
        }
        // lifecycle
        const approvalRequestId =
          item.event.type === 'tool.approval_required' ||
          item.event.type === 'coordinator.child_approval_required' ||
          item.event.type === 'shell.approval_required'
            ? String(item.event.payload['requestId'] ?? item.event.payload['request_id'] ?? '')
            : '';
        const resolvedScope = approvalRequestId ? resolvedApprovals.get(approvalRequestId) : undefined;
        return (
          <LifecycleEventCard
            key={`lc-${item.event.sequence > 0 ? item.event.sequence : i}`}
            event={item.event}
            runId={runId}
            isResolved={resolvedScope != null}
            resolvedScope={resolvedScope ?? null}
            runOutcome={item.event.type === 'run.completed' ? runOutcome : undefined}
          />
        );
      })}

      {streamStatus === 'connecting' && (
        <span className={styles.connecting}>
          <Spinner size="extra-tiny" aria-hidden="true" />
          <Text size={200}>Waiting for agent...</Text>
        </span>
      )}
    </div>
  );
});
