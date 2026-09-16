import { makeStyles } from '@fluentui/react-components';
import { FauxControl } from './primitives';

/** Competitor evaluation framework. Ratings are explicitly illustrative — the
 *  point is the weighted method, not fabricated vendor facts. */
const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: '#fdfbf8',
    color: '#211d1a',
    minWidth: 0,
    padding: '20px 22px 24px',
  },
  head: { marginBottom: '14px' },
  title: { margin: 0, fontSize: '19px', fontWeight: 700, letterSpacing: '-0.01em' },
  sub: { margin: '6px 0 0', fontSize: '13px', color: '#635c57', lineHeight: 1.5 },
  illus: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    marginTop: '10px',
    padding: '4px 11px',
    borderRadius: '8px',
    backgroundColor: '#fdf1df',
    border: '1px solid #f0d9b0',
    color: '#8a4b01',
    fontSize: '11.5px',
    fontWeight: 700,
  },
  weights: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
    gap: '10px',
    margin: '16px 0',
  },
  weightCard: {
    border: '1px solid #e7e1dc',
    borderRadius: '10px',
    padding: '11px 13px',
    backgroundColor: '#ffffff',
  },
  weightPct: { fontSize: '20px', fontWeight: 800, color: '#272320' },
  weightName: { fontSize: '12px', fontWeight: 600, color: '#3f3935', marginTop: '2px' },
  weightDesc: { fontSize: '11px', color: '#8a827b', marginTop: '3px', lineHeight: 1.4 },
  tableWrap: { overflowX: 'auto', borderRadius: '12px', border: '1px solid #e7e1dc' },
  table: { width: '100%', borderCollapse: 'collapse', minWidth: '520px' },
  th: {
    textAlign: 'left',
    padding: '10px 12px',
    fontSize: '11.5px',
    fontWeight: 700,
    color: '#635c57',
    backgroundColor: '#f6f2ee',
    borderBottom: '1px solid #e7e1dc',
    whiteSpace: 'nowrap',
  },
  thNum: { textAlign: 'center' },
  td: {
    padding: '10px 12px',
    fontSize: '12.5px',
    borderBottom: '1px solid #efeae7',
    color: '#3f3935',
  },
  criterion: { fontWeight: 600 },
  score: { textAlign: 'center', fontWeight: 700, fontVariantNumeric: 'tabular-nums' },
  bar: {
    display: 'inline-block',
    width: '30px',
    height: '6px',
    borderRadius: '9999px',
    backgroundColor: '#efeae7',
    marginTop: '3px',
    position: 'relative',
    overflow: 'hidden',
  },
  totalRow: { backgroundColor: '#f8f4f1' },
  totalCell: { fontWeight: 800, fontSize: '13.5px' },
  winner: { color: '#146c37' },
  notes: { marginTop: '18px' },
  notesTitle: {
    fontSize: '11px',
    fontWeight: 700,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: '#8a827b',
    marginBottom: '8px',
  },
  noteList: { margin: 0, paddingLeft: '18px', display: 'flex', flexDirection: 'column', gap: '6px' },
  note: { fontSize: '12px', lineHeight: 1.5, color: '#4a443f' },
  noteLink: { color: '#635c57', textDecoration: 'underline', textDecorationStyle: 'dotted' },
});

const VENDORS = ['GitHub Copilot', 'Cursor', 'Claude Code', 'OpenAI Codex'];
// Illustrative 1–5 ratings ONLY. Not sourced vendor benchmarks.
const ROWS: { crit: string; weight: number; scores: number[] }[] = [
  { crit: 'Repository understanding', weight: 0.3, scores: [4, 5, 5, 4] },
  { crit: 'Enterprise controls', weight: 0.3, scores: [5, 3, 4, 4] },
  { crit: 'Workflow fit (TS + .NET)', weight: 0.25, scores: [5, 4, 4, 3] },
  { crit: 'Cost model at 60 seats', weight: 0.15, scores: [4, 3, 3, 4] },
];

function weighted(i: number): number {
  return ROWS.reduce((sum, r) => sum + r.weight * r.scores[i], 0);
}

export function CompetitorEvalArtifact() {
  const s = useStyles();
  const totals = VENDORS.map((_, i) => weighted(i));
  const best = totals.indexOf(Math.max(...totals));
  return (
    <div className={s.root}>
      <div className={s.head}>
        <h3 className={s.title}>Coding-agent evaluation · 60-engineer TypeScript + .NET org</h3>
        <p className={s.sub}>
          A weighted decision framework across four criteria. Fill the matrix from your own
          trials — the ratings below are a worked illustration of the method, not vendor benchmarks.
        </p>
        <span className={s.illus}>◆ Illustrative ratings — validate against your own pilot</span>
      </div>

      <div className={s.weights}>
        {ROWS.map((r) => (
          <div className={s.weightCard} key={r.crit}>
            <div className={s.weightPct}>{Math.round(r.weight * 100)}%</div>
            <div className={s.weightName}>{r.crit}</div>
            <div className={s.weightDesc}>Weight applied to each vendor&apos;s 1–5 rating</div>
          </div>
        ))}
      </div>

      <div className={s.tableWrap}>
        <table className={s.table}>
          <thead>
            <tr>
              <th className={s.th}>Criterion</th>
              <th className={`${s.th} ${s.thNum}`}>Weight</th>
              {VENDORS.map((v) => (
                <th className={`${s.th} ${s.thNum}`} key={v}>{v}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {ROWS.map((r) => (
              <tr key={r.crit}>
                <td className={`${s.td} ${s.criterion}`}>{r.crit}</td>
                <td className={`${s.td} ${s.score}`}>{Math.round(r.weight * 100)}%</td>
                {r.scores.map((sc, i) => (
                  <td className={`${s.td} ${s.score}`} key={i}>{sc.toFixed(1)}</td>
                ))}
              </tr>
            ))}
            <tr className={s.totalRow}>
              <td className={`${s.td} ${s.totalCell}`}>Weighted total</td>
              <td className={`${s.td} ${s.score}`}>100%</td>
              {totals.map((t, i) => (
                <td
                  className={`${s.td} ${s.score} ${s.totalCell} ${i === best ? s.winner : ''}`}
                  key={i}
                >
                  {t.toFixed(2)}
                </td>
              ))}
            </tr>
          </tbody>
        </table>
      </div>

      <div className={s.notes}>
        <div className={s.notesTitle}>Illustrative source notes</div>
        <ol className={s.noteList}>
          <li className={s.note}>
            Repository understanding — reflects how each tool indexed a mixed TS/.NET monorepo in
            a scoped trial. <FauxControl className={s.noteLink}>internal-trial-notes</FauxControl> (illustrative).
          </li>
          <li className={s.note}>
            Enterprise controls — SSO, audit, data-retention, and admin policy posture per each
            vendor&apos;s public docs at evaluation time. <FauxControl className={s.noteLink}>vendor-docs-snapshot</FauxControl> (illustrative).
          </li>
          <li className={s.note}>
            Cost model — list pricing modelled at 60 seats plus estimated usage; confirm current
            terms before deciding. <FauxControl className={s.noteLink}>pricing-worksheet</FauxControl> (illustrative).
          </li>
        </ol>
      </div>
    </div>
  );
}
