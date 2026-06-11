import { useRef, useState, type ReactNode, type RefObject } from 'react';
import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ChevronLeftRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import { FileTreePanel } from './ArtifactBrowser';
import { FileViewerModal } from './FileViewerModal';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'row',
    height: 'calc(100vh - 140px)',
    overflow: 'hidden',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  leftPanel: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'row',
    overflow: 'hidden',
    transition: 'width 0.15s ease',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  leftPanelExpanded: {
    width: '260px',
  },
  leftPanelCollapsed: {
    width: '28px',
  },
  center: {
    flex: 1,
    overflow: 'auto',
    minWidth: 0,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  panelContent: {
    flex: 1,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  toggleStrip: {
    flexShrink: 0,
    width: '20px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'flex-start',
    paddingTop: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  toggleStripLeft: {
    borderLeft: 'none',
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  toggleButton: {
    minWidth: '20px',
    width: '20px',
    height: '44px',
    padding: '0',
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    ':hover': {
      color: tokens.colorNeutralForeground1,
      backgroundColor: tokens.colorNeutralBackground3,
    },
  },
});

interface RunLayoutProps {
  runId: string;
  runStatus: string;
  centerContent: ReactNode;
  centerScrollRef?: RefObject<HTMLDivElement | null>;
  onCenterScroll?: () => void;
  onRequestChangesSuccess?: () => void;
  onCommitSuccess?: () => void;
}

export function RunLayout({ runId, runStatus, centerContent, centerScrollRef, onCenterScroll, onRequestChangesSuccess, onCommitSuccess }: RunLayoutProps) {
  const styles = useStyles();
  const [leftExpanded, setLeftExpanded] = useState(true);
  const artifactState = useArtifactBrowser(runId, runStatus, onRequestChangesSuccess, onCommitSuccess);
  const internalRef = useRef<HTMLDivElement>(null);
  const scrollRef = centerScrollRef ?? internalRef;

  const handleFileClick = (path: string, isChanged = true) => {
    artifactState.handleFileSelect(path, isChanged);
  };

  return (
    <div className={styles.root}>
      {/* Left panel — file tree with tabs */}
      <div
        className={mergeClasses(
          styles.leftPanel,
          leftExpanded ? styles.leftPanelExpanded : styles.leftPanelCollapsed,
        )}
      >
        {leftExpanded && (
          <div className={styles.panelContent}>
            <FileTreePanel state={artifactState} onFileClick={handleFileClick} />
          </div>
        )}
        <div className={mergeClasses(styles.toggleStrip, styles.toggleStripLeft)}>
          <Button
            appearance="subtle"
            className={styles.toggleButton}
            size="small"
            onClick={() => setLeftExpanded((v) => !v)}
            aria-label={leftExpanded ? 'Collapse file tree' : 'Expand file tree'}
          >
            {leftExpanded
              ? <ChevronLeftRegular style={{ fontSize: '16px' }} />
              : <ChevronRightRegular style={{ fontSize: '16px' }} />}
          </Button>
        </div>
      </div>

      {/* Center — run timeline or run detail */}
      <div ref={scrollRef} onScroll={onCenterScroll} className={styles.center}>{centerContent}</div>

      {/* File viewer modal — opens when a file is selected */}
      <FileViewerModal
        runId={runId}
        filePath={artifactState.selectedPath}
        onClose={artifactState.clearSelection}
        diff={artifactState.diff}
        diffLoading={artifactState.diffLoading}
        diffError={artifactState.diffError}
        isChanged={artifactState.selectedPathIsChanged}
      />
    </div>
  );
}
