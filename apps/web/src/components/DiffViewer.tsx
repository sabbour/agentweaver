import React from 'react';
import { Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { Prism as SyntaxHighlighter, createElement as syntaxCreateElement } from 'react-syntax-highlighter';
import { oneLight } from 'react-syntax-highlighter/dist/esm/styles/prism';

// Local type alias matching react-syntax-highlighter's ambient rendererNode/rendererProps
interface RendererNode {
  type: 'element' | 'text';
  value?: string | number;
  tagName?: string;
  properties?: { className: string[]; [key: string]: unknown };
  children?: RendererNode[];
}
interface RendererProps {
  rows: RendererNode[];
  stylesheet: Record<string, React.CSSProperties>;
  useInlineStyles: boolean;
}

// ---------------------------------------------------------------------------
// Language detection
// ---------------------------------------------------------------------------

const EXT_TO_LANG: Record<string, string> = {
  ts: 'typescript', tsx: 'tsx', js: 'javascript', jsx: 'jsx',
  py: 'python', rs: 'rust', go: 'go', cs: 'csharp', java: 'java',
  cpp: 'cpp', c: 'c', h: 'c', hpp: 'cpp',
  json: 'json', yaml: 'yaml', yml: 'yaml', toml: 'toml',
  md: 'markdown', html: 'html', css: 'css', scss: 'scss',
  sh: 'bash', bash: 'bash', zsh: 'bash',
  xml: 'xml', sql: 'sql',
};

function detectLanguage(filename?: string): string {
  if (!filename) return 'text';
  const name = filename.split('/').pop() ?? filename;
  if (name.toLowerCase() === 'dockerfile') return 'docker';
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  return EXT_TO_LANG[ext] ?? 'text';
}

// Transparent wrapper components so SyntaxHighlighter renders <table><tbody><tr> structure
function DiffTable({ children }: { children?: React.ReactNode }) {
  return (
    <table style={{ width: '100%', borderCollapse: 'collapse', margin: 0, tableLayout: 'fixed' }}>
      {children}
    </table>
  );
}
function DiffTbody({ children }: { children?: React.ReactNode }) {
  return <tbody>{children}</tbody>;
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '13px',
  },
  fileHeader: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  filePath: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  changeCount: {
    flexShrink: 0,
    marginLeft: tokens.spacingHorizontalS,
    color: tokens.colorPaletteGreenForeground1,
  },
  scroll: {
    flex: 1,
    overflow: 'auto',
  },
  empty: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    lineHeight: tokens.lineHeightBase300,
  },
  lineNum: {
    userSelect: 'none',
    textAlign: 'right',
    paddingLeft: tokens.spacingHorizontalS,
    paddingRight: tokens.spacingHorizontalS,
    width: '40px',
    color: tokens.colorNeutralForeground4,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    whiteSpace: 'nowrap',
    verticalAlign: 'top',
  },
  lineSign: {
    userSelect: 'none',
    paddingLeft: tokens.spacingHorizontalXS,
    paddingRight: tokens.spacingHorizontalXS,
    width: '16px',
    textAlign: 'center',
    verticalAlign: 'top',
  },
  lineContent: {
    paddingLeft: tokens.spacingHorizontalXS,
    paddingRight: tokens.spacingHorizontalM,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    verticalAlign: 'top',
    width: '100%',
  },
  rowAdded: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
    color: tokens.colorPaletteGreenForeground1,
  },
  rowRemoved: {
    backgroundColor: tokens.colorPaletteRedBackground1,
    color: tokens.colorPaletteRedForeground1,
  },
  rowHunk: {
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground3,
  },
  rowFileHeader: {
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground3,
  },
});

interface DiffLine {
  oldNum: number | null;
  newNum: number | null;
  sign: string;
  content: string;
  type: 'added' | 'removed' | 'context' | 'hunk' | 'fileheader';
}

function parseDiff(diff: string): DiffLine[] {
  const result: DiffLine[] = [];
  let oldLine = 0;
  let newLine = 0;

  for (const raw of diff.split('\n')) {
    if (raw.startsWith('diff ') || raw.startsWith('index ') ||
        raw.startsWith('--- ') || raw.startsWith('+++ ')) {
      result.push({ oldNum: null, newNum: null, sign: '', content: raw, type: 'fileheader' });
      continue;
    }
    if (raw.startsWith('@@')) {
      // Parse @@ -oldStart[,oldCount] +newStart[,newCount] @@
      const m = raw.match(/^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
      if (m) {
        oldLine = parseInt(m[1], 10);
        newLine = parseInt(m[2], 10);
      }
      result.push({ oldNum: null, newNum: null, sign: '', content: raw, type: 'hunk' });
      continue;
    }
    if (raw.startsWith('+')) {
      result.push({ oldNum: null, newNum: newLine, sign: '+', content: raw.slice(1), type: 'added' });
      newLine++;
    } else if (raw.startsWith('-')) {
      result.push({ oldNum: oldLine, newNum: null, sign: '-', content: raw.slice(1), type: 'removed' });
      oldLine++;
    } else {
      const content = raw.startsWith('\\') ? raw : raw.slice(1);
      result.push({ oldNum: oldLine, newNum: newLine, sign: ' ', content, type: 'context' });
      oldLine++;
      newLine++;
    }
  }
  return result;
}

function countChanges(lines: DiffLine[]): { added: number; removed: number } {
  let added = 0; let removed = 0;
  for (const l of lines) {
    if (l.type === 'added') added++;
    else if (l.type === 'removed') removed++;
  }
  return { added, removed };
}

// ---------------------------------------------------------------------------
// Syntax-highlighted diff renderer
// ---------------------------------------------------------------------------

function HighlightedDiff({
  lines,
  lang,
  styles,
}: {
  lines: DiffLine[];
  lang: string;
  styles: ReturnType<typeof useStyles>;
}) {
  // Separate code lines (added/removed/context) from structural lines (hunk/fileheader)
  const codeLines = lines.filter(
    (l) => l.type === 'added' || l.type === 'removed' || l.type === 'context',
  );
  const code = codeLines.map((l) => l.content).join('\n');

  // Track which hunk/fileheader rows appear immediately before each code line index
  const hunksBefore = new Map<number, DiffLine[]>();
  let codeIdx = 0;
  let pending: DiffLine[] = [];
  for (const line of lines) {
    if (line.type === 'hunk' || line.type === 'fileheader') {
      pending.push(line);
    } else {
      if (pending.length > 0) {
        hunksBefore.set(codeIdx, [...pending]);
        pending = [];
      }
      codeIdx++;
    }
  }
  if (pending.length > 0) hunksBefore.set(codeLines.length, [...pending]);

  const renderer = ({ rows, stylesheet, useInlineStyles }: RendererProps) => (
    <>
      {rows.map((row: RendererNode, i: number) => {
        const codeLine = codeLines[i];
        const hunks = hunksBefore.get(i) ?? [];
        const bgColor =
          codeLine?.type === 'added'
            ? '#e6ffed'
            : codeLine?.type === 'removed'
              ? '#ffecec'
              : undefined;

        return (
          <React.Fragment key={i}>
            {hunks.map((h, j) =>
              h.type !== 'fileheader' ? (
                <tr key={`hk-${j}`} className={styles.rowHunk}>
                  <td className={styles.lineSign} />
                  <td className={styles.lineNum} />
                  <td className={styles.lineNum} />
                  <td className={styles.lineContent}>{h.content}</td>
                </tr>
              ) : null,
            )}
            {codeLine && (
              <tr style={bgColor ? { backgroundColor: bgColor } : undefined}>
                <td className={styles.lineSign}>{codeLine.sign}</td>
                <td className={styles.lineNum}>{codeLine.oldNum ?? ''}</td>
                <td className={styles.lineNum}>{codeLine.newNum ?? ''}</td>
                <td className={styles.lineContent}>
                  {row.children?.map((token: RendererNode, j: number) =>
                    syntaxCreateElement({ node: token as never, stylesheet, useInlineStyles, key: j }),
                  )}
                </td>
              </tr>
            )}
          </React.Fragment>
        );
      })}
      {(hunksBefore.get(codeLines.length) ?? []).map((h, j) =>
        h.type !== 'fileheader' ? (
          <tr key={`hkend-${j}`} className={styles.rowHunk}>
            <td className={styles.lineSign} />
            <td className={styles.lineNum} />
            <td className={styles.lineNum} />
            <td className={styles.lineContent}>{h.content}</td>
          </tr>
        ) : null,
      )}
    </>
  );

  if (!code) {
    return (
      <table style={{ width: '100%', borderCollapse: 'collapse', margin: 0 }}>
        <tbody>
          {(hunksBefore.get(0) ?? []).map((h, j) =>
            h.type !== 'fileheader' ? (
              <tr key={j} className={styles.rowHunk}>
                <td className={styles.lineSign} />
                <td className={styles.lineNum} />
                <td className={styles.lineNum} />
                <td className={styles.lineContent}>{h.content}</td>
              </tr>
            ) : null,
          )}
        </tbody>
      </table>
    );
  }

  return (
    <SyntaxHighlighter
      language={lang}
      style={oneLight}
      PreTag={DiffTable}
      CodeTag={DiffTbody}
      wrapLines={true}
      renderer={renderer as never}
      customStyle={{ fontSize: '13px', fontFamily: tokens.fontFamilyMonospace }}
    >
      {code}
    </SyntaxHighlighter>
  );
}

// ---------------------------------------------------------------------------
// Plain (non-highlighted) diff renderer — fallback for unknown file types
// ---------------------------------------------------------------------------

function PlainDiff({
  lines,
  styles,
}: {
  lines: DiffLine[];
  styles: ReturnType<typeof useStyles>;
}) {
  return (
    <table className={styles.table}>
      <tbody>
        {lines.map((line, i) => {
          const rowClass =
            line.type === 'added'
              ? styles.rowAdded
              : line.type === 'removed'
                ? styles.rowRemoved
                : line.type === 'hunk'
                  ? styles.rowHunk
                  : line.type === 'fileheader'
                    ? styles.rowFileHeader
                    : undefined;

          if (line.type === 'fileheader') return null;

          return (
            <tr key={i} className={rowClass}>
              <td className={mergeClasses(styles.lineSign, rowClass)}>{line.sign}</td>
              <td className={mergeClasses(styles.lineNum, rowClass)}>{line.oldNum ?? ''}</td>
              <td className={mergeClasses(styles.lineNum, rowClass)}>{line.newNum ?? ''}</td>
              <td className={mergeClasses(styles.lineContent, rowClass)}>
                {line.content || ' '}
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

// ---------------------------------------------------------------------------
// Public component
// ---------------------------------------------------------------------------

interface DiffViewerProps {
  diff: string | null;
  filename?: string;
}

export function DiffViewer({ diff, filename }: DiffViewerProps) {
  const styles = useStyles();
  const lang = detectLanguage(filename);
  const useSyntax = lang !== 'text';

  if (!diff) {
    return (
      <div className={styles.root}>
        {filename && (
          <div className={styles.fileHeader}>
            <span className={styles.filePath}>{filename}</span>
          </div>
        )}
        <div className={styles.empty}>
          <Text>No changes</Text>
        </div>
      </div>
    );
  }

  const lines = parseDiff(diff);
  const { added, removed } = countChanges(lines);

  return (
    <div className={styles.root}>
      {filename && (
        <div
          className={styles.fileHeader}
          style={
            useSyntax
              ? { backgroundColor: '#252526', borderColor: '#3c3c3c', color: '#cccccc' }
              : undefined
          }
        >
          <span
            className={styles.filePath}
            style={useSyntax ? { color: '#cccccc' } : undefined}
          >
            {filename}
          </span>
          <span className={styles.changeCount}>
            {added > 0 && `+${added}`}
            {removed > 0 && ` -${removed}`}
          </span>
        </div>
      )}
      <div className={styles.scroll}>
        {useSyntax ? (
          <HighlightedDiff lines={lines} lang={lang} styles={styles} />
        ) : (
          <PlainDiff lines={lines} styles={styles} />
        )}
      </div>
    </div>
  );
}
