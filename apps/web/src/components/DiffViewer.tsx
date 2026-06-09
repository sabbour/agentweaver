import { Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    overflow: 'auto',
    maxHeight: '480px',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  empty: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    color: tokens.colorNeutralForeground3,
  },
  pre: {
    margin: '0',
    padding: `${tokens.spacingVerticalS} 0`,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
  },
  line: {
    display: 'block',
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: tokens.spacingHorizontalM,
    whiteSpace: 'pre',
  },
  added: {
    color: tokens.colorPaletteGreenForeground1,
    backgroundColor: tokens.colorPaletteGreenBackground1,
  },
  removed: {
    color: tokens.colorPaletteRedForeground1,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  hunk: {
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  fileHeader: {
    color: tokens.colorNeutralForeground3,
  },
});

interface DiffViewerProps {
  diff: string | null;
}

export function DiffViewer({ diff }: DiffViewerProps) {
  const styles = useStyles();

  if (!diff) {
    return (
      <div className={styles.root}>
        <div className={styles.empty}>
          <Text>No changes</Text>
        </div>
      </div>
    );
  }

  const lines = diff.split('\n');

  return (
    <div className={styles.root}>
      <pre className={styles.pre}>
        {lines.map((line, i) => {
          const isAdded = line.startsWith('+') && !line.startsWith('+++');
          const isRemoved = line.startsWith('-') && !line.startsWith('---');
          const isHunk = line.startsWith('@@');
          const isFileHeader =
            line.startsWith('diff ') ||
            line.startsWith('index ') ||
            line.startsWith('--- ') ||
            line.startsWith('+++ ');

          const lineClass = mergeClasses(
            styles.line,
            isAdded && styles.added,
            isRemoved && styles.removed,
            isHunk && styles.hunk,
            isFileHeader && styles.fileHeader,
          );

          return (
            <span key={i} className={lineClass}>
              {line || ' '}
            </span>
          );
        })}
      </pre>
    </div>
  );
}
