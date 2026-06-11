import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { DiffViewer } from './DiffViewer';
import type { WorkspaceFileDiff } from '../api/types';

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  surface: {
    width: 'min(900px, 70vw)',
    maxWidth: '900px',
    minWidth: '480px',
    maxHeight: '80vh',
    display: 'flex',
    flexDirection: 'column',
  },
  titleRow: {
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalS,
    overflow: 'hidden',
  },
  fileName: {
    fontFamily: tokens.fontFamilyMonospace,
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flexShrink: 1,
  },
  folderPath: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flexShrink: 2,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    flex: 1,
    minHeight: '400px',
  },
  content: {
    flex: 1,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
  },
  spinnerWrapper: {
    display: 'flex',
    justifyContent: 'center',
    alignItems: 'center',
    padding: tokens.spacingVerticalXXL,
    flex: 1,
  },
  errorText: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    color: tokens.colorPaletteRedForeground1,
  },
  noChangesText: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    paddingTop: tokens.spacingVerticalXS,
  },
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function splitPath(filePath: string): { name: string; folder: string } {
  const idx = filePath.lastIndexOf('/');
  if (idx === -1) return { name: filePath, folder: '' };
  return { name: filePath.slice(idx + 1), folder: filePath.slice(0, idx) };
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export interface FileViewerModalProps {
  filePath: string | null;
  onClose: () => void;
  diff: WorkspaceFileDiff | null;
  diffLoading: boolean;
  diffError: string | null;
  isChangedFile: boolean;
}

export function FileViewerModal({
  filePath,
  onClose,
  diff,
  diffLoading,
  diffError,
  isChangedFile,
}: FileViewerModalProps) {
  const styles = useStyles();
  const isOpen = filePath !== null;

  const { name, folder } = filePath ? splitPath(filePath) : { name: '', folder: '' };

  return (
    <Dialog open={isOpen} onOpenChange={(_, data) => { if (!data.open) onClose(); }} modalType="modal">
      <DialogSurface className={styles.surface}>
        <DialogBody className={styles.body}>
          <DialogTitle
            action={
              <Button
                appearance="subtle"
                icon={<DismissRegular />}
                onClick={onClose}
                aria-label="Close"
              />
            }
          >
            <div className={styles.titleRow}>
              <span className={styles.fileName}>{name}</span>
              {folder && <span className={styles.folderPath}>{folder}</span>}
            </div>
          </DialogTitle>
          <DialogContent className={styles.content}>
            {!isChangedFile ? (
              <div className={styles.noChangesText}>
                <Text>No changes in this file</Text>
              </div>
            ) : diffLoading ? (
              <div className={styles.spinnerWrapper}>
                <Spinner size="small" />
              </div>
            ) : diffError ? (
              <div className={styles.errorText}>
                <Text>{diffError}</Text>
              </div>
            ) : diff?.is_binary ? (
              <div className={styles.noChangesText}>
                <Text>Binary file — diff not available</Text>
              </div>
            ) : (
              <DiffViewer diff={diff?.diff ?? null} filename={filePath ?? undefined} />
            )}
          </DialogContent>
          <DialogActions className={styles.actions}>
            <Button appearance="secondary" onClick={onClose}>
              Close
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
