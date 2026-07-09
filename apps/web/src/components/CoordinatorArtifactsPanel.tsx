import { type ReactNode } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';
import { useArtifactBrowser, type ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
import { FileTreePanel } from './ArtifactBrowser';
import { FileViewerModal } from './FileViewerModal';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    height: '100%',
  },
  tree: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginBottom: tokens.spacingVerticalS,
  },
});

export interface CoordinatorArtifactsPanelProps {
  runId: string;
  runStatus: string;
  adapter: ArtifactBrowserAdapter;
  previewStatusSlot?: ReactNode;
}

/**
 * Workspace file browser for the current run's assembled artifacts (#165). Reuses the shared
 * artifact-browser hook + file tree, pointing at the coordinator's integration-branch adapter so the
 * panel shows the collective diff. Clicking a file opens the standard file viewer (with diffs).
 */
export function CoordinatorArtifactsPanel({ runId, runStatus, adapter, previewStatusSlot }: CoordinatorArtifactsPanelProps) {
  const styles = useStyles();
  const state = useArtifactBrowser(runId, runStatus, undefined, undefined, undefined, undefined, adapter);

  return (
    <div className={styles.root} data-testid="coord-artifacts-panel">
      <div className={styles.hint}>Files produced across this run, shown as the collective integration diff.</div>
      <div className={styles.tree}>
        <FileTreePanel state={state} previewStatusSlot={previewStatusSlot} onFileClick={(path, isChanged) => state.handleFileSelect(path, isChanged ?? true)} />
      </div>
      <FileViewerModal
        runId={runId}
        filePath={state.selectedPath}
        onClose={state.clearSelection}
        diff={state.diff}
        diffLoading={state.diffLoading}
        diffError={state.diffError}
        isChanged={state.selectedPathIsChanged}
        getContent={adapter.getContent}
      />
    </div>
  );
}
