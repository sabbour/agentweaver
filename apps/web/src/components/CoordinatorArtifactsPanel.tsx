import { Text } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import { FileTreePanel } from './ArtifactBrowser';
import { FileViewer } from './FileViewer';
import { makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { ArtifactBrowserAdapter } from '../hooks/useArtifactBrowser';
const useStyles = makeStyles({
  root: {
    minHeight: 0,
    height: '100%',
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'stretch',
  },
  treeColumn: {
    width: 'min(360px, 34vw)',
    flexShrink: 0,
    minHeight: 0,
    height: '100%',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalM,
    boxSizing: 'border-box',
    overflowY: 'auto',
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  hint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  viewerColumn: {
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    height: '100%',
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  },
});

export interface CoordinatorArtifactsPanelProps {
  runId: string;
  runStatus: string;
  adapter?: ArtifactBrowserAdapter;
  /** Which sub-view to open on first render: the integration diff ('changes', default) or the
   *  produced-files browser ('files'). Lets the Changes vs Files chips reach distinct destinations. */
  initialTab?: 'changes' | 'files';
  /** Existing run-stream sequence key used to refetch artifacts immediately when new run events land. */
  liveUpdateKey?: number;
  previewStatusSlot?: ReactNode;
}

/**
 * File browser for the current run's assembled artifacts. Reuses the shared
 * artifact-browser hook + file tree, pointing at the coordinator's integration-branch adapter so the
 * panel shows the collective diff. Renders as a two-pane split (file tree on the left, the
 * read-only file viewer on the right) inside the full-width Changes/Files slide-in — clicking a
 * file selects it in place instead of opening a separate modal on top.
 */
export function CoordinatorArtifactsPanel({ runId, runStatus, adapter, initialTab = 'changes', liveUpdateKey, previewStatusSlot }: CoordinatorArtifactsPanelProps) {
  const styles = useStyles();
  const state = useArtifactBrowser(runId, runStatus, undefined, undefined, undefined, undefined, adapter, initialTab, liveUpdateKey);

  return (
    <div className={styles.root} data-testid="coord-artifacts-panel">
      <div className={styles.treeColumn}>
        <Text className={styles.hint}>
          Files produced across this run, shown as the collective integration diff.
        </Text>
        <FileTreePanel state={state} previewStatusSlot={previewStatusSlot} onFileClick={(path, isChanged) => state.handleFileSelect(path, isChanged ?? true)} />
      </div>
      <div className={styles.viewerColumn}>
        <FileViewer
          runId={runId}
          filePath={state.selectedPath}
          getContent={adapter?.getContent}
          isChanged={state.selectedPathIsChanged}
          diff={state.diff}
          diffLoading={state.diffLoading}
          diffError={state.diffError}
        />
      </div>
    </div>
  );
}
