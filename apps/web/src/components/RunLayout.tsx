import { useRef, useState, type ReactNode, type RefObject } from 'react';
import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { ChevronLeftRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import { FileTreePanel, DiffPanel } from './ArtifactBrowser';

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
    width: '240px',
  },
  leftPanelCollapsed: {
    width: '28px',
  },
  rightPanel: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'row',
    overflow: 'hidden',
    transition: 'width 0.15s ease',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  rightPanelExpanded: {
    width: '480px',
  },
  rightPanelCollapsed: {
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
}

export function RunLayout({ runId, runStatus, centerContent, centerScrollRef, onCenterScroll }: RunLayoutProps) {
  const styles = useStyles();
  const [leftExpanded, setLeftExpanded] = useState(true);
  const [rightExpanded, setRightExpanded] = useState(true);
  const artifactState = useArtifactBrowser(runId, runStatus);
  const internalRef = useRef<HTMLDivElement>(null);
  const scrollRef = centerScrollRef ?? internalRef;

  return (
    <div className={styles.root}>
      {/* Left panel — file tree */}
      <div
        className={mergeClasses(
          styles.leftPanel,
          leftExpanded ? styles.leftPanelExpanded : styles.leftPanelCollapsed,
        )}
      >
        {leftExpanded && (
          <div className={styles.panelContent}>
            <FileTreePanel state={artifactState} />
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

      {/* Right panel — diff viewer */}
      <div
        className={mergeClasses(
          styles.rightPanel,
          rightExpanded ? styles.rightPanelExpanded : styles.rightPanelCollapsed,
        )}
      >
        <div className={styles.toggleStrip}>
          <Button
            appearance="subtle"
            className={styles.toggleButton}
            size="small"
            onClick={() => setRightExpanded((v) => !v)}
            aria-label={rightExpanded ? 'Collapse diff viewer' : 'Expand diff viewer'}
          >
            {rightExpanded
              ? <ChevronRightRegular style={{ fontSize: '16px' }} />
              : <ChevronLeftRegular style={{ fontSize: '16px' }} />}
          </Button>
        </div>
        {rightExpanded && (
          <div className={styles.panelContent}>
            <DiffPanel state={artifactState} />
          </div>
        )}
      </div>
    </div>
  );
}
