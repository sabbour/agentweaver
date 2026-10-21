import { Button, Field, Select, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState } from 'react';
import { FilesTabPanel } from '../components/ArtifactBrowser';
import { FileViewer } from '../components/FileViewer';
import { EmptyState } from '../components/ui';
import type { WorkspaceFileContent } from '../api/types';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type { SourceControlOutputCaptureDetail, SourceControlOutputCapturePage } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';

/*
 * Browser for saved output captures of one run: pick a session, pick a capture,
 * then view its files. Every read uses a token bound to the run. Every response
 * is checked against the requested run, session and capture, and file bytes are
 * checked against the capture manifest before they are shown.
 */

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minWidth: 0 },
  split: {
    display: 'grid', gridTemplateColumns: 'minmax(180px, 1fr) minmax(0, 3fr)',
    border: `1px solid ${tokens.colorNeutralStroke2}`, minHeight: '340px',
    '@media (max-width: 640px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  files: { padding: tokens.spacingHorizontalS, overflow: 'auto', maxHeight: '480px' },
  viewer: { display: 'flex', minWidth: 0, minHeight: '300px', maxHeight: '600px', overflow: 'auto' },
  meta: { color: tokens.colorNeutralForeground3 },
  select: { minWidth: 0, maxWidth: '100%', '& select': { minWidth: 0 } },
});

/** Keys on current authority as well as route identity so old content disappears before a new read. */
export function OutputCaptureBrowser({ projectId, runId, initialCaptureId, onSelect, busy }: {
  projectId: string;
  runId: string;
  initialCaptureId?: string;
  onSelect?: (captureId: string) => void;
  busy?: boolean;
}) {
  const { session, authorizationContext } = useAuth();
  // The key changes when the run, the signed-in token, the tenant, or the
  // membership revision changes. A new key remounts CaptureRun, so all state
  // and any data read under the old authority is dropped at once.
  const scope = JSON.stringify([projectId, runId, session?.accessToken,
    authorizationContext?.tenantId, authorizationContext?.membershipRevision]);
  return <CaptureRun key={scope} {...{ projectId, runId, initialCaptureId, onSelect, busy }} />;
}

/** Local reads discard late responses on selection, authority changes, and unmount. */
function useRead<T>(read: () => Promise<T>) {
  const [result, setResult] = useState<{ data?: T; error?: unknown }>({});
  // Bumped by retry() to run the effect again with the same `read`.
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    // Set to false on cleanup. A response that arrives after that is ignored.
    let active = true;
    // Start the read in a microtask so state is never set synchronously inside the effect.
    void Promise.resolve().then(read).then(
      (data) => { if (active) setResult({ data }); },
      (error: unknown) => { if (active) setResult({ error }); },
    );
    return () => { active = false; };
  }, [read, generation]);
  // retry() clears the old result first, so the UI shows loading instead of the old error.
  return { ...result, retry: () => { setResult({}); setGeneration((value) => value + 1); } };
}

/** Read error with one action: sign in to the run when `signIn` is given, otherwise retry. */
function ReadError({ error, retry, signIn }: { error: unknown; retry: () => void; signIn?: () => void }) {
  return <div>
    <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>
    <Button onClick={signIn ?? retry}>{signIn ? 'Sign in to run' : 'Try again'}</Button>
  </div>;
}

/** Reads the run's session tree and lets the user choose which session's captures to browse. */
function CaptureRun({ projectId, runId, initialCaptureId, onSelect, busy }: Parameters<typeof OutputCaptureBrowser>[0]) {
  const styles = useStyles();
  const { apiCall, ensureSession, authorize } = useAuth();
  const read = useCallback(async () => {
    const binding = { projectId, runId };
    // Get a run-bound token without a popup. If sign-in is needed, throw a
    // dedicated error code so the UI offers "Sign in to run" instead of "Try again".
    if (!await ensureSession(binding, false)) throw new GatewayError(401, { code: 'run_sign_in_required' },
      'Sign in to this run to browse its artifacts.');
    const status = await apiCall((token, tenant) => gatewayClient.getRunStatus(token, projectId, runId, tenant), binding);
    // Defense in depth: never show data for a run other than the one requested.
    if (status.projectId !== projectId || status.runId !== runId) throw new Error('The run changed. Open it again.');
    const tree = await apiCall((token, tenant) =>
      gatewayClient.getSessionTree(token, projectId, runId, status.rootSessionId, tenant), binding);
    // The tree must have the same root as the status, and every session in it
    // must belong to this project and run.
    if (tree.rootSessionId !== status.rootSessionId || tree.nodes.some((node) =>
      node.identity.projectId !== projectId || node.identity.runId !== runId))
      throw new Error('The sessions belong to a different run. Open the run again.');
    return tree;
  }, [apiCall, ensureSession, projectId, runId]);
  const result = useRead(read);
  const [pickedSession, setPickedSession] = useState('');
  // For the sign-in error, run interactive sign-in for this run and then read again.
  if (result.error != null) return <ReadError error={result.error} retry={result.retry}
    signIn={errorCode(result.error) === 'run_sign_in_required' ? () => { void authorize({ projectId, runId }).then(result.retry); } : undefined} />;
  if (!result.data) return <Loading label="Loading sessions" />;
  const tree = result.data;
  // Use the picked session only if it is still in the tree; default to the main (root) session.
  const sessionId = tree.nodes.some((node) => node.identity.sessionId === pickedSession) ? pickedSession : tree.rootSessionId;
  if (!tree.nodes.length) return <EmptyState title="No sessions" description="This run has no sessions to browse." />;
  return <section className={styles.stack} aria-label="Artifacts">
    <Field label="Session">
      <Select className={styles.select} value={sessionId} onChange={(_, data) => setPickedSession(data.value)}>
        {tree.nodes.map((node) => <option key={node.identity.sessionId} value={node.identity.sessionId}>
          {node.identity.sessionId === tree.rootSessionId ? 'Main session' : `${node.kind} session · ${node.identity.sessionId}`}
        </option>)}
      </Select>
    </Field>
    {initialCaptureId && <Text className={styles.meta}>Choose the session containing the linked capture.</Text>}
    <CaptureList key={sessionId} {...{ projectId, runId, sessionId, initialCaptureId, onSelect, busy }} />
  </section>;
}

/** Lists one session's saved captures, newest first, with paging, and shows the chosen one. */
function CaptureList({ projectId, runId, sessionId, initialCaptureId, onSelect, busy }: Parameters<typeof OutputCaptureBrowser>[0] & { sessionId: string }) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  // Reads one page. The cursor is the (capturedAt, captureId) pair of the last
  // capture already shown; null reads the newest page.
  const readPage = useCallback((cursor: { capturedAt: string; captureId: string } | null) =>
    apiCall((token, tenant) => gatewayClient.listRunOutputCaptures(token, projectId, runId, sessionId,
      { limit: 50, beforeCapturedAt: cursor?.capturedAt, beforeCaptureId: cursor?.captureId }, tenant),
      { projectId, runId }), [apiCall, projectId, runId, sessionId]);
  const firstRead = useCallback(() => readPage(null), [readPage]);
  const first = useRead(firstRead);
  // `more` holds the combined list after one or more "load more" calls.
  const [more, setMore] = useState<SourceControlOutputCapturePage | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  // Mounted flag, so a "load more" that finishes after unmount does not set state.
  const active = useRef(true);
  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  // Start with the linked capture, if any, so it opens without a click.
  const [picked, setPicked] = useState(initialCaptureId ?? '');
  const page = more ?? first.data;
  // Only select a capture that is in the loaded list. Otherwise show "Choose a capture".
  const captureId = page?.captures.some((capture) => capture.captureId === picked) ? picked : '';
  const loadMore = async () => {
    // Stop when there is no next page or a load is already running.
    if (!page?.nextBeforeCapturedAt || !page.nextBeforeCaptureId || loading) return;
    setLoading(true);
    setError(null);
    try {
      const next = await readPage({ capturedAt: page.nextBeforeCapturedAt, captureId: page.nextBeforeCaptureId });
      // Append the new page and skip captures already listed. The new page's
      // cursor replaces the old one because `next` is spread first.
      if (active.current) setMore({ ...next, captures: [...page.captures, ...next.captures.filter(
        (capture) => !page.captures.some((old) => old.captureId === capture.captureId))] });
    } catch (reason) {
      // Keep the captures already shown; only show the error.
      if (active.current) setError(reason);
    } finally {
      if (active.current) setLoading(false);
    }
  };
  if (first.error != null) return <ReadError error={first.error} retry={first.retry} />;
  if (!page) return <Loading label="Loading captures" />;
  return <div className={styles.stack}>
    {page.captures.length === 0 ? <EmptyState title="No saved captures" description="Files appear here after this session saves produced output. Failed or unknown runs may withhold captures." /> : (
      <Field label="Saved capture">
        <Select className={styles.select} value={captureId} onChange={(_, data) => setPicked(data.value)}>
          <option value="">Choose a capture</option>
          {page.captures.map((capture) => <option key={capture.captureId} value={capture.captureId}>
            {`${capture.branchName} · ${new Date(capture.capturedAt).toLocaleString()} · ${capture.captureId}`}
          </option>)}
        </Select>
      </Field>
    )}
    {initialCaptureId && !captureId && <Text>The linked capture is not on this page. Choose a session or load more captures.</Text>}
    {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
    {page.nextBeforeCapturedAt && page.nextBeforeCaptureId && (
      <Button disabled={loading} onClick={() => void loadMore()}>{loading ? 'Loading…' : 'Load more captures'}</Button>
    )}
    {captureId && <CaptureDetail key={captureId} {...{ projectId, runId, sessionId, captureId, onSelect, busy }} />}
  </div>;
}

/** Shows one capture's file list and viewer, and an optional "Link capture" action. */
function CaptureDetail({ projectId, runId, sessionId, captureId, onSelect, busy }: {
  projectId: string; runId: string; sessionId: string; captureId: string;
  onSelect?: (captureId: string) => void; busy?: boolean;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const read = useCallback(async () => {
    const detail = await apiCall((token, tenant) =>
      gatewayClient.readSourceControlOutputCapture(token, projectId, runId, sessionId, captureId, tenant ?? ''), { projectId, runId });
    // The capture and its manifest must describe the same run, workspace and
    // output tree. If they do not, refuse to show it.
    if (detail.capture.captureId !== captureId || detail.manifest.runId !== runId ||
      detail.capture.workspaceId !== detail.manifest.workspaceId || detail.capture.outputTreeSha !== detail.manifest.outputTreeSha)
      throw new Error('The capture does not match this run. Choose it again.');
    return detail;
  }, [apiCall, projectId, runId, sessionId, captureId]);
  const result = useRead<SourceControlOutputCaptureDetail>(read);
  const [path, setPath] = useState<string | null>(null);
  // Content loader for FileViewer. The first argument is FileViewer's run id and is not needed here.
  const getContent = useCallback(async (_run: string, filePath: string): Promise<WorkspaceFileContent> => {
    // Only read paths listed in the manifest; the manifest holds the expected size and hash.
    const file = result.data?.manifest.files.find((entry) => entry.path === filePath);
    if (!file) throw new Error('This file is no longer in the capture. Choose it again.');
    const { bytes } = await apiCall((token, tenant) => gatewayClient.readSourceControlOutputCaptureFile(
      token, projectId, runId, sessionId, captureId, filePath, tenant ?? ''), { projectId, runId });
    // Verify the bytes before showing them: size and SHA-256 must match the manifest.
    const digest = await crypto.subtle.digest('SHA-256', new Uint8Array(bytes));
    // Hex-encode the digest to compare with the manifest's lowercase hex hash.
    const hash = Array.from(new Uint8Array(digest), (value) => value.toString(16).padStart(2, '0')).join('');
    if (bytes.byteLength !== file.byteLength || hash !== file.sha256)
      throw new Error('The saved file could not be verified. Choose the capture again.');
    // Strict UTF-8 decode: invalid bytes throw, and the file is treated as binary.
    let content: string | null = null;
    try { content = new TextDecoder('utf-8', { fatal: true }).decode(bytes); } catch { /* Non-UTF-8 files use the binary state. */ }
    // A NUL character also marks the file as binary, even if it decoded.
    const binary = content === null || content.includes('\0');
    return { path: filePath, content: binary ? null : content, is_binary: binary, language: null };
  }, [apiCall, projectId, runId, sessionId, captureId, result.data]);
  if (result.error != null) return <ReadError error={result.error} retry={result.retry} />;
  if (!result.data) return <Loading label="Loading capture" />;
  const { capture, manifest } = result.data;
  return <section className={styles.stack} aria-label="Capture details">
    <Text weight="semibold">{capture.branchName}</Text>
    <Text className={styles.meta}>{manifest.files.length} files · Saved {new Date(capture.capturedAt).toLocaleString()}</Text>
    <div className={styles.split}>
      <div className={styles.files} role="region" aria-label="Captured files">
        <FilesTabPanel workspaceFiles={manifest.files.map((file) => ({ path: file.path, is_folder: false, status: null }))}
          workspaceLoading={false} workspaceError={null} selectedPath={path} onFileClick={setPath} />
      </div>
      <div className={styles.viewer}>
        <FileViewer key={path} runId={runId} filePath={path} getContent={getContent} />
      </div>
    </div>
    {onSelect && <div>
      <Text>Link this saved capture, including all its files.</Text>{' '}
      <Button appearance="primary" disabled={busy} onClick={() => onSelect(captureId)}>Link capture</Button>
    </div>}
  </section>;
}
