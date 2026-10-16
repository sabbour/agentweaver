import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Text,
  tokens,
  } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { FileViewer } from './FileViewer';
import type { WorkspaceFileContent, WorkspaceFileDiff } from '../api/types';
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
  subtitle: {
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    paddingBottom: tokens.spacingVerticalXS,
    color: tokens.colorNeutralForeground3,
    fontFamily: tokens.fontFamilyMonospace,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    display: 'block',
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
  const fileName = filePath?.split('/').pop() ?? 'File viewer';

  return (
    <Dialog open={isOpen} onOpenChange={(_, data) => { if (!data.open) onClose(); }} modalType="modal">
      <DialogSurface className={styles.surface} aria-label={filePath ?? 'File viewer'}>
        <DialogBody className={styles.body}>
          <DialogTitle
            action={
              <Button
                appearance="subtle"
                aria-label="Close"
                icon={<DismissRegular />}
                onClick={onClose}
              />
            }
          >
            {fileName}
          </DialogTitle>
          {filePath && (
            <Text className={styles.subtitle}>{filePath}</Text>
          )}
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
