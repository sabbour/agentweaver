import { makeStyles } from '@fluentui/react-components';
import { FauxControl } from './primitives';

/** Product decision package for "Should we launch a Team pricing tier?" —
 *  synthesizes PM problem framing, PMM positioning, pricing scenarios, user
 *  journeys, and a visual prototype into one decision pack. */
const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: '#fdfbf8',
    color: '#211d1a',
    minWidth: 0,
    padding: '22px 24px 26px',
  },
  head: { marginBottom: '4px' },
  kicker: { fontSize: '11px', fontWeight: 700, letterSpacing: '0.12em', textTransform: 'uppercase', color: '#8a827b' },
  title: { margin: '5px 0 0', fontSize: '21px', fontWeight: 800, letterSpacing: '-0.01em' },
  verdict: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '7px',
    marginTop: '10px',
    padding: '5px 13px',
    borderRadius: '9999px',
    backgroundColor: '#dcf0e3',
    color: '#146c37',
    fontSize: '12.5px',
    fontWeight: 700,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '14px',
    marginTop: '18px',
    '@media (max-width: 620px)': { gridTemplateColumns: '1fr' },
  },
  card: { border: '1px solid #e7e1dc', borderRadius: '12px', padding: '15px', backgroundColor: '#ffffff' },
  cardWide: { gridColumn: '1 / -1' },
  cardTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: '7px',
    fontSize: '11px',
    fontWeight: 700,
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
    color: '#8a827b',
    marginBottom: '10px',
  },
  disc: {
    width: '18px',
    height: '18px',
    borderRadius: '5px',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '10px',
    fontWeight: 800,
    color: '#fdfbf8',
  },
  pm: { backgroundColor: '#6a4bd8' },
  pmm: { backgroundColor: '#b0442a' },
  body: { fontSize: '12.5px', lineHeight: 1.55, color: '#3f3935', margin: 0 },
  bullets: { margin: '0', paddingLeft: '16px', display: 'flex', flexDirection: 'column', gap: '5px' },
  bullet: { fontSize: '12px', lineHeight: 1.45, color: '#4a443f' },
  posRow: { display: 'flex', flexDirection: 'column', gap: '8px' },
  posLine: { fontSize: '12.5px', lineHeight: 1.5 },
  posKey: { fontWeight: 700, color: '#272320' },
  scenarios: { display: 'flex', flexDirection: 'column', gap: '9px' },
  scenario: {
    display: 'grid',
    gridTemplateColumns: '1fr auto',
    gap: '10px',
    alignItems: 'center',
    padding: '10px 12px',
    borderRadius: '9px',
    backgroundColor: '#f8f4f1',
  },
  scenarioRec: { backgroundColor: '#f4faf6', outline: '1.5px solid #c9d9cd' },
  scName: { fontSize: '13px', fontWeight: 700 },
  scMeta: { fontSize: '11.5px', color: '#8a827b', marginTop: '2px' },
  scPrice: { fontSize: '17px', fontWeight: 800, textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  scPer: { fontSize: '10.5px', color: '#8a827b', fontWeight: 600 },
  journey: { display: 'flex', gap: '8px', alignItems: 'stretch', overflowX: 'auto', paddingBottom: '4px' },
  step: {
    flex: '1 0 120px',
    borderRadius: '9px',
    backgroundColor: '#f8f4f1',
    padding: '11px',
  },
  stepNo: { fontSize: '10px', fontWeight: 700, color: '#b0442a' },
  stepName: { fontSize: '12.5px', fontWeight: 700, margin: '3px 0 4px' },
  stepText: { fontSize: '11px', lineHeight: 1.4, color: '#635c57' },
  proto: {
    borderRadius: '10px',
    overflow: 'hidden',
    border: '1px solid #e7e1dc',
    backgroundColor: '#ffffff',
  },
  protoBar: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 12px',
    backgroundColor: '#f6f2ee',
    borderBottom: '1px solid #e7e1dc',
  },
  dot: { width: '9px', height: '9px', borderRadius: '50%' },
  actions: { marginTop: '18px', display: 'flex', gap: '10px', flexWrap: 'wrap', alignItems: 'center' },
  primary: { padding: '9px 16px', borderRadius: '9px', backgroundColor: '#272320', color: '#faf6f2', fontSize: '12.5px', fontWeight: 700 },
  secondary: { padding: '9px 16px', borderRadius: '9px', border: '1px solid #d8d2cc', color: '#3f3935', fontSize: '12.5px', fontWeight: 700 },
});

export function ProductDecisionArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <div className={s.head}>
        <div className={s.kicker}>Product decision package</div>
        <h3 className={s.title}>Should we launch a Team pricing tier?</h3>
        <span className={s.verdict}>✓ Recommendation: launch Team at $24/seat · gated beta first</span>
      </div>

      <div className={s.grid}>
        <section className={s.card}>
          <div className={s.cardTitle}><span className={`${s.disc} ${s.pm}`}>PM</span>Problem &amp; market research</div>
          <p className={s.body} style={{ marginBottom: '8px' }}>
            34% of Pro accounts already share one login across 3+ people — a clear signal of
            unmet team demand and leaked revenue. Small teams churn when they hit single-seat
            collaboration limits.
          </p>
          <ul className={s.bullets}>
            <li className={s.bullet}>TAM: ~28k self-serve accounts with multi-user behavior.</li>
            <li className={s.bullet}>Top ask: shared workspaces, roles, and consolidated billing.</li>
            <li className={s.bullet}>Risk: cannibalizing Enterprise pilots if scoped too high.</li>
          </ul>
        </section>

        <section className={s.card}>
          <div className={s.cardTitle}><span className={`${s.disc} ${s.pmm}`}>PMM</span>Positioning</div>
          <div className={s.posRow}>
            <div className={s.posLine}><span className={s.posKey}>For</span> small teams outgrowing single-seat Pro</div>
            <div className={s.posLine}><span className={s.posKey}>Who need</span> shared workspaces without Enterprise procurement</div>
            <div className={s.posLine}><span className={s.posKey}>Team is</span> the fastest way to run agent workflows together</div>
            <div className={s.posLine}><span className={s.posKey}>Unlike</span> stitching Pro seats together, it centralizes roles &amp; billing</div>
          </div>
        </section>

        <section className={`${s.card} ${s.cardWide}`}>
          <div className={s.cardTitle}>Pricing scenarios</div>
          <div className={s.scenarios}>
            <div className={s.scenario}>
              <div><div className={s.scName}>Conservative</div><div className={s.scMeta}>3-seat min · low adoption friction · slower ARPA growth</div></div>
              <div><div className={s.scPrice}>$18<span className={s.scPer}> /seat</span></div></div>
            </div>
            <div className={`${s.scenario} ${s.scenarioRec}`}>
              <div><div className={s.scName}>Recommended · balanced</div><div className={s.scMeta}>3-seat min · roles + shared runs · protects Enterprise ladder</div></div>
              <div><div className={s.scPrice}>$24<span className={s.scPer}> /seat</span></div></div>
            </div>
            <div className={s.scenario}>
              <div><div className={s.scName}>Aggressive</div><div className={s.scMeta}>5-seat min · higher ARPA · adoption &amp; churn risk</div></div>
              <div><div className={s.scPrice}>$32<span className={s.scPer}> /seat</span></div></div>
            </div>
          </div>
        </section>

        <section className={`${s.card} ${s.cardWide}`}>
          <div className={s.cardTitle}>User journey</div>
          <div className={s.journey}>
            {[
              ['01', 'Hit the wall', 'Pro user invites a teammate, blocked by single-seat limits.'],
              ['02', 'Discover Team', 'In-product upsell explains shared workspaces & roles.'],
              ['03', 'Create workspace', 'Owner names the team, sets seats, adds members.'],
              ['04', 'Consolidated billing', 'One invoice; roles govern who can run & approve.'],
              ['05', 'Collaborate', 'Shared runs, reviews, and a team activity feed.'],
            ].map(([no, name, text]) => (
              <div className={s.step} key={no}>
                <div className={s.stepNo}>{no}</div>
                <div className={s.stepName}>{name}</div>
                <div className={s.stepText}>{text}</div>
              </div>
            ))}
          </div>
        </section>

        <section className={`${s.card} ${s.cardWide}`}>
          <div className={s.cardTitle}>Visual design prototype</div>
          <div className={s.proto}>
            <div className={s.protoBar}>
              <span className={s.dot} style={{ backgroundColor: '#ff5c5c' }} />
              <span className={s.dot} style={{ backgroundColor: '#ffbd44' }} />
              <span className={s.dot} style={{ backgroundColor: '#41c463' }} />
              <span style={{ fontSize: '11px', color: '#8a827b', marginLeft: '6px' }}>agentweaver.app/team/new</span>
            </div>
            <svg viewBox="0 0 640 210" role="img" aria-label="Illustrative wireframe of the Team workspace creation screen." style={{ display: 'block', width: '100%', height: 'auto' }}>
              <rect x={0} y={0} width={640} height={210} fill="#ffffff" />
              <rect x={24} y={22} width={180} height={16} rx={4} fill="#2b2622" />
              <rect x={24} y={48} width={300} height={9} rx={4} fill="#cfc7c0" />
              <rect x={24} y={82} width={360} height={44} rx={8} fill="#f2ede9" />
              <rect x={36} y={96} width={120} height={9} rx={4} fill="#a89f97" />
              <rect x={24} y={138} width={172} height={44} rx={8} fill="#f2ede9" />
              <rect x={212} y={138} width={172} height={44} rx={8} fill="#f2ede9" />
              <rect x={430} y={82} width={186} height={100} rx={10} fill="#f8f4f1" stroke="#e7e1dc" />
              <rect x={446} y={98} width={90} height={11} rx={4} fill="#2b2622" />
              <rect x={446} y={120} width={150} height={7} rx={3} fill="#cfc7c0" />
              <rect x={446} y={134} width={130} height={7} rx={3} fill="#cfc7c0" />
              <rect x={446} y={154} width={110} height={20} rx={6} fill="#272320" />
            </svg>
          </div>
        </section>
      </div>

      <div className={s.actions}>
        <FauxControl className={s.primary}>Approve for gated beta</FauxControl>
        <FauxControl className={s.secondary}>Request revisions</FauxControl>
      </div>
    </div>
  );
}
