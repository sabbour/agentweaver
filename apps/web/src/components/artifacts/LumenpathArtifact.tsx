import { makeStyles } from '@fluentui/react-components';
import { FauxButton, FauxControl, FauxLink } from './primitives';

/**
 * Lumenpath — a fictional "journey-path intelligence" brand. It replays product
 * sessions as a single route map: the paths that convert, the ones that stall,
 * and the exact step a segment turns back.
 *
 * Art direction is deliberately NOT a generic SaaS template. It is editorial and
 * asymmetric: a dominant authored map surface anchors the hero, capabilities are
 * laid out with varied weight (no uniform card grid), and plans are a recommended
 * lead row with two quieter secondary rows (no symmetric three-up with a floating
 * middle). Nocturnal saturated palette — Lumenpath is a distinct brand, allowed to
 * be loud against Agentweaver's warm monochrome. No gradient text, no glass
 * (backdrop blur), no side-stripe cards, no fake hero metric, no pill soup.
 */

const NIGHT = '#160b32';
const PANEL = '#1d1147';
const INK = '#f3efff';
const MUTED = '#b6a9e0';
const DIM = '#8578ad';
const LINE = '#31215e';
const VIOLET = '#7b5cff';
const TEAL = '#19e6b0';
const CORAL = '#ff5c8a';
const AMBER = '#ffc94b';

const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: NIGHT,
    color: INK,
    minWidth: 0,
    backgroundImage:
      'linear-gradient(rgba(123,92,255,0.06) 1px, transparent 1px), linear-gradient(90deg, rgba(123,92,255,0.06) 1px, transparent 1px)',
    backgroundSize: '46px 46px',
  },

  nav: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
    padding: '16px 30px',
    borderBottom: `1px solid ${LINE}`,
    flexWrap: 'wrap',
  },
  brand: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '10px',
    fontWeight: 800,
    fontSize: '18px',
    letterSpacing: '-0.02em',
  },
  navLinks: {
    display: 'flex',
    gap: '22px',
    marginLeft: 'auto',
    marginRight: '18px',
    fontSize: '13px',
    fontWeight: 600,
    color: MUTED,
    flexWrap: 'wrap',
    '@media (max-width: 560px)': { display: 'none' },
  },
  navCta: {
    padding: '8px 16px',
    borderRadius: '9px',
    border: `1px solid ${VIOLET}`,
    color: INK,
    fontWeight: 700,
    fontSize: '13px',
  },

  hero: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 0.86fr) minmax(0, 1.14fr)',
    gap: '32px',
    alignItems: 'center',
    padding: '46px 30px 40px',
    '@media (max-width: 720px)': {
      gridTemplateColumns: '1fr',
      gap: '28px',
      padding: '34px 22px 30px',
    },
  },
  kicker: {
    fontSize: '12px',
    fontWeight: 700,
    letterSpacing: '0.16em',
    textTransform: 'uppercase',
    color: TEAL,
    marginBottom: '16px',
  },
  h1: {
    margin: 0,
    fontSize: 'clamp(2.2rem, 5vw, 3.15rem)',
    lineHeight: 1.02,
    fontWeight: 850,
    letterSpacing: '-0.038em',
  },
  h1turn: { color: CORAL },
  lede: {
    marginTop: '20px',
    maxWidth: '42ch',
    fontSize: '15px',
    lineHeight: 1.62,
    color: MUTED,
  },
  heroCtas: {
    display: 'flex',
    gap: '12px',
    marginTop: '26px',
    flexWrap: 'wrap',
  },
  ctaPrimary: {
    padding: '12px 22px',
    borderRadius: '11px',
    backgroundColor: TEAL,
    color: '#04241b',
    fontWeight: 800,
    fontSize: '14px',
  },
  ctaGhost: {
    padding: '12px 20px',
    borderRadius: '11px',
    border: `1.5px solid ${LINE}`,
    color: INK,
    fontWeight: 700,
    fontSize: '14px',
  },
  heroNote: {
    marginTop: '20px',
    fontSize: '12.5px',
    fontWeight: 600,
    color: DIM,
  },

  mapCard: {
    position: 'relative',
    borderRadius: '18px',
    backgroundColor: PANEL,
    border: `1px solid ${LINE}`,
    boxShadow: '0 26px 60px rgba(6, 2, 20, 0.55)',
    overflow: 'hidden',
  },
  mapBar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '10px',
    padding: '13px 16px',
    borderBottom: `1px solid ${LINE}`,
    backgroundColor: '#190e3d',
  },
  mapTitle: { fontSize: '12.5px', fontWeight: 700, color: INK, letterSpacing: '-0.01em' },
  mapRange: { fontSize: '11px', fontWeight: 600, color: DIM },
  mapSvg: { display: 'block', width: '100%', height: 'auto' },
  mapLegend: {
    display: 'flex',
    gap: '18px',
    padding: '11px 16px',
    borderTop: `1px solid ${LINE}`,
    backgroundColor: '#190e3d',
    flexWrap: 'wrap',
    margin: 0,
  },
  legendItem: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '7px',
    fontSize: '11px',
    fontWeight: 600,
    color: MUTED,
  },
  legendDot: { width: '9px', height: '9px', borderRadius: '3px' },

  band: {
    padding: '20px 30px 46px',
    '@media (max-width: 720px)': { padding: '8px 22px 36px' },
  },
  bandLead: {
    margin: '0 0 6px',
    fontSize: 'clamp(1.5rem, 3.6vw, 2.05rem)',
    fontWeight: 850,
    letterSpacing: '-0.03em',
    maxWidth: '18ch',
  },
  bandLeadSub: {
    margin: '0 0 30px',
    maxWidth: '52ch',
    fontSize: '14.5px',
    lineHeight: 1.6,
    color: MUTED,
  },
  splitRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.35fr) minmax(0, 0.95fr)',
    gap: '20px',
    alignItems: 'stretch',
    '@media (max-width: 660px)': { gridTemplateColumns: '1fr' },
  },
  askPanel: {
    borderRadius: '16px',
    backgroundColor: PANEL,
    border: `1px solid ${LINE}`,
    padding: '22px 22px 24px',
    display: 'flex',
    flexDirection: 'column',
  },
  askEyebrow: {
    fontSize: '11px',
    fontWeight: 700,
    letterSpacing: '0.12em',
    textTransform: 'uppercase',
    color: AMBER,
    marginBottom: '12px',
  },
  askTitle: { fontSize: '18px', fontWeight: 800, letterSpacing: '-0.02em', marginBottom: '8px' },
  askText: { fontSize: '13.5px', lineHeight: 1.58, color: MUTED, marginBottom: '18px' },
  askQuery: {
    display: 'flex',
    alignItems: 'center',
    gap: '9px',
    padding: '11px 13px',
    borderRadius: '10px',
    backgroundColor: '#120830',
    border: `1px solid ${LINE}`,
    fontSize: '13px',
    fontWeight: 600,
    color: INK,
  },
  askCaret: { color: TEAL, fontWeight: 800 },
  askAnswer: {
    marginTop: '10px',
    paddingLeft: '13px',
    fontSize: '12.5px',
    lineHeight: 1.55,
    color: MUTED,
  },
  askAnswerStrong: { color: CORAL, fontWeight: 700 },
  capList: {
    listStyle: 'none',
    margin: 0,
    padding: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  capItem: {
    display: 'grid',
    gridTemplateColumns: '20px minmax(0, 1fr)',
    gap: '12px',
    alignItems: 'start',
    padding: '13px 2px',
    borderTop: `1px solid ${LINE}`,
  },
  capItemFirst: { borderTop: 'none' },
  capGlyph: { marginTop: '2px' },
  capName: { display: 'block', fontSize: '13.5px', fontWeight: 700, color: INK, letterSpacing: '-0.01em' },
  capDetail: { display: 'block', fontSize: '12px', lineHeight: 1.5, color: DIM, marginTop: '2px' },

  plans: { padding: '4px 30px 44px', '@media (max-width: 720px)': { padding: '0 22px 36px' } },
  plansHead: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '14px',
    marginBottom: '20px',
    flexWrap: 'wrap',
  },
  plansTitle: {
    margin: 0,
    fontSize: 'clamp(1.4rem, 3.4vw, 1.95rem)',
    fontWeight: 850,
    letterSpacing: '-0.03em',
    maxWidth: '20ch',
  },
  plansNote: { fontSize: '12.5px', fontWeight: 600, color: DIM },

  leadPlan: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '20px',
    alignItems: 'center',
    padding: '24px 26px',
    borderRadius: '18px',
    backgroundColor: VIOLET,
    color: INK,
    '@media (max-width: 620px)': { gridTemplateColumns: '1fr', gap: '16px' },
  },
  leadTop: { display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '10px', flexWrap: 'wrap' },
  leadName: { fontSize: '15px', fontWeight: 850, letterSpacing: '0.01em' },
  leadBadge: {
    fontSize: '10.5px',
    fontWeight: 800,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
    color: '#2a1a02',
    backgroundColor: AMBER,
    padding: '3px 10px',
    borderRadius: '9999px',
  },
  leadIncludes: { fontSize: '13px', lineHeight: 1.55, color: '#efeaff', maxWidth: '46ch', margin: 0 },
  leadRight: {
    display: 'flex',
    alignItems: 'center',
    gap: '18px',
    '@media (max-width: 620px)': { justifyContent: 'space-between' },
  },
  leadPrice: { fontSize: '30px', fontWeight: 850, letterSpacing: '-0.03em', whiteSpace: 'nowrap' },
  leadPriceUnit: { fontSize: '13px', fontWeight: 600, color: '#d9ceff' },
  leadCta: {
    padding: '12px 20px',
    borderRadius: '10px',
    backgroundColor: NIGHT,
    color: INK,
    fontWeight: 800,
    fontSize: '13.5px',
    whiteSpace: 'nowrap',
  },

  secondaryRow: {
    marginTop: '12px',
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '12px',
    '@media (max-width: 560px)': { gridTemplateColumns: '1fr' },
  },
  minorPlan: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '16px 18px',
    borderRadius: '14px',
    backgroundColor: PANEL,
    border: `1px solid ${LINE}`,
  },
  minorLeft: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 },
  minorName: { fontSize: '13.5px', fontWeight: 800, color: INK },
  minorDetail: { fontSize: '11.5px', lineHeight: 1.45, color: DIM },
  minorPrice: { fontSize: '15px', fontWeight: 800, color: TEAL, whiteSpace: 'nowrap' },

  close: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '18px',
    margin: '0 30px 36px',
    padding: '26px 0 0',
    borderTop: `1px solid ${LINE}`,
    flexWrap: 'wrap',
    '@media (max-width: 720px)': { margin: '0 22px 30px' },
  },
  closeTitle: {
    margin: 0,
    fontSize: 'clamp(1.3rem, 3.2vw, 1.8rem)',
    fontWeight: 850,
    letterSpacing: '-0.03em',
    maxWidth: '22ch',
  },
  closeCta: {
    padding: '13px 24px',
    borderRadius: '11px',
    backgroundColor: TEAL,
    color: '#04241b',
    fontWeight: 800,
    fontSize: '14.5px',
    whiteSpace: 'nowrap',
  },
});

function Check({ color }: { color: string }) {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
      <path
        d="M3 8.5 L6.4 12 L13 4.5"
        stroke={color}
        strokeWidth="2.2"
        fill="none"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

export function LumenpathArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <header className={s.nav}>
        <span className={s.brand}>
          <svg width="26" height="26" viewBox="0 0 26 26" aria-hidden="true">
            <rect width="26" height="26" rx="8" fill={VIOLET} />
            <path
              d="M5 18 L11 8 L15 13 L21 6"
              stroke={TEAL}
              strokeWidth="2.4"
              fill="none"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
            <circle cx="11" cy="8" r="2.1" fill={AMBER} />
            <circle cx="21" cy="6" r="2.1" fill={INK} />
          </svg>
          Lumenpath
        </span>
        <nav className={s.navLinks}>
          <FauxLink>Product</FauxLink>
          <FauxLink>Pricing</FauxLink>
          <FauxLink>Changelog</FauxLink>
        </nav>
        <FauxButton className={s.navCta}>Start free</FauxButton>
      </header>

      <section className={s.hero}>
        <div>
          <p className={s.kicker}>Journey-path intelligence</p>
          <h1 className={s.h1}>
            Find the exact step a segment <span className={s.h1turn}>turns back.</span>
          </h1>
          <p className={s.lede}>
            Lumenpath replays every session as one route map — the paths that convert, the
            ones that stall, and the precise moment a cohort abandons the flow.
          </p>
          <div className={s.heroCtas}>
            <FauxButton className={s.ctaPrimary}>Map your first flow</FauxButton>
            <FauxControl className={s.ctaGhost}>Tour a live map</FauxControl>
          </div>
          <p className={s.heroNote}>Connects to your existing events. No tracking plan to redesign.</p>
        </div>

        <figure className={s.mapCard} style={{ margin: 0 }}>
          <div className={s.mapBar}>
            <span className={s.mapTitle}>Checkout flow</span>
            <span className={s.mapRange}>Mapped · last 7 days · illustrative</span>
          </div>
          <svg className={s.mapSvg} viewBox="0 0 460 250" role="img" aria-label="Journey route map">
            {[70, 140, 210, 280, 350, 420].map((x) => (
              <line key={`v${x}`} x1={x} y1={18} x2={x} y2={214} stroke="#271655" strokeWidth="1" />
            ))}
            <line x1="24" y1="150" x2="436" y2="150" stroke="#271655" strokeWidth="1" strokeDasharray="2 8" />

            <path
              d="M56 178 L150 150 L244 156 L410 70"
              stroke={TEAL}
              strokeWidth="4.5"
              fill="none"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
            <path
              d="M244 156 L318 210"
              stroke={CORAL}
              strokeWidth="3.5"
              fill="none"
              strokeLinecap="round"
              strokeDasharray="2 8"
            />

            <circle cx="56" cy="178" r="7" fill={AMBER} />
            <circle cx="150" cy="150" r="6.5" fill={VIOLET} stroke={INK} strokeWidth="1.5" />
            <circle cx="244" cy="156" r="8" fill={VIOLET} stroke={CORAL} strokeWidth="2.5" />
            <circle cx="410" cy="70" r="8.5" fill={TEAL} stroke="#04241b" strokeWidth="2" />
            <circle cx="318" cy="210" r="5.5" fill={CORAL} />

            <text x="56" y="200" fill={DIM} fontSize="9" textAnchor="middle" fontFamily="Segoe UI">Landing</text>
            <text x="150" y="138" fill={DIM} fontSize="9" textAnchor="middle" fontFamily="Segoe UI">Browse</text>
            <text x="244" y="176" fill={MUTED} fontSize="9" textAnchor="middle" fontFamily="Segoe UI">Cart</text>
            <text x="410" y="56" fill={TEAL} fontSize="9.5" textAnchor="middle" fontWeight="700" fontFamily="Segoe UI">
              Checkout
            </text>
            <text x="326" y="226" fill={CORAL} fontSize="8.5" textAnchor="middle" fontFamily="Segoe UI">Exit</text>

            <rect x="150" y="86" width="112" height="44" rx="9" fill="#120830" stroke={CORAL} />
            <text x="164" y="106" fill={CORAL} fontSize="15" fontWeight="800" fontFamily="Segoe UI">−38%</text>
            <text x="164" y="120" fill={MUTED} fontSize="8.5" fontFamily="Segoe UI">mobile drops at Cart</text>
          </svg>
          <figcaption className={s.mapLegend}>
            <span className={s.legendItem}>
              <span className={s.legendDot} style={{ backgroundColor: TEAL }} /> Converting route
            </span>
            <span className={s.legendItem}>
              <span className={s.legendDot} style={{ backgroundColor: CORAL }} /> Turn-back
            </span>
            <span className={s.legendItem}>
              <span className={s.legendDot} style={{ backgroundColor: AMBER }} /> Waypoint
            </span>
          </figcaption>
        </figure>
      </section>

      <section className={s.band}>
        <h2 className={s.bandLead}>One map for every surface.</h2>
        <p className={s.bandLeadSub}>
          Web, mobile, and email sessions stitch into a single route — so a drop-off is a
          step on the map, not a number in a table you have to reconstruct.
        </p>

        <div className={s.splitRow}>
          <div className={s.askPanel}>
            <span className={s.askEyebrow}>Ask a route</span>
            <div className={s.askTitle}>Question in. Cohort out.</div>
            <p className={s.askText}>
              Type a plain-language question about any path. Lumenpath returns the segment,
              the step that stalled it, and the size of the miss.
            </p>
            <div className={s.askQuery}>
              <span className={s.askCaret}>›</span>
              Where do mobile shoppers leave checkout?
            </div>
            <p className={s.askAnswer}>
              <span className={s.askAnswerStrong}>1,204 mobile sessions</span> abandon at Cart after
              adding an item — 38% above the desktop route.
            </p>
          </div>

          <ul className={s.capList}>
            <li className={`${s.capItem} ${s.capItemFirst}`}>
              <span className={s.capGlyph}>
                <Check color={TEAL} />
              </span>
              <span>
                <span className={s.capName}>Cross-surface stitching</span>
                <span className={s.capDetail}>Web · mobile · email joined on one identity.</span>
              </span>
            </li>
            <li className={s.capItem}>
              <span className={s.capGlyph}>
                <Check color={TEAL} />
              </span>
              <span>
                <span className={s.capName}>Segment-aware drop-off</span>
                <span className={s.capDetail}>See which cohort turns back, not just how many.</span>
              </span>
            </li>
            <li className={s.capItem}>
              <span className={s.capGlyph}>
                <Check color={TEAL} />
              </span>
              <span>
                <span className={s.capName}>Release guardrails</span>
                <span className={s.capDetail}>Flags a ship the moment a watched route slips.</span>
              </span>
            </li>
            <li className={s.capItem}>
              <span className={s.capGlyph}>
                <Check color={TEAL} />
              </span>
              <span>
                <span className={s.capName}>Warehouse sync</span>
                <span className={s.capDetail}>Every mapped route lands back in your tables.</span>
              </span>
            </li>
          </ul>
        </div>
      </section>

      <section className={s.plans}>
        <div className={s.plansHead}>
          <h2 className={s.plansTitle}>Plans scale with the sessions you map.</h2>
          <span className={s.plansNote}>Session-based · cancel anytime</span>
        </div>

        <div className={s.leadPlan}>
          <div>
            <div className={s.leadTop}>
              <span className={s.leadName}>Trail</span>
              <span className={s.leadBadge}>Most popular</span>
            </div>
            <p className={s.leadIncludes}>
              Up to 1M sessions/mo · unlimited saved routes · plain-language answers · release
              guardrails · warehouse sync.
            </p>
          </div>
          <div className={s.leadRight}>
            <span className={s.leadPrice}>
              $79<span className={s.leadPriceUnit}> /mo</span>
            </span>
            <FauxControl className={s.leadCta}>Choose Trail</FauxControl>
          </div>
        </div>

        <div className={s.secondaryRow}>
          <div className={s.minorPlan}>
            <span className={s.minorLeft}>
              <span className={s.minorName}>Spark</span>
              <span className={s.minorDetail}>Up to 10k sessions · 3 saved routes · community support.</span>
            </span>
            <span className={s.minorPrice}>Free</span>
          </div>
          <div className={s.minorPlan}>
            <span className={s.minorLeft}>
              <span className={s.minorName}>Beacon</span>
              <span className={s.minorDetail}>Unlimited volume · SSO &amp; audit · named success engineer.</span>
            </span>
            <span className={s.minorPrice}>Custom</span>
          </div>
        </div>
      </section>

      <section className={s.close}>
        <h2 className={s.closeTitle}>Your users left a trail today. Come read it.</h2>
        <FauxButton className={s.closeCta}>Create your workspace →</FauxButton>
      </section>
    </div>
  );
}
