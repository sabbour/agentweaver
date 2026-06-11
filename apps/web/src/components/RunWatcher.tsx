import { useEffect, useMemo, useRef } from 'react';
import { Badge, Divider, makeStyles, tokens } from '@fluentui/react-components';
import { useRunStream } from '../api/sse';
import { API_KEY, API_URL } from '../config';
import type { ReviewResponse } from '../api/types';
import { deriveRunStatusFromEvents } from '../timeline/deriveRunStatus';
import { RunHeader } from './RunHeader';
import { RunLayout } from './RunLayout';
import { Timeline } from './Timeline';
import { useTimelineItems } from '../timeline/useTimelineItems';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  centerContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalM,
  },
  reviewSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    marginTop: tokens.spacingVerticalS,
  },
  resolvedBadge: {
    alignSelf: 'flex-start',
  },
});

function resolvedBadgeColor(
  status: string,
): 'success' | 'subtle' | 'danger' | 'informative' {
  if (status === 'merged') return 'success';
  if (status === 'declined') return 'subtle';
  if (status === 'merging') return 'informative';
  return 'danger';
}

interface RunWatcherProps { runId: string; }

export function RunWatcher({ runId }: RunWatcherProps) {
  const styles = useStyles();
  const { events, status, error, reconnect } = useRunStream(runId, API_KEY, API_URL);
  const { items, runOutcome } = useTimelineItems(events, runId);
  const isLiveRun = status === 'connecting' || status === 'streaming';

  // Auto-scroll center panel to bottom as new events arrive,
  // unless the user has manually scrolled up.
  const centerScrollRef = useRef<HTMLDivElement>(null);
  const userScrolledUp = useRef(false);

  const handleCenterScroll = () => {
    const el = centerScrollRef.current;
    if (!el) return;
    userScrolledUp.current = el.scrollHeight - el.scrollTop - el.clientHeight > 64;
  };

  useEffect(() => {
    if (!userScrolledUp.current && centerScrollRef.current) {
      centerScrollRef.current.scrollTop = centerScrollRef.current.scrollHeight;
    }
  }, [items.length]);

  const resolvedReview = useMemo((): ReviewResponse | null => {
    const mergeEvent = events.find(
      (e) => e.type === 'merge.completed' || e.type === 'merge.failed',
    );
    if (!mergeEvent) return null;
    if (mergeEvent.type === 'merge.completed') {
      const commitHash = mergeEvent.payload.merged_commit_hash as string | undefined;
      return {
        run_id: runId,
        status: 'merged',
        merge_result: commitHash ?? null,
      };
    }
    const reason = mergeEvent.payload.reason as string | undefined;
    return {
      run_id: runId,
      status: 'merge_failed',
      merge_result: reason ?? null,
    };
  }, [events, runId]);

  // Derive run status from the most recent lifecycle event so that revisit
  // cycles (revision.started -> second review.requested) are handled correctly.
  // A second review.requested overrides the earlier one; revision.started
  // overrides the first review.requested. isLiveRun is only used as a fallback
  // when no lifecycle events have arrived yet.
  const derivedRunStatus = deriveRunStatusFromEvents(events, isLiveRun);

  const centerContent = (
    <div className={styles.centerContent}>
      <Timeline items={items} streamStatus={status} isLiveRun={isLiveRun} runId={runId} runOutcome={runOutcome} />
      {resolvedReview && (
        <div className={styles.reviewSection}>
          <Divider />
          <Badge
            className={styles.resolvedBadge}
            color={resolvedBadgeColor(resolvedReview.status)}
          >
            {resolvedReview.status}
            {resolvedReview.merge_result ? ` \u2014 ${resolvedReview.merge_result}` : ''}
          </Badge>
        </div>
      )}
    </div>
  );

  return (
    <div className={styles.root}>
      <RunHeader runId={runId} streamStatus={status} error={error ?? undefined} />
      <RunLayout
        runId={runId}
        runStatus={derivedRunStatus}
        centerContent={centerContent}
        centerScrollRef={centerScrollRef}
        onCenterScroll={handleCenterScroll}
        onRequestChangesSuccess={reconnect}
        onCommitSuccess={reconnect}
      />
    </div>
  );
}
