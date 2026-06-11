import { Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground1,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
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
    minWidth: '40px',
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
    whiteSpace: 'pre',
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

interface DiffViewerProps {
  diff: string | null;
  filename?: string;
}

export function DiffViewer({ diff, filename }: DiffViewerProps) {
  const styles = useStyles();

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
        <div className={styles.fileHeader}>
          <span className={styles.filePath}>{filename}</span>
          <span className={styles.changeCount}>
            {added > 0 && `+${added}`}{removed > 0 && ` -${removed}`}
          </span>
        </div>
      )}
      <div className={styles.scroll}>
        <table className={styles.table}>
          <tbody>
            {lines.map((line, i) => {
              const rowClass =
                line.type === 'added' ? styles.rowAdded :
                line.type === 'removed' ? styles.rowRemoved :
                line.type === 'hunk' ? styles.rowHunk :
                line.type === 'fileheader' ? styles.rowFileHeader :
                undefined;

              if (line.type === 'fileheader') return null;

              return (
                <tr key={i} className={rowClass}>
                  <td className={mergeClasses(styles.lineNum, rowClass)}>
                    {line.oldNum ?? ''}
                  </td>
                  <td className={mergeClasses(styles.lineNum, rowClass)}>
                    {line.newNum ?? ''}
                  </td>
                  <td className={mergeClasses(styles.lineSign, rowClass)}>{line.sign}</td>
                  <td className={mergeClasses(styles.lineContent, rowClass)}>
                    {line.content || ' '}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
