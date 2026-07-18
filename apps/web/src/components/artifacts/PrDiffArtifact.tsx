import { makeStyles } from '@fluentui/react-components';
import { FauxButton, FauxControl } from './primitives';

const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    color: '#1f1b18',
    backgroundColor: '#fdfbf8',
  },
  head: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '18px 20px',
    borderBottom: '1px solid #e7e1dc',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'baseline',
    flexWrap: 'wrap',
    gap: '8px',
  },
  title: {
    fontSize: '18px',
    fontWeight: 650,
    letterSpacing: '-0.01em',
  },
  number: {
    fontSize: '18px',
    fontWeight: 400,
    color: '#8a827b',
  },
  metaRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
    fontSize: '12.5px',
    color: '#635c57',
  },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '3px 10px',
    borderRadius: '9999px',
    backgroundColor: '#e7f6ec',
    color: '#146c37',
    fontWeight: 600,
  },
  branchChip: {
    fontFamily: 'ui-monospace, "Cascadia Code", "Segoe UI Mono", monospace',
    fontSize: '11.5px',
    padding: '2px 7px',
    borderRadius: '6px',
    backgroundColor: '#f1ece8',
    color: '#4a443f',
  },
  body: {
    display: 'grid',
    gridTemplateColumns: '190px minmax(0, 1fr)',
    '@media (max-width: 640px)': {
      gridTemplateColumns: '1fr',
    },
  },
  filesRail: {
    borderRight: '1px solid #e7e1dc',
    padding: '14px 12px',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    '@media (max-width: 640px)': {
      borderRight: 'none',
      borderBottom: '1px solid #e7e1dc',
    },
  },
  railLabel: {
    fontSize: '11px',
    fontWeight: 600,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: '#8a827b',
    marginBottom: '6px',
  },
  fileRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    padding: '5px 8px',
    borderRadius: '6px',
    fontSize: '12.5px',
  },
  fileRowActive: {
    backgroundColor: '#efeae7',
    fontWeight: 600,
  },
  fileName: {
    fontFamily: 'ui-monospace, "Cascadia Code", "Segoe UI Mono", monospace',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  fileStat: {
    fontSize: '11px',
    color: '#8a827b',
    flexShrink: 0,
  },
  diffWrap: {
    minWidth: 0,
    padding: '14px 16px 18px',
  },
  diffCard: {
    border: '1px solid #e7e1dc',
    borderRadius: '10px',
    overflow: 'hidden',
  },
  diffHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '8px 12px',
    backgroundColor: '#f6f2ee',
    borderBottom: '1px solid #e7e1dc',
    fontFamily: 'ui-monospace, "Cascadia Code", "Segoe UI Mono", monospace',
    fontSize: '12px',
    color: '#4a443f',
  },
  code: {
    margin: 0,
    fontFamily: 'ui-monospace, "Cascadia Code", "Segoe UI Mono", monospace',
    fontSize: '12.5px',
    lineHeight: '20px',
    overflowX: 'auto',
  },
  line: {
    display: 'grid',
    gridTemplateColumns: '38px 38px 1fr',
    columnGap: '0',
    whiteSpace: 'pre',
  },
  gutter: {
    padding: '0 8px',
    textAlign: 'right',
    color: '#b3aaa2',
    userSelect: 'none',
    backgroundColor: '#faf7f4',
  },
  lineText: {
    padding: '0 12px',
  },
  add: {
    backgroundColor: '#e7f6ec',
  },
  addText: {
    backgroundColor: '#e7f6ec',
    color: '#12522c',
  },
  del: {
    backgroundColor: '#fbe9ee',
  },
  delText: {
    backgroundColor: '#fbe9ee',
    color: '#8a1f3f',
  },
  hunk: {
    backgroundColor: '#f2eef9',
    color: '#5a4a86',
  },
  summary: {
    padding: '14px 16px',
    borderTop: '1px solid #e7e1dc',
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  summaryTitle: {
    fontSize: '13px',
    fontWeight: 650,
  },
  checkRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '12.5px',
    color: '#3f3935',
  },
  checkDot: {
    width: '14px',
    height: '14px',
    borderRadius: '50%',
    backgroundColor: '#16a149',
    color: '#fff',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '9px',
    flexShrink: 0,
  },
  actions: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    marginTop: '2px',
  },
  primary: {
    padding: '7px 14px',
    borderRadius: '8px',
    backgroundColor: '#1f7a3d',
    color: '#fff',
    fontSize: '12.5px',
    fontWeight: 600,
  },
  ghost: {
    padding: '7px 14px',
    borderRadius: '8px',
    border: '1px solid #d8d2cc',
    color: '#3f3935',
    fontSize: '12.5px',
    fontWeight: 600,
  },
});

type Row = { kind: 'ctx' | 'add' | 'del' | 'hunk'; a?: number; b?: number; text: string };

const ROWS: Row[] = [
  { kind: 'hunk', text: '@@ -18,7 +18,18 @@ export function useIdleTimeout(opts: IdleOptions) {' },
  { kind: 'ctx', a: 18, b: 18, text: '  const { timeoutMs, onIdle } = opts;' },
  { kind: 'ctx', a: 19, b: 19, text: '  const timer = useRef<number>();' },
  { kind: 'del', a: 20, text: '  const reset = () => {' },
  { kind: 'del', a: 21, text: '    clearTimeout(timer.current);' },
  { kind: 'add', b: 20, text: '  const warnAt = Math.max(0, timeoutMs - WARN_WINDOW_MS);' },
  { kind: 'add', b: 21, text: '  const reset = useCallback(() => {' },
  { kind: 'add', b: 22, text: '    window.clearTimeout(timer.current);' },
  { kind: 'add', b: 23, text: '    setState(\'active\');' },
  { kind: 'ctx', a: 22, b: 24, text: '    timer.current = window.setTimeout(() => {' },
  { kind: 'del', a: 23, text: '      onIdle();' },
  { kind: 'add', b: 25, text: '      setState(\'idle\');' },
  { kind: 'add', b: 26, text: '      onIdle?.();' },
  { kind: 'ctx', a: 24, b: 27, text: '    }, timeoutMs);' },
  { kind: 'add', b: 28, text: '  }, [timeoutMs, onIdle]);' },
  { kind: 'ctx', a: 25, b: 29, text: '  return { reset, state } as const;' },
];

export function PrDiffArtifact() {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <div className={styles.head}>
        <div className={styles.titleRow}>
          <span className={styles.title}>Add idle-warning countdown before session timeout</span>
          <span className={styles.number}>#1428</span>
        </div>
        <div className={styles.metaRow}>
          <span className={styles.statusPill}>● Ready to merge</span>
          <span>Tank wants to merge 3 commits into</span>
          <span className={styles.branchChip}>main</span>
          <span>from</span>
          <span className={styles.branchChip}>feat/idle-warning</span>
        </div>
      </div>
      <div className={styles.body}>
        <nav className={styles.filesRail} aria-label="Changed files">
          <span className={styles.railLabel}>3 files changed</span>
          <span className={`${styles.fileRow} ${styles.fileRowActive}`}>
            <span className={styles.fileName}>useIdleTimeout.ts</span>
            <span className={styles.fileStat}>+9 −4</span>
          </span>
          <span className={styles.fileRow}>
            <span className={styles.fileName}>IdleWarningDialog.tsx</span>
            <span className={styles.fileStat}>+41 −0</span>
          </span>
          <span className={styles.fileRow}>
            <span className={styles.fileName}>useIdleTimeout.test.ts</span>
            <span className={styles.fileStat}>+58 −2</span>
          </span>
        </nav>
        <div className={styles.diffWrap}>
          <div className={styles.diffCard}>
            <div className={styles.diffHeader}>
              <span>src/hooks/useIdleTimeout.ts</span>
            </div>
            <pre className={styles.code}>
              {ROWS.map((row, i) => {
                const rowCls =
                  row.kind === 'add'
                    ? styles.add
                    : row.kind === 'del'
                      ? styles.del
                      : row.kind === 'hunk'
                        ? styles.hunk
                        : undefined;
                const textCls =
                  row.kind === 'add'
                    ? styles.addText
                    : row.kind === 'del'
                      ? styles.delText
                      : undefined;
                if (row.kind === 'hunk') {
                  return (
                    <div key={i} className={styles.line}>
                      <span className={`${styles.gutter} ${styles.hunk}`} />
                      <span className={`${styles.gutter} ${styles.hunk}`} />
                      <span className={`${styles.lineText} ${styles.hunk}`}>{row.text}</span>
                    </div>
                  );
                }
                return (
                  <div key={i} className={`${styles.line} ${rowCls ?? ''}`}>
                    <span className={styles.gutter}>{row.a ?? ''}</span>
                    <span className={styles.gutter}>{row.b ?? ''}</span>
                    <span className={`${styles.lineText} ${textCls ?? ''}`}>
                      {row.kind === 'add' ? '+' : row.kind === 'del' ? '-' : ' '}
                      {row.text}
                    </span>
                  </div>
                );
              })}
            </pre>
          </div>
          <div className={styles.summary}>
            <span className={styles.summaryTitle}>Checks and review</span>
            <span className={styles.checkRow}>
              <span className={styles.checkDot}>✓</span> build · typecheck · 214 unit tests passed
            </span>
            <span className={styles.checkRow}>
              <span className={styles.checkDot}>✓</span> Coverage 91% · no new lint findings
            </span>
            <span className={styles.checkRow}>
              <span className={styles.checkDot}>✓</span> Human review requested — merge stays blocked until approved
            </span>
            <div className={styles.actions}>
              <FauxButton className={styles.primary}>Approve &amp; merge</FauxButton>
              <FauxControl className={styles.ghost}>View full diff</FauxControl>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
