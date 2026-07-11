import {
  Text } from '@fluentui/react-components';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import { FileTreePanel } from './ArtifactBrowser';
import { FileViewerModal } from './FileViewerModal';
import { makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
const useStyles = makeStyles({
  root: {
    minHeight: 0,
    height: '100%',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  tree: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    marginBottom: tokens.spacingVerticalS,
    color: tokens.colorNeutralForeground3,
  },
});

export interface CoordinatorArtifactsPanelProps {
  runId: string;
  runStatus: string;
  adapter: ArtifactBrowserAdapter;
}

/**
 * File browser for the current run's assembled artifacts. Reuses the shared
 * artifact-browser hook + file tree, pointing at the coordinator's integration-branch adapter so the
 * panel shows the collective diff. Clicking a file opens the standard file viewer (with diffs).
 */
export function CoordinatorArtifactsPanel({ runId, runStatus, adapter }: CoordinatorArtifactsPanelProps) {
  const styles = useStyles();
  const state = useArtifactBrowser(runId, runStatus, undefined, undefined, undefined, undefined, adapter);

  return (
    <div className={styles.root} data-testid="coord-artifacts-panel">
      <Text className={styles.hint}>
        Files produced across this run, shown as the collective integration diff.
      </Text>
      <div className={styles.tree}>
        <FileTreePanel state={state} onFileClick={(path, isChanged) => state.handleFileSelect(path, isChanged ?? true)} />
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
