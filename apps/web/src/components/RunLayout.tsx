import { useEffect, useRef, useState, type ReactNode, type RefObject } from 'react';
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
    position: 'relative',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  leftPanel: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'row',
    position: 'relative',
    overflow: 'hidden',
    transition: 'width 0.15s ease',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  leftPanelExpanded: {
    width: '260px',
  },
  leftPanelCollapsed: {
    width: '0px',
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
  toggleButton: {
    position: 'absolute',
    top: '50%',
    transform: 'translateY(-50%)',
    zIndex: 2,
    width: '16px',
    height: '32px',
    minWidth: 'unset',
    padding: '0',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    boxShadow: tokens.shadow4,
    transition: 'left 0.15s ease',
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

  useEffect(() => {
    if (runStatus === 'awaiting_review') {
      setLeftExpanded(true);
    }
  }, [runStatus]);

  return (
    <div className={styles.root}>
      {/* Left panel — file tree, no strip */}
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
      </div>

      {/* Floating toggle button — sibling of leftPanel, absolute in root */}
      <Button
        appearance="subtle"
        className={styles.toggleButton}
        style={{
          left: leftExpanded ? '260px' : '0px',
          borderRadius: '0 4px 4px 0',
          borderLeft: 'none',
          borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
        }}
        size="small"
        onClick={() => setLeftExpanded((v) => !v)}
        aria-label={leftExpanded ? 'Collapse file tree' : 'Expand file tree'}
      >
        {leftExpanded
          ? <ChevronLeftRegular style={{ fontSize: '12px' }} />
          : <ChevronRightRegular style={{ fontSize: '12px' }} />}
      </Button>

      {/* Center — run timeline */}
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
