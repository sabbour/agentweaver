import {
  Badge,
  MessageBar,
  Spinner,
  Tab,
  TabList,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  FILTERS,
  useArtifactBrowser,
  type ArtifactBrowserState,
  type FilterValue,
} from '../hooks/useArtifactBrowser';
import { DiffViewer } from './DiffViewer';

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useFileTreeStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'hidden',
  },
  tabListWrapper: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    flexShrink: 0,
  },
  fileList: {
    overflowY: 'auto',
    flex: 1,
  },
  fileEntry: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  fileEntrySelected: {
    backgroundColor: tokens.colorNeutralBackground3,
  },
  filePath: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    flex: 1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  emptyState: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
  spinnerWrapper: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: tokens.spacingVerticalL,
  },
});

const useDiffPanelStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'hidden',
  },
  content: {
    flex: 1,
    overflow: 'auto',
  },
  placeholder: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    height: '100%',
    color: tokens.colorNeutralForeground3,
    padding: tokens.spacingHorizontalM,
  },
  spinnerWrapper: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: tokens.spacingVerticalL,
  },
  binaryNotice: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
});

// Legacy styles used by the combined ArtifactBrowser component.
const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  panels: {
    display: 'flex',
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
    height: '600px',
    overflow: 'hidden',
  },
  leftPanel: {
    width: '280px',
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
  rightPanel: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    overflow: 'hidden',
  },
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function statusBadgeColor(
  status: 'added' | 'modified' | 'deleted',
): 'success' | 'warning' | 'danger' {
  if (status === 'added') return 'success';
  if (status === 'modified') return 'warning';
  return 'danger';
}

// ---------------------------------------------------------------------------
// FileTreePanel
// ---------------------------------------------------------------------------

interface FileTreePanelProps {
  state: ArtifactBrowserState;
}

export function FileTreePanel({ state }: FileTreePanelProps) {
  const styles = useFileTreeStyles();
  const {
    activeFilter,
    isHistorical,
    handleFilterChange,
    files,
    filesLoading,
    filesError,
    selectedPath,
    handleFileSelect,
  } = state;

  return (
    <div className={styles.root}>
      <div className={styles.tabListWrapper}>
        <TabList
          size="small"
          selectedValue={activeFilter}
          onTabSelect={(_e, data) => handleFilterChange(data.value as FilterValue)}
        >
          {FILTERS.map((f) => (
            <Tab key={f.value} value={f.value} disabled={isHistorical && f.value !== 'all'}>
              {f.label}
            </Tab>
          ))}
        </TabList>
      </div>
      <div className={styles.fileList}>
        {filesLoading ? (
          <div className={styles.spinnerWrapper}>
            <Spinner size="tiny" />
          </div>
        ) : filesError ? (
          <div className={styles.emptyState}>
            <Text>{filesError}</Text>
          </div>
        ) : files.length === 0 ? (
          <div className={styles.emptyState}>
            <Text>No changes</Text>
          </div>
        ) : (
          files.map((entry) => (
            <div
              key={entry.path}
              className={
                entry.path === selectedPath
                  ? `${styles.fileEntry} ${styles.fileEntrySelected}`
                  : styles.fileEntry
              }
              onClick={() => handleFileSelect(entry.path)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  handleFileSelect(entry.path);
                }
              }}
            >
              <Text className={styles.filePath} title={entry.path}>
                {entry.path}
              </Text>
              <Badge color={statusBadgeColor(entry.status)} size="small">
                {entry.status}
              </Badge>
            </div>
          ))
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// DiffPanel
// ---------------------------------------------------------------------------

interface DiffPanelProps {
  state: ArtifactBrowserState;
}

export function DiffPanel({ state }: DiffPanelProps) {
  const styles = useDiffPanelStyles();
  const { selectedPath, diff, diffLoading, diffError } = state;

  return (
    <div className={styles.root}>
      <div className={styles.content}>
        {!selectedPath ? (
          <div className={styles.placeholder}>
            <Text>Select a file to view its diff</Text>
          </div>
        ) : diffLoading ? (
          <div className={styles.spinnerWrapper}>
            <Spinner size="tiny" />
          </div>
        ) : diffError ? (
          <div className={styles.binaryNotice}>
            <Text>{diffError}</Text>
          </div>
        ) : diff?.is_binary ? (
          <div className={styles.binaryNotice}>
            <Text>Binary file — diff not available</Text>
          </div>
        ) : (
          <DiffViewer diff={diff?.diff ?? null} />
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// ArtifactBrowser — combined component kept for backward compatibility.
// Tests import this component and expect both panels rendered together.
// ---------------------------------------------------------------------------

interface ArtifactBrowserProps {
  runId: string;
  runStatus: string;
}

export function ArtifactBrowser({ runId, runStatus }: ArtifactBrowserProps) {
  const styles = useStyles();
  const state = useArtifactBrowser(runId, runStatus);

  return (
    <div className={styles.root}>
      {state.isHistorical && (
        <MessageBar intent="info">
          Showing the artifact state at run completion.
        </MessageBar>
      )}
      <div className={styles.panels}>
        <div className={styles.leftPanel}>
          <FileTreePanel state={state} />
        </div>
        <div className={styles.rightPanel}>
          <DiffPanel state={state} />
        </div>
      </div>
    </div>
  );
}
