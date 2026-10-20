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
  return `${new Date(capture.capturedAt).toLocaleString()} · ${capture.branchName}`;
}

type ScopeProps = { projectId: string; runId: string; sessionId: string };
type LoadError = { message: string; code?: string };

function toLoadError(error: unknown): LoadError {
  return { message: errorMessage(error), code: errorCode(error) };
}

/**
 * Browses the files a run produced. Each capture is a pinned, immutable copy of the run's
 * workspace output, so the list stays readable after the workspace is gone. The owner withholds
 * captures while the run is failed or its outcome is not yet known.
 */
export function ProducedFilesPanel(props: ScopeProps) {
  return <CaptureList key={`${props.projectId}/${props.runId}/${props.sessionId}`} {...props} />;
}

function CaptureList({ projectId, runId, sessionId }: ScopeProps) {
  const { apiCall } = useAuth();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const [captures, setCaptures] = useState<SourceControlOutputCaptureSummary[] | null>(null);
  const [listError, setListError] = useState<LoadError | null>(null);
  const [withheld, setWithheld] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    apiCall(
      (token, tenantSelector) => gatewayClient.listSourceControlOutputCaptures(token, projectId, runId, sessionId, tenantSelector),
      binding,
    ).then((page) => {
      if (current) setCaptures(page.captures);
    }).catch((error: unknown) => {
      if (!current) return;
      if (error instanceof GatewayError && error.status === 404) {
        setWithheld(true);
        setCaptures([]);
        return;
      }
      setListError(toLoadError(error));
    });
    return () => { current = false; };
  }, [apiCall, binding, projectId, runId, sessionId]);

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
          <CaptureBrowser key={captureId} projectId={projectId} runId={runId} sessionId={sessionId} captureId={captureId} />
        </div>
      )}
    </Panel>
  );
}

function CaptureBrowser({ projectId, runId, sessionId, captureId }: ScopeProps & { captureId: string }) {
  const { apiCall } = useAuth();
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const [detail, setDetail] = useState<SourceControlOutputCaptureDetail | null>(null);
  const [error, setError] = useState<LoadError | null>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [patch, setPatch] = useState<string | null>(null);
  const [showChanges, setShowChanges] = useState(false);

  useEffect(() => {
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
    if (patch !== null) return;
    apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCaptureDiff(token, projectId, runId, sessionId, tenantSelector, captureId),
      binding,
    ).then((value) => setPatch(value.patch))
      .catch((reason: unknown) => setError(toLoadError(reason)));
  }, [apiCall, binding, captureId, patch, projectId, runId, sessionId]);

  const getContent = useCallback(async (_runId: string, path: string): Promise<WorkspaceFileContent> => {
    const file = await apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCaptureFile(token, projectId, runId, sessionId, tenantSelector, captureId, path),
      binding,
    );
    return decodeCapturedFile(path, file.bytes);
  }, [apiCall, binding, captureId, projectId, runId, sessionId]);

  if (error) return <ErrorNotice code={error.code}>{error.message}</ErrorNotice>;
  if (detail === null) return <Loading label="Loading files…" />;
  const files = detail.manifest.files;
  if (files.length === 0) return <EmptyState title="No files" description="This save has no files." />;
  const filePath = selectedPath ?? files[0].path;

  return (
    <>
      <div>
        <Button appearance={showChanges ? 'secondary' : 'primary'} onClick={() => setShowChanges(false)}>Files</Button>{' '}
        <Button appearance={showChanges ? 'primary' : 'secondary'} onClick={openChanges}>Changes</Button>
      </div>
      {showChanges ? (
        patch === null ? <Loading label="Loading changes…" /> : <DiffViewer diff={patch} filename="" />
      ) : (
        <>
          <ul className="v1-list" aria-label="Produced files">
            {files.map((file) => (
              <li key={file.path}>
                <Button appearance={file.path === filePath ? 'primary' : 'subtle'} onClick={() => setSelectedPath(file.path)}>
                  {file.path}
                </Button>
              </li>
            ))}
          </ul>
          <FileViewer key={filePath} runId={runId} filePath={filePath} getContent={getContent} />
        </>
      )}
    </>
  );
}