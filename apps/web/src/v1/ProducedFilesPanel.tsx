import { Button } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useState } from 'react';
import type { WorkspaceFileContent } from '../api/types';
import { DiffViewer } from '../components/DiffViewer';
import { FileViewer } from '../components/FileViewer';
import { EmptyState } from '../components/ui/States';
import { gatewayClient, GatewayError } from './api';
import { useAuth } from './AuthContext';
import type {
  SourceControlOutputCaptureDetail,
  SourceControlOutputCaptureSummary,
} from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading, Panel } from './pageParts';
import { decodeCapturedFile } from './producedFiles';

function captureLabel(capture: SourceControlOutputCaptureSummary): string {
  // Label a capture by when it was saved and which branch it came from. Readers never see
  // capture or event IDs; those stay internal.
  return `${new Date(capture.capturedAt).toLocaleString()} · ${capture.branchName}`;
}

type ScopeProps = { projectId: string; runId: string; sessionId: string };
type LoadError = { message: string; code?: string };

function toLoadError(error: unknown): LoadError {
  // Keep only the readable message and the error code. ErrorNotice shows both, and the raw
  // error object can hold response details that the page must not display.
  return { message: errorMessage(error), code: errorCode(error) };
}

/**
 * Browses the files a run produced. Each capture is a pinned, immutable copy of the run's
 * workspace output, so the list stays readable after the workspace is gone. The owner withholds
 * captures while the run is failed or its outcome is not yet known.
 */
export function ProducedFilesPanel(props: ScopeProps) {
  // Key the list by project, run, and session. When the user switches to another session,
  // React mounts a fresh list, so no state or late response from the old session leaks in.
  return <CaptureList key={`${props.projectId}/${props.runId}/${props.sessionId}`} {...props} />;
}

function CaptureList({ projectId, runId, sessionId }: ScopeProps) {
  const { apiCall } = useAuth();
  // apiCall uses this binding to request a token scoped to this project and run. Memoize it so
  // the effect below does not rerun on every render.
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  // null means the list is still loading; an empty array means it loaded with no captures.
  const [captures, setCaptures] = useState<SourceControlOutputCaptureSummary[] | null>(null);
  const [listError, setListError] = useState<LoadError | null>(null);
  // True when the owner hides captures for this run (failed or unknown outcome).
  const [withheld, setWithheld] = useState(false);
  // The capture the user picked. null means "use the newest one".
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    // Ignore a response that arrives after the component unmounts or the inputs change.
    let current = true;
    // Read only the first page (newest 50). The panel shows recent saves, not full history.
    apiCall(
      (token, tenantSelector) => gatewayClient.listSourceControlOutputCaptures(token, projectId, runId, sessionId, tenantSelector),
      binding,
    ).then((page) => {
      if (current) setCaptures(page.captures);
    }).catch((error: unknown) => {
      if (!current) return;
      // The owner answers 404 when it withholds captures for a failed or undecided run. That is
      // an expected state, not an error, so show the "not available" message instead.
      if (error instanceof GatewayError && error.status === 404) {
        setWithheld(true);
        setCaptures([]);
        return;
      }
      setListError(toLoadError(error));
    });
    return () => { current = false; };
  }, [apiCall, binding, projectId, runId, sessionId]);

  // The list is newest first, so default to the first capture until the user picks another.
  const captureId = selectedId ?? captures?.[0]?.captureId ?? '';

  return (
    <Panel title="Produced files">
      {listError && <ErrorNotice code={listError.code}>{listError.message}</ErrorNotice>}
      {!listError && captures === null && <Loading label="Loading produced files…" />}
      {withheld && (
        <EmptyState title="Produced files are not available" description="Files appear after the run finishes without failing." />
      )}
      {!withheld && captures !== null && captures.length === 0 && (
        <EmptyState title="No produced files yet" description="Files appear here after the run saves its work." />
      )}
      {captures !== null && captures.length > 0 && (
        <div className="v1-stack">
          {/* Show a picker only when there is more than one capture to choose from. */}
          {captures.length > 1 ? (
            <label>
              Saved at{' '}
              <select className="v1-select" value={captureId} onChange={(event) => setSelectedId(event.target.value)}>
                {captures.map((capture) => (
                  <option key={capture.captureId} value={capture.captureId}>{captureLabel(capture)}</option>
                ))}
              </select>
            </label>
          ) : (
            <p>Saved {captureLabel(captures[0])}</p>
          )}
          {/* Keyed by capture so switching captures resets the file selection and cached diff. */}
          <CaptureBrowser key={captureId} projectId={projectId} runId={runId} sessionId={sessionId} captureId={captureId} />
        </div>
      )}
    </Panel>
  );
}

function CaptureBrowser({ projectId, runId, sessionId, captureId }: ScopeProps & { captureId: string }) {
  const { apiCall } = useAuth();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  // The capture and its manifest. The manifest is the only list of files the owner will serve.
  const [detail, setDetail] = useState<SourceControlOutputCaptureDetail | null>(null);
  const [error, setError] = useState<LoadError | null>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  // The diff is loaded on first use and then kept, because a capture never changes.
  const [patch, setPatch] = useState<string | null>(null);
  const [showChanges, setShowChanges] = useState(false);

  useEffect(() => {
    // Ignore a response that arrives after the component unmounts or the capture changes.
    let current = true;
    apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCapture(token, projectId, runId, sessionId, tenantSelector, captureId),
      binding,
    ).then((value) => {
      if (current) setDetail(value);
    }).catch((reason: unknown) => {
      if (current) setError(toLoadError(reason));
    });
    return () => { current = false; };
  }, [apiCall, binding, captureId, projectId, runId, sessionId]);

  const openChanges = useCallback(() => {
    setShowChanges(true);
    // A capture is immutable, so a diff already loaded is still correct. Don't fetch it again.
    if (patch !== null) return;
    apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCaptureDiff(token, projectId, runId, sessionId, tenantSelector, captureId),
      binding,
    ).then((value) => setPatch(value.patch))
      .catch((reason: unknown) => setError(toLoadError(reason)));
  }, [apiCall, binding, captureId, patch, projectId, runId, sessionId]);

  // FileViewer calls this to load a file. Its runId argument is ignored because the capture
  // scope is already fixed. The path comes from the manifest, and the owner rejects any other.
  const getContent = useCallback(async (_runId: string, path: string): Promise<WorkspaceFileContent> => {
    const file = await apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCaptureFile(token, projectId, runId, sessionId, tenantSelector, captureId, path),
      binding,
    );
    // Decode the raw bytes here so binary content is detected exactly, not guessed by the browser.
    return decodeCapturedFile(path, file.bytes);
  }, [apiCall, binding, captureId, projectId, runId, sessionId]);

  // A failed capture or diff read replaces the whole browser with the error.
  if (error) return <ErrorNotice code={error.code}>{error.message}</ErrorNotice>;
  if (detail === null) return <Loading label="Loading files…" />;
  const files = detail.manifest.files;
  if (files.length === 0) return <EmptyState title="No files" description="This save has no files." />;
  // Show the first file until the user selects another.
  const filePath = selectedPath ?? files[0].path;

  return (
    <>
      <div>
        <Button appearance={showChanges ? 'secondary' : 'primary'} onClick={() => setShowChanges(false)}>Files</Button>{' '}
        <Button appearance={showChanges ? 'primary' : 'secondary'} onClick={openChanges}>Changes</Button>
      </div>
      {/* The diff covers the whole capture, so DiffViewer gets no single file name. */}
      {showChanges ? (
        patch === null ? <Loading label="Loading changes…" /> : <DiffViewer diff={patch} filename="" />
      ) : (
        <>
          {/* The manifest is the file list. Paths are shown as-is; the page does not read folders. */}
          <ul className="v1-list" aria-label="Produced files">
            {files.map((file) => (
              <li key={file.path}>
                <Button appearance={file.path === filePath ? 'primary' : 'subtle'} onClick={() => setSelectedPath(file.path)}>
                  {file.path}
                </Button>
              </li>
            ))}
          </ul>
          {/* Keyed by path so FileViewer reloads when the user selects a different file. */}
          <FileViewer key={filePath} runId={runId} filePath={filePath} getContent={getContent} />
        </>
      )}
    </>
  );
}