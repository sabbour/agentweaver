/**
 * Lists the files a run session produced, as admitted output captures, and
 * shows the text patch of one capture on request. Reads use the run-bound
 * session, so the service checks that the caller owns this run.
 */
import { Button, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { EmptyState } from '../components/ui/States';
import { GatewayError, gatewayClient } from './api';
import { useAuth } from './AuthContext';
import type { SourceControlOutputCaptureDiff, SourceControlOutputCaptureSummary } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, Panel } from './pageParts';

const PAGE_SIZE = 20;

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, margin: 0, padding: 0, listStyle: 'none' },
  row: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  patch: {
    margin: 0,
    maxHeight: '480px',
    overflow: 'auto',
    padding: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusSmall,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre',
  },
});

/**
 * Plain message for a failed capture read. 403 means the session is not bound
 * to this run. 404 means no admitted capture is readable, for example while
 * the run is failed or its settings changed.
 */
function outputCaptureErrorMessage(error: unknown): string {
  if (error instanceof GatewayError && error.status === 403) return 'You do not have permission to read the files of this run.';
  if (error instanceof GatewayError && error.status === 404) return 'The files of this run are not available. They are hidden while the run is failed or its outcome is unknown.';
  return errorMessage(error);
}

/** Formats a byte count as B, KB, or MB with one decimal place. */
function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function RunOutputCapturesPanel({ projectId, runId, sessionId }: { projectId: string; runId: string; sessionId: string }) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const [captures, setCaptures] = useState<SourceControlOutputCaptureSummary[]>([]);
  const [cursor, setCursor] = useState<{ beforeCapturedAt: string; beforeCaptureId: string } | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [diff, setDiff] = useState<SourceControlOutputCaptureDiff | null>(null);
  const [diffLoadingId, setDiffLoadingId] = useState<string | null>(null);
  const [diffError, setDiffError] = useState<unknown>(null);
  // Counts reads so a late response for an older run or page is ignored.
  const generation = useRef(0);

  /**
   * Reads one page. With no cursor it replaces the list (first load and
   * refresh); with a cursor it appends the next page. The next cursor is kept
   * only when the service returns both of its parts.
   */
  const loadPage = useCallback(async (after: { beforeCapturedAt: string; beforeCaptureId: string } | null) => {
    const current = ++generation.current;
    if (after) setLoadingMore(true); else setLoading(true);
    setError(null);
    try {
      const page = await apiCall(
        (token, tenantSelector) => gatewayClient.listRunOutputCaptures(
          token, projectId, runId, sessionId, { limit: PAGE_SIZE, ...(after ?? {}) }, tenantSelector,
        ),
        binding,
      );
      if (current !== generation.current) return;
      setCaptures((previous) => (after ? [...previous, ...page.captures] : page.captures));
      setCursor(page.nextBeforeCapturedAt && page.nextBeforeCaptureId
        ? { beforeCapturedAt: page.nextBeforeCapturedAt, beforeCaptureId: page.nextBeforeCaptureId }
        : null);
    } catch (reason) {
      if (current !== generation.current) return;
      // A failed first load clears the list; a failed next page keeps what is shown.
      if (!after) { setCaptures([]); setCursor(null); }
      setError(reason);
    } finally {
      if (current === generation.current) { setLoading(false); setLoadingMore(false); }
    }
  }, [apiCall, binding, projectId, runId, sessionId]);

  // Load on mount and whenever the run or session changes. The read starts in a
  // timer so its state updates do not run inside the effect body.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      setDiff(null);
      setDiffError(null);
      void loadPage(null);
    }, 0);
    return () => window.clearTimeout(timer);
  }, [loadPage]);

  /** Reads the patch for one capture. Clicking the shown capture again hides it. */
  const toggleDiff = async (captureId: string) => {
    if (diff?.capture.captureId === captureId) { setDiff(null); return; }
    setDiffLoadingId(captureId);
    setDiffError(null);
    try {
      const next = await apiCall(
        (token, tenantSelector) => gatewayClient.readRunOutputCaptureDiff(token, projectId, runId, sessionId, captureId, tenantSelector),
        binding,
      );
      setDiff(next);
    } catch (reason) {
      setDiff(null);
      setDiffError(reason);
    } finally {
      setDiffLoadingId(null);
    }
  };

  let body;
  if (loading) body = <Loading label="Loading run files…" />;
  else if (error != null && captures.length === 0)
    body = <ErrorNotice code={errorCode(error)}>{outputCaptureErrorMessage(error)}</ErrorNotice>;
  else if (captures.length === 0)
    body = <EmptyState title="No files yet" description="This run has not saved any produced files." />;
  else
    body = (
      <>
        {error != null && <ErrorNotice code={errorCode(error)}>{outputCaptureErrorMessage(error)}</ErrorNotice>}
        <ul className={styles.list} aria-label="Saved file sets">
          {captures.map((capture) => (
            <li key={capture.captureId} className={styles.row}>
              <Text weight="semibold">{capture.branchName}</Text>
              <Text className={styles.muted}>
                Saved {new Date(capture.capturedAt).toLocaleString()} · base {capture.baseSha.slice(0, 7)} · patch {formatBytes(capture.patchByteLength)}
              </Text>
              <Button
                size="small"
                disabled={diffLoadingId !== null}
                onClick={() => { void toggleDiff(capture.captureId); }}
              >
                {diff?.capture.captureId === capture.captureId ? 'Hide changes' : diffLoadingId === capture.captureId ? 'Loading…' : 'Show changes'}
              </Button>
            </li>
          ))}
        </ul>
        {diffError != null && <ErrorNotice code={errorCode(diffError)}>{outputCaptureErrorMessage(diffError)}</ErrorNotice>}
        {diff && (
          diff.patch.length === 0
            ? <Text className={styles.muted}>This file set has no text changes.</Text>
            : <pre className={styles.patch} aria-label={`Changes on ${diff.capture.branchName}`}>{diff.patch}</pre>
        )}
        {cursor && (
          <Button disabled={loadingMore} onClick={() => { void loadPage(cursor); }}>
            {loadingMore ? 'Loading…' : 'Load older file sets'}
          </Button>
        )}
      </>
    );

  return (
    <Panel
      title="Produced files"
      description="File sets this run saved from its workspace, newest first."
      actions={<Button disabled={loading || loadingMore} onClick={() => { setDiff(null); void loadPage(null); }}>Refresh</Button>}
    >
      {body}
    </Panel>
  );
}