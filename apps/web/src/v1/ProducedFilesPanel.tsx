import { makeStyles, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useState } from 'react';
import type { WorkspaceFileContent } from '../api/types';
import { FileViewer } from '../components/FileViewer';
import { FilesTabPanel } from '../components/ArtifactBrowser';
import { EmptyState } from '../components/ui/States';
import { gatewayClient } from './api';
import { useAuth } from './AuthContext';
import type { SourceControlOutputCaptureDetail } from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';
import { decodeCapturedFile } from './producedFiles';

type ScopeProps = { projectId: string; runId: string; sessionId: string; captureId: string };

const useStyles = makeStyles({
  browser: {
    display: 'grid',
    gridTemplateColumns: 'minmax(180px, 30%) minmax(0, 1fr)',
    minHeight: '360px',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 640px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  tree: { maxHeight: '360px', overflow: 'auto', minWidth: 0 },
  viewer: { display: 'flex', flexDirection: 'column', minWidth: 0 },
});

/** Browses one immutable saved file set through the retained 0.x file viewer. */
export function ProducedFilesPanel(props: ScopeProps) {
  // Remount on every scope change so neither selection nor late responses cross captures.
  return <CaptureBrowser key={`${props.projectId}/${props.runId}/${props.sessionId}/${props.captureId}`} {...props} />;
}

function CaptureBrowser({ projectId, runId, sessionId, captureId }: ScopeProps) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  // Keep the token binding stable so rendering does not restart the manifest request.
  const binding = useMemo(() => ({ projectId, runId }), [projectId, runId]);
  const [detail, setDetail] = useState<SourceControlOutputCaptureDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);

  useEffect(() => {
    // Ignore responses after unmount; a newly selected capture has its own keyed browser.
    let current = true;
    apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCapture(token, projectId, runId, sessionId, tenantSelector, captureId),
      binding,
    ).then((value) => {
      if (current) setDetail(value);
    }).catch((reason: unknown) => {
      if (current) setError(reason);
    });
    return () => { current = false; };
  }, [apiCall, binding, captureId, projectId, runId, sessionId]);

  const getContent = useCallback(async (_runId: string, path: string): Promise<WorkspaceFileContent> => {
    // FileViewer's run argument cannot change the capture scope. Only manifest paths are offered,
    // and the owner independently rejects paths outside this immutable file set.
    const file = await apiCall(
      (token, tenantSelector) => gatewayClient.readSourceControlOutputCaptureFile(token, projectId, runId, sessionId, tenantSelector, captureId, path),
      binding,
    );
    // Decode bytes, not a browser text response, to preserve binary and invalid UTF-8 detection.
    return decodeCapturedFile(path, file.bytes);
  }, [apiCall, binding, captureId, projectId, runId, sessionId]);

  if (error) return <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>;
  if (detail === null) return <Loading label="Loading files…" />;
  const files = detail.manifest.files;
  if (files.length === 0) return <EmptyState title="No files" description="This save has no files." />;
  // Start with the first manifest entry; users navigate by file names, never internal IDs.
  const filePath = selectedPath ?? files[0].path;

  return (
    <div className={styles.browser}>
      <div className={styles.tree}>
        <FilesTabPanel
          workspaceFiles={files.map((file) => ({ path: file.path, is_folder: false, status: null }))}
          workspaceLoading={false}
          workspaceError={null}
          selectedPath={filePath}
          onFileClick={(path) => setSelectedPath(path)}
        />
      </div>
      {/* Remount the retained viewer per path so its preview and loading state follow selection. */}
      <div className={styles.viewer}>
        <FileViewer key={filePath} runId={runId} filePath={filePath} getContent={getContent} />
      </div>
    </div>
  );
}
