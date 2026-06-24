import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { FileViewer } from './FileViewer';
import type { WorkspaceFileDiff, WorkspaceFileContent } from '../api/types';

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  surface: {
    width: '80vw',
    maxWidth: '1100px',
    height: '80vh',
    maxHeight: '900px',
    display: 'flex',
    flexDirection: 'column',
  },
  closeBtn: {
    position: 'absolute',
    top: tokens.spacingVerticalXS,
    right: tokens.spacingHorizontalXS,
    zIndex: 10,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    flex: 1,
    minHeight: '400px',
    position: 'relative',
  },
  content: {
    flex: 1,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    padding: 0,
  },
  actions: {
    paddingTop: tokens.spacingVerticalXS,
  },
});

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export interface FileViewerModalProps {
  runId: string;
  filePath: string | null;
  onClose: () => void;
  diff: WorkspaceFileDiff | null;
  diffLoading: boolean;
  diffError: string | null;
  isChanged?: boolean;
  /** Optional per-file content fetcher. When provided (e.g. coordinator assembly reads from the
   *  integration branch), it replaces the default worktree-backed apiClient.getRunFileContent. */
  getContent?: (runId: string, path: string) => Promise<WorkspaceFileContent>;
}

export function FileViewerModal({
  runId,
  filePath,
  onClose,
  diff,
  diffLoading,
  diffError,
  isChanged = true,
  getContent,
}: FileViewerModalProps) {
  const styles = useStyles();
  const isOpen = filePath !== null;

  return (
    <Dialog open={isOpen} onOpenChange={(_, data) => { if (!data.open) onClose(); }} modalType="modal">
      <DialogSurface className={styles.surface} aria-label={filePath ?? 'File viewer'}>
        <DialogBody className={styles.body}>
          <Button
            appearance="subtle"
            icon={<DismissRegular />}
            size="small"
            onClick={onClose}
            aria-label="Close"
            className={styles.closeBtn}
          />
          <DialogContent className={styles.content}>
            <FileViewer
              runId={runId}
              filePath={filePath}
              getContent={getContent}
              isChanged={isChanged}
              diff={diff}
              diffLoading={diffLoading}
              diffError={diffError}
            />
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
