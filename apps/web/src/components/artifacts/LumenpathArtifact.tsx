import { makeStyles } from '@fluentui/react-components';
import { FauxButton, FauxControl, FauxLink } from './primitives';

/**
 * Lumenpath — a deliberately loud, saturated fictional marketing page.
 * It contrasts hard with Agentweaver's warm-monochrome frame to prove the
 * platform can produce work in a completely different art direction.
 * No gradient text, no glassmorphism; colour is carried by flat bold fields
 * and inline SVG.
 */
const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: '#140a2b',
    color: '#f4efff',
    minWidth: 0,
  },
  nav: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
    padding: '16px 26px',
    flexWrap: 'wrap',
  },
  brand: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '10px',
    fontWeight: 800,
    fontSize: '19px',
    letterSpacing: '-0.02em',
  },
  brandMark: {
    display: 'inline-flex',
  },
  navLinks: {
    display: 'flex',
    gap: '20px',
    fontSize: '13.5px',
    fontWeight: 600,
    color: '#c8bbef',
    flexWrap: 'wrap',
  },
  navCta: {
    padding: '9px 16px',
    borderRadius: '9999px',
    backgroundColor: '#12e6b4',
    color: '#0a2a22',
    fontWeight: 800,
    fontSize: '13.5px',
  },
  hero: {
    position: 'relative',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.1fr) minmax(0, 0.9fr)',
    gap: '28px',
    alignItems: 'center',
    padding: '40px 26px 52px',
    overflow: 'hidden',
    '@media (max-width: 680px)': {
      gridTemplateColumns: '1fr',
    },
  },
  eyebrow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '8px',
    padding: '5px 12px',
    borderRadius: '9999px',
    backgroundColor: 'rgba(255, 92, 138, 0.16)',
    border: '1px solid rgba(255, 92, 138, 0.5)',
    color: '#ff89ad',
    fontSize: '12px',
    fontWeight: 700,
    letterSpacing: '0.02em',
    marginBottom: '18px',
  },
  h1: {
    margin: 0,
    fontSize: 'clamp(2.4rem, 6vw, 3.6rem)',
    lineHeight: 1.02,
    fontWeight: 850,
    letterSpacing: '-0.035em',
  },
  h1Accent: { color: '#ffd23f' },
  h1Accent2: { color: '#12e6b4' },
  lede: {
    marginTop: '18px',
    maxWidth: '46ch',
    fontSize: '15.5px',
    lineHeight: 1.6,
    color: '#cabff0',
  },
  heroCtas: {
    display: 'flex',
    gap: '12px',
    marginTop: '26px',
    flexWrap: 'wrap',
  },
  ctaPrimary: {
    padding: '13px 22px',
    borderRadius: '12px',
    backgroundColor: '#7b5cff',
    color: '#ffffff',
    fontWeight: 800,
    fontSize: '14.5px',
    boxShadow: '0 12px 30px rgba(123, 92, 255, 0.45)',
  },
  ctaGhost: {
    padding: '13px 22px',
    borderRadius: '12px',
    border: '1.5px solid rgba(244, 239, 255, 0.35)',
    color: '#f4efff',
    fontWeight: 700,
    fontSize: '14.5px',
  },
  heroStats: {
    display: 'flex',
    gap: '26px',
    marginTop: '30px',
    flexWrap: 'wrap',
  },
  stat: { display: 'flex', flexDirection: 'column' },
  statNum: { fontSize: '22px', fontWeight: 850, color: '#12e6b4' },
  statLabel: { fontSize: '11.5px', color: '#a99fce', fontWeight: 600 },
  heroArt: {
    position: 'relative',
    minHeight: '250px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  marquee: {
    display: 'flex',
    gap: '30px',
    alignItems: 'center',
    padding: '14px 26px',
    backgroundColor: '#ff5c8a',
    color: '#2a0713',
    fontWeight: 800,
    fontSize: '13px',
    letterSpacing: '0.04em',
    flexWrap: 'wrap',
    textTransform: 'uppercase',
  },
  features: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))',
    gap: '16px',
    padding: '44px 26px',
  },
  card: {
    padding: '22px',
    borderRadius: '18px',
    backgroundColor: '#1e1140',
    border: '1px solid rgba(123, 92, 255, 0.28)',
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  cardBig: {
    gridColumn: 'span 2',
    backgroundColor: '#ffd23f',
    color: '#241a02',
    border: 'none',
    '@media (max-width: 520px)': { gridColumn: 'span 1' },
  },
  cardIcon: { width: '38px', height: '38px' },
  cardTitle: { fontSize: '16px', fontWeight: 800, letterSpacing: '-0.01em' },
  cardText: { fontSize: '13px', lineHeight: 1.55, color: '#bcb0e4' },
  cardTextDark: { fontSize: '13.5px', lineHeight: 1.55, color: '#4a3a08', fontWeight: 500 },
  pricing: {
    padding: '20px 26px 48px',
  },
  pricingHead: {
    textAlign: 'center',
    marginBottom: '26px',
  },
  pricingTitle: {
    margin: 0,
    fontSize: 'clamp(1.7rem, 4vw, 2.4rem)',
    fontWeight: 850,
    letterSpacing: '-0.03em',
  },
  tierGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(190px, 1fr))',
    gap: '16px',
  },
  tier: {
    padding: '24px 22px',
    borderRadius: '18px',
    backgroundColor: '#1e1140',
    border: '1px solid rgba(200, 187, 239, 0.18)',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  tierFeatured: {
    backgroundColor: '#7b5cff',
    border: '1px solid #a58bff',
    transform: 'translateY(-6px)',
    boxShadow: '0 20px 48px rgba(123, 92, 255, 0.4)',
  },
  tierName: { fontSize: '13px', fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.05em', color: '#12e6b4' },
  tierNameFeatured: { color: '#0a2a22', backgroundColor: '#12e6b4', alignSelf: 'flex-start', padding: '2px 10px', borderRadius: '9999px' },
  price: { fontSize: '34px', fontWeight: 850, letterSpacing: '-0.03em' },
  priceUnit: { fontSize: '13px', fontWeight: 500, color: '#a99fce' },
  tierList: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  tierItem: { display: 'flex', gap: '8px', fontSize: '12.5px', lineHeight: 1.4, color: '#d6cdf2' },
  tierCta: {
    marginTop: '4px',
    padding: '11px 16px',
    borderRadius: '10px',
    textAlign: 'center',
    fontWeight: 800,
    fontSize: '13.5px',
    backgroundColor: '#12e6b4',
    color: '#0a2a22',
  },
  tierCtaFeatured: { backgroundColor: '#ffd23f', color: '#241a02' },
  finalCta: {
    margin: '0 26px 34px',
    padding: '36px 28px',
    borderRadius: '24px',
    backgroundColor: '#12e6b4',
    color: '#052019',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '20px',
    flexWrap: 'wrap',
  },
  finalTitle: { margin: 0, fontSize: 'clamp(1.5rem, 4vw, 2.1rem)', fontWeight: 850, letterSpacing: '-0.03em', maxWidth: '20ch' },
  finalBtn: {
    padding: '14px 26px',
    borderRadius: '12px',
    backgroundColor: '#140a2b',
    color: '#f4efff',
    fontWeight: 800,
    fontSize: '15px',
  },
});

export function LumenpathArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <header className={s.nav}>
        <span className={s.brand}>
          <svg className={s.brandMark} width="26" height="26" viewBox="0 0 26 26" aria-hidden="true">
            <rect width="26" height="26" rx="8" fill="#7b5cff" />
            <path d="M6 17 L13 6 L20 17" stroke="#12e6b4" strokeWidth="2.6" fill="none" strokeLinecap="round" strokeLinejoin="round" />
            <circle cx="13" cy="17" r="2.4" fill="#ffd23f" />
          </svg>
          Lumenpath
        </span>
        <nav className={s.navLinks}>
          <FauxLink>Product</FauxLink>
          <FauxLink>Solutions</FauxLink>
          <FauxLink>Pricing</FauxLink>
          <FauxLink>Docs</FauxLink>
        </nav>
        <FauxButton className={s.navCta}>Start free</FauxButton>
      </header>

      <section className={s.hero}>
        <div>
          <span className={s.eyebrow}>◆ New · Realtime path analytics</span>
          <h1 className={s.h1}>
            Light up the <span className={s.h1Accent}>path</span> your users
            <span className={s.h1Accent2}> actually</span> take.
          </h1>
          <p className={s.lede}>
            Lumenpath turns every tap, scroll, and drop-off into a living map of intent —
            so your team ships the next thing that matters, not the next thing on the list.
          </p>
          <div className={s.heroCtas}>
            <FauxButton className={s.ctaPrimary}>Get started free</FauxButton>
            <FauxControl className={s.ctaGhost}>Watch the 2-min tour</FauxControl>
          </div>
          <div className={s.heroStats}>
            <span className={s.stat}>
              <span className={s.statNum}>3.2M</span>
              <span className={s.statLabel}>paths mapped daily</span>
            </span>
            <span className={s.stat}>
              <span className={s.statNum}>40%</span>
              <span className={s.statLabel}>faster to insight</span>
            </span>
            <span className={s.stat}>
              <span className={s.statNum}>4.9★</span>
              <span className={s.statLabel}>from 1,200 teams</span>
            </span>
          </div>
        </div>
        <div className={s.heroArt} aria-hidden="true">
          <svg width="300" height="240" viewBox="0 0 300 240" role="img" aria-label="Abstract path visualization">
            <rect x="18" y="24" width="264" height="192" rx="18" fill="#1e1140" stroke="#3a2470" />
            <path d="M40 180 C 90 120, 120 150, 160 90 S 240 60, 262 54" stroke="#12e6b4" strokeWidth="4" fill="none" strokeLinecap="round" />
            <path d="M40 196 C 100 170, 150 190, 200 150 S 250 120, 262 118" stroke="#ff5c8a" strokeWidth="4" fill="none" strokeLinecap="round" strokeDasharray="2 8" />
            <circle cx="40" cy="180" r="7" fill="#ffd23f" />
            <circle cx="160" cy="90" r="7" fill="#7b5cff" stroke="#f4efff" strokeWidth="2" />
            <circle cx="262" cy="54" r="9" fill="#12e6b4" stroke="#052019" strokeWidth="2" />
            <rect x="196" y="150" width="66" height="40" rx="9" fill="#140a2b" stroke="#7b5cff" />
            <text x="206" y="167" fill="#12e6b4" fontSize="11" fontWeight="800" fontFamily="Segoe UI">+128%</text>
            <text x="206" y="182" fill="#a99fce" fontSize="8" fontFamily="Segoe UI">checkout</text>
          </svg>
        </div>
      </section>

      <div className={s.marquee}>
        <span>◆ Northwind</span>
        <span>◆ Aperture</span>
        <span>◆ Hollowtide</span>
        <span>◆ Brightfold</span>
        <span>◆ Kestrel</span>
        <span>◆ Vantage</span>
      </div>

      <section className={s.features}>
        <article className={`${s.card} ${s.cardBig}`}>
          <svg className={s.cardIcon} viewBox="0 0 38 38" aria-hidden="true">
            <rect width="38" height="38" rx="11" fill="#241a02" />
            <path d="M10 26 L16 16 L22 22 L28 12" stroke="#ffd23f" strokeWidth="3" fill="none" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <span className={s.cardTitle}>Every journey, one canvas</span>
          <span className={s.cardTextDark}>
            Stitch sessions across web, mobile, and email into a single directed graph.
            Spot the exact step where momentum breaks — and the segment it breaks for.
          </span>
        </article>
        <article className={s.card}>
          <svg className={s.cardIcon} viewBox="0 0 38 38" aria-hidden="true">
            <rect width="38" height="38" rx="11" fill="#12e6b4" />
            <circle cx="19" cy="19" r="8" fill="none" stroke="#0a2a22" strokeWidth="3" />
            <path d="M25 25 L30 30" stroke="#0a2a22" strokeWidth="3" strokeLinecap="round" />
          </svg>
          <span className={s.cardTitle}>Answers, not dashboards</span>
          <span className={s.cardText}>Ask in plain language. Get the cohort, the chart, and the why.</span>
        </article>
        <article className={s.card}>
          <svg className={s.cardIcon} viewBox="0 0 38 38" aria-hidden="true">
            <rect width="38" height="38" rx="11" fill="#ff5c8a" />
            <path d="M19 9 L27 14 V24 L19 29 L11 24 V14 Z" fill="none" stroke="#2a0713" strokeWidth="2.6" strokeLinejoin="round" />
          </svg>
          <span className={s.cardTitle}>Ship-safe experiments</span>
          <span className={s.cardText}>Guardrails watch every rollout and flag regressions before they spread.</span>
        </article>
      </section>

      <section className={s.pricing}>
        <div className={s.pricingHead}>
          <h2 className={s.pricingTitle}>Pricing that scales with your paths</h2>
        </div>
        <div className={s.tierGrid}>
          <article className={s.tier}>
            <span className={s.tierName}>Spark</span>
            <span className={s.price}>$0<span className={s.priceUnit}> /mo</span></span>
            <ul className={s.tierList}>
              <li className={s.tierItem}>◆ Up to 10k sessions</li>
              <li className={s.tierItem}>◆ 3 saved journeys</li>
              <li className={s.tierItem}>◆ Community support</li>
            </ul>
            <FauxControl className={s.tierCta}>Start free</FauxControl>
          </article>
          <article className={`${s.tier} ${s.tierFeatured}`}>
            <span className={`${s.tierName} ${s.tierNameFeatured}`}>Most popular</span>
            <span className={s.price}>$79<span className={s.priceUnit}> /mo</span></span>
            <ul className={s.tierList}>
              <li className={s.tierItem}>◆ Up to 1M sessions</li>
              <li className={s.tierItem}>◆ Unlimited journeys</li>
              <li className={s.tierItem}>◆ Natural-language insights</li>
              <li className={s.tierItem}>◆ Experiment guardrails</li>
            </ul>
            <FauxControl className={`${s.tierCta} ${s.tierCtaFeatured}`}>Choose Growth</FauxControl>
          </article>
          <article className={s.tier}>
            <span className={s.tierName}>Beacon</span>
            <span className={s.price}>Custom</span>
            <ul className={s.tierList}>
              <li className={s.tierItem}>◆ Unlimited volume</li>
              <li className={s.tierItem}>◆ SSO &amp; audit logs</li>
              <li className={s.tierItem}>◆ Dedicated success team</li>
            </ul>
            <FauxControl className={s.tierCta}>Talk to sales</FauxControl>
          </article>
        </div>
      </section>

      <section className={s.finalCta}>
        <h2 className={s.finalTitle}>Your users are already on a path. Start seeing it.</h2>
        <FauxButton className={s.finalBtn}>Create your workspace →</FauxButton>
      </section>
    </div>
  );
}
