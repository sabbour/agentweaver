import { useEffect, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneLight } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { DiffViewer } from './DiffViewer';
import { apiClient } from '../api/apiClient';
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
}

export function FileViewerModal({
  runId,
  filePath,
  onClose,
  diff,
  diffLoading,
  diffError,
  isChanged = true,
}: FileViewerModalProps) {
  const styles = useStyles();
  const isOpen = filePath !== null;

  const [fileContent, setFileContent] = useState<WorkspaceFileContent | null>(null);
  const [contentLoading, setContentLoading] = useState(false);
  const [contentError, setContentError] = useState<string | null>(null);

  useEffect(() => {
    if (!filePath || isChanged !== false) return;
    setContentLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setContentError(null);
    setFileContent(null);
  }, [runId, filePath, isChanged]);

  useEffect(() => {
    if (!filePath || isChanged !== false) return;

    let active = true;

    apiClient
      .getRunFileContent(runId, filePath)
      .then((data) => {
        if (active) {
          setFileContent(data);
          setContentLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (active) {
          setContentError(err instanceof Error ? err.message : String(err));
          setContentLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, [runId, filePath, isChanged]);

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
            {isChanged ? (
              diffLoading ? (
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
              )
            ) : contentLoading ? (
              <div className={styles.spinnerWrapper}>
                <Spinner size="small" />
              </div>
            ) : contentError ? (
              <div className={styles.errorText}>
                <Text>{contentError}</Text>
              </div>
            ) : fileContent?.is_binary ? (
              <div className={styles.noChangesText}>
                <Text>Binary file</Text>
              </div>
            ) : fileContent?.content === null && fileContent?.language === 'too_large' ? (
              <div className={styles.noChangesText}>
                <Text>File too large to display</Text>
              </div>
            ) : (
              <SyntaxHighlighter
                language={fileContent?.language ?? 'plaintext'}
                style={oneLight}
                showLineNumbers
                customStyle={{ margin: 0, height: '100%', overflow: 'auto', fontSize: '13px' }}
              >
                {fileContent?.content ?? ''}
              </SyntaxHighlighter>
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
