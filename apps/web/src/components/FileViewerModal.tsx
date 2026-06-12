import { useEffect, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  Spinner,
  Tab,
  TabList,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneLight } from 'react-syntax-highlighter/dist/esm/styles/prism';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';
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
  tabStrip: {
    paddingTop: tokens.spacingVerticalXS,
    paddingBottom: tokens.spacingVerticalXS,
    paddingLeft: tokens.spacingHorizontalM,
  },
  markdownPreview: {
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXXL}`,
    overflow: 'auto',
    flex: 1,
    lineHeight: '1.6',
    fontFamily: tokens.fontFamilyBase,
  },
});

// ---------------------------------------------------------------------------
// Markdown preview renderer
// ---------------------------------------------------------------------------

function MarkdownPreview({ content, className }: { content: string; className?: string }) {
  return (
    <div className={className}>
      <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
        {content}
      </ReactMarkdown>
    </div>
  );
}

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
  const isMarkdown = filePath?.toLowerCase().endsWith('.md') ?? false;

  const [viewMode, setViewMode] = useState<'diff' | 'source' | 'preview'>(isChanged ? 'diff' : 'source');

  const [fileContent, setFileContent] = useState<WorkspaceFileContent | null>(null);
  const [contentLoading, setContentLoading] = useState(false);
  const [contentError, setContentError] = useState<string | null>(null);

  // Reset view mode when file changes
  useEffect(() => {
    setViewMode(isChanged ? 'diff' : 'source'); // eslint-disable-line react-hooks/set-state-in-effect
  }, [filePath, isChanged]);

  // Reset content state when the file identity changes
  useEffect(() => {
    setFileContent(null); // eslint-disable-line react-hooks/set-state-in-effect
    setContentError(null);
    setContentLoading(false);
  }, [filePath, runId]);

  // Fetch content for non-changed files (source view)
  useEffect(() => {
    if (!filePath || isChanged !== false) return;

    let active = true;
    setContentLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setContentError(null);

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

  // Fetch content lazily on first switch to preview (diff mode only).
  // fileContent/contentLoading intentionally omitted from deps — the guard reads
  // their current values at trigger time; adding them would re-run the effect on
  // every fetch tick and cancel in-flight requests.
  useEffect(() => {
    if (!filePath || !isMarkdown || viewMode !== 'preview' || isChanged !== true) return;
    if (fileContent !== null || contentLoading) return; // already fetched or in-flight

    let active = true;
    setContentLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setContentError(null);

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
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewMode, filePath, isMarkdown, isChanged, runId]);

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
          {isMarkdown && (
            <div className={styles.tabStrip}>
              <TabList
                selectedValue={viewMode}
                onTabSelect={(_, d) => setViewMode(d.value as typeof viewMode)}
                size="small"
              >
                {isChanged ? (
                  <>
                    <Tab value="diff">Diff</Tab>
                    <Tab value="preview">Preview</Tab>
                  </>
                ) : (
                  <>
                    <Tab value="source">Source</Tab>
                    <Tab value="preview">Preview</Tab>
                  </>
                )}
              </TabList>
            </div>
          )}
          <DialogContent className={styles.content}>
            {/* Preview pane — kept mounted once loaded to preserve scroll position */}
            {isMarkdown && (
              <div style={{ display: viewMode === 'preview' ? 'flex' : 'none', flex: 1, flexDirection: 'column', overflow: 'hidden' }}>
                {contentLoading ? (
                  <div className={styles.spinnerWrapper}>
                    <Spinner size="small" />
                  </div>
                ) : contentError ? (
                  <div className={styles.errorText}>
                    <Text>{contentError}</Text>
                  </div>
                ) : (
                  <MarkdownPreview
                    content={fileContent?.content ?? ''}
                    className={styles.markdownPreview}
                  />
                )}
              </div>
            )}
            {/* Diff / source pane — kept mounted to preserve scroll position */}
            <div style={{ display: (!isMarkdown || viewMode !== 'preview') ? 'flex' : 'none', flex: 1, flexDirection: 'column', overflow: 'hidden' }}>
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
            </div>
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
