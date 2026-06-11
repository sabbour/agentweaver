import { useState, type ReactNode } from 'react';
import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useArtifactBrowser } from '../hooks/useArtifactBrowser';
import { FileTreePanel, DiffPanel } from './ArtifactBrowser';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'row',
    height: 'calc(100vh - 160px)',
    overflow: 'hidden',
  },
  leftPanel: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    transition: 'width 0.2s',
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  leftPanelExpanded: {
    width: '260px',
  },
  leftPanelCollapsed: {
    width: '32px',
  },
  center: {
    flex: 1,
    overflow: 'auto',
    minWidth: 0,
  },
  rightPanel: {
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    transition: 'width 0.2s',
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  rightPanelExpanded: {
    width: '400px',
  },
  rightPanelCollapsed: {
    width: '32px',
  },
  toggleRow: {
    flexShrink: 0,
    display: 'flex',
    justifyContent: 'center',
    padding: tokens.spacingVerticalXS,
  },
  panelContent: {
    flex: 1,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
  },
});

interface RunLayoutProps {
  runId: string;
  runStatus: string;
  centerContent: ReactNode;
}

export function RunLayout({ runId, runStatus, centerContent }: RunLayoutProps) {
  const styles = useStyles();
  const [leftExpanded, setLeftExpanded] = useState(true);
  const [rightExpanded, setRightExpanded] = useState(true);
  const artifactState = useArtifactBrowser(runId, runStatus);

  return (
    <div className={styles.root}>
      {/* Left panel — file tree */}
      <div
        className={mergeClasses(
          styles.leftPanel,
          leftExpanded ? styles.leftPanelExpanded : styles.leftPanelCollapsed,
        )}
      >
        <div className={styles.toggleRow}>
          <Button
            appearance="subtle"
            size="small"
            onClick={() => setLeftExpanded((v) => !v)}
            aria-label={leftExpanded ? 'Collapse file tree' : 'Expand file tree'}
          >
            {leftExpanded ? '<' : '>'}
          </Button>
        </div>
        {leftExpanded && (
          <div className={styles.panelContent}>
            <FileTreePanel state={artifactState} />
          </div>
        )}
      </div>

      {/* Center — run timeline or run detail */}
      <div className={styles.center}>{centerContent}</div>

      {/* Right panel — diff viewer */}
      <div
        className={mergeClasses(
          styles.rightPanel,
          rightExpanded ? styles.rightPanelExpanded : styles.rightPanelCollapsed,
        )}
      >
        <div className={styles.toggleRow}>
          <Button
            appearance="subtle"
            size="small"
            onClick={() => setRightExpanded((v) => !v)}
            aria-label={rightExpanded ? 'Collapse diff viewer' : 'Expand diff viewer'}
          >
            {rightExpanded ? '>' : '<'}
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
