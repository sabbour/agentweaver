import { useCallback, useEffect, useState } from 'react';
import {
  Spinner,
  Tab,
  TabList,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
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
  root: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minHeight: 0,
    overflow: 'hidden',
  },
  content: {
    flex: 1,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
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
  emptyState: {
    display: 'flex',
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: tokens.spacingVerticalXXL,
    color: tokens.colorNeutralForeground3,
    textAlign: 'center',
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

export interface FileViewerProps {
  runId: string;
  filePath: string | null;
  /** Optional per-file content fetcher. When provided it replaces the default
   *  worktree-backed apiClient.getRunFileContent. */
  getContent?: (runId: string, path: string) => Promise<WorkspaceFileContent>;
  /** When true, the viewer renders a Diff/Preview pair (review experience). When
   *  false (the default), it renders a read-only Source/Preview pair. */
  isChanged?: boolean;
  diff?: WorkspaceFileDiff | null;
  diffLoading?: boolean;
  diffError?: string | null;
}

/**
 * Inline (non-modal) file viewer. Renders a Source/Preview (or Diff/Preview when
 * `isChanged`) tab strip plus the corresponding pane, fetching content lazily.
 * Markdown files open in the rendered preview by default; other files open in
 * source. Shows an empty state when `filePath` is null.
 */
export function FileViewer({
  runId,
  filePath,
  getContent,
  isChanged = false,
  diff = null,
  diffLoading = false,
  diffError = null,
}: FileViewerProps) {
  const styles = useStyles();
  const isMarkdown = filePath?.toLowerCase().endsWith('.md') ?? false;
  const fetchContent = useCallback(
    (rid: string, p: string): Promise<WorkspaceFileContent> =>
      (getContent ?? apiClient.getRunFileContent.bind(apiClient))(rid, p),
    [getContent],
  );

  // Default view: changed files open on Diff; otherwise markdown opens on the
  // rendered Preview and everything else on Source.
  const defaultViewMode = (): 'diff' | 'source' | 'preview' =>
    isChanged ? 'diff' : isMarkdown ? 'preview' : 'source';

  const [viewMode, setViewMode] = useState<'diff' | 'source' | 'preview'>(defaultViewMode());

  const [fileContent, setFileContent] = useState<WorkspaceFileContent | null>(null);
  const [contentLoading, setContentLoading] = useState(false);
  const [contentError, setContentError] = useState<string | null>(null);

  // Reset view mode when the file (or changed-ness) changes.
  useEffect(() => {
    setViewMode(isChanged ? 'diff' : isMarkdown ? 'preview' : 'source'); // eslint-disable-line react-hooks/set-state-in-effect
  }, [filePath, isChanged, isMarkdown]);

  // Reset content state when the file identity changes.
  useEffect(() => {
    setFileContent(null); // eslint-disable-line react-hooks/set-state-in-effect
    setContentError(null);
    setContentLoading(false);
  }, [filePath, runId]);

  // Fetch content for non-changed files (source/preview view).
  useEffect(() => {
    if (!filePath || isChanged !== false) return;

    let active = true;
    setContentLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setContentError(null);

    fetchContent(runId, filePath)
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
  }, [runId, filePath, isChanged, fetchContent]);

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

    fetchContent(runId, filePath)
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

  if (filePath === null) {
    return (
      <div className={styles.emptyState}>
        <Text>Select a file to view its contents.</Text>
      </div>
    );
  }

  return (
    <div className={styles.root} aria-label={filePath}>
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
      <div className={styles.content}>
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
      </div>
    </div>
  );
}
