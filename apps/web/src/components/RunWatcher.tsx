import { useEffect, useMemo, useRef, useState } from 'react';
import { Badge, Divider, Spinner, makeStyles, tokens } from '@fluentui/react-components';
import { useRunStream } from '../api/sse';
import { API_KEY, API_URL } from '../config';
import { apiClient } from '../api/apiClient';
import type { RunDetail, ReviewResponse } from '../api/types';
import { DiffViewer } from './DiffViewer';
import { ReviewPanel } from './ReviewPanel';
import { RunHeader } from './RunHeader';
import { Timeline } from './Timeline';
import { useTimelineItems } from '../timeline/useTimelineItems';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
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
  const { events, status, error } = useRunStream(runId, API_KEY, API_URL);
  const { items } = useTimelineItems(events, runId);
  const isLiveRun = status === 'connecting' || status === 'streaming';

  const [reviewRun, setReviewRun] = useState<RunDetail | null>(null);
  const [reviewComplete, setReviewComplete] = useState<ReviewResponse | null>(null);
  const fetchingRef = useRef(false);

  const hasReviewRequested = events.some((e) => e.type === 'review.requested');

  useEffect(() => {
    if (hasReviewRequested && !reviewRun && !fetchingRef.current) {
      fetchingRef.current = true;
      apiClient
        .getRun(runId)
        .then(setReviewRun)
        .catch(() => { fetchingRef.current = false; });
    }
  }, [hasReviewRequested, reviewRun, runId]);

  // Derive effective review status by bridging terminal merge SSE events.
  const effectiveReviewComplete = useMemo((): ReviewResponse | null => {
    if (reviewComplete && reviewComplete.status !== 'merging') return reviewComplete;
    const mergeEvent = events.find(
      (e) => e.type === 'merge.completed' || e.type === 'merge.failed',
    );
    if (!mergeEvent) return reviewComplete;
    if (mergeEvent.type === 'merge.completed') {
      const commitHash = mergeEvent.payload.merged_commit_hash as string | undefined;
      return {
        run_id: reviewComplete?.run_id ?? runId,
        status: 'merged',
        merge_result: commitHash ?? reviewComplete?.merge_result ?? null,
      };
    }
    const reason = mergeEvent.payload.reason as string | undefined;
    return {
      run_id: reviewComplete?.run_id ?? runId,
      status: 'merge_failed',
      merge_result: reason ?? reviewComplete?.merge_result ?? null,
    };
  }, [reviewComplete, events, runId]);

  const showReviewPanel = reviewRun?.status === 'awaiting_review' &&
    (!effectiveReviewComplete || effectiveReviewComplete.status === 'merge_failed');

  const resolvedStatus = effectiveReviewComplete?.status ?? (reviewRun && reviewRun.status !== 'awaiting_review' ? reviewRun.status : null);

  return (
    <div className={styles.root}>
      <RunHeader runId={runId} streamStatus={status} error={error ?? undefined} />
      <Timeline items={items} streamStatus={status} isLiveRun={isLiveRun} />
      {hasReviewRequested && (
        <div className={styles.reviewSection}>
          <Divider />
          {reviewRun ? (
            <>
              <DiffViewer diff={reviewRun.diff} />
              {showReviewPanel && (
                <ReviewPanel
                  runId={runId}
                  treeHash={reviewRun.tree_hash}
                  onReviewComplete={setReviewComplete}
                />
              )}
              {resolvedStatus ? (
                <Badge
                  className={styles.resolvedBadge}
                  color={resolvedBadgeColor(resolvedStatus)}
                >
                  {resolvedStatus}
                  {effectiveReviewComplete?.merge_result ? ` \u2014 ${effectiveReviewComplete.merge_result}` : ''}
                </Badge>
              ) : null}
            </>
          ) : (
            <Spinner size="tiny" label="Loading diff..." />
          )}
        </div>
      )}
    </div>
  );
}
