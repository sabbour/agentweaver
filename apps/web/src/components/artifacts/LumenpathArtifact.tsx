import { makeStyles } from '@fluentui/react-components';
import { FauxButton, FauxControl, FauxLink } from './primitives';

/**
 * Lumenpath — a fictional product-analytics brand with a nocturnal, cartographic
 * art direction: it maps the routes users take through a product. The loud,
 * saturated palette contrasts hard with Agentweaver's warm-monochrome frame to
 * prove the platform can produce work in a completely different voice.
 * No gradient text, no glassmorphism; colour is carried by flat bold fields and
 * inline SVG. The visual language is a route map (waypoints, paths, drop-offs).
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
    padding: '40px 26px 48px',
    overflow: 'hidden',
    '@media (max-width: 680px)': {
      gridTemplateColumns: '1fr',
    },
  },
  eyebrow: {
    display: 'inline-flex',
    alignItems: 'center',
    padding: '5px 12px',
    borderRadius: '9999px',
    backgroundColor: 'rgba(255, 92, 138, 0.16)',
    border: '1px solid rgba(255, 92, 138, 0.5)',
    color: '#ff89ad',
    fontSize: '12px',
    fontWeight: 700,
    letterSpacing: '0.04em',
    textTransform: 'uppercase',
    marginBottom: '18px',
  },
  h1: {
    margin: 0,
    fontSize: 'clamp(2.3rem, 5.6vw, 3.4rem)',
    lineHeight: 1.04,
    fontWeight: 850,
    letterSpacing: '-0.035em',
  },
  h1AccentPink: { color: '#ff5c8a' },
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
  heroProof: {
    marginTop: '22px',
    fontSize: '12.5px',
    fontWeight: 600,
    color: '#8fe9d4',
    letterSpacing: '0.01em',
  },
  heroArt: {
    position: 'relative',
    minHeight: '250px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  customers: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px 26px',
    padding: '15px 26px',
    backgroundColor: '#0e0720',
    borderTop: '1px solid #26183f',
    borderBottom: '1px solid #26183f',
    flexWrap: 'wrap',
  },
  customersLabel: {
    fontSize: '11px',
    fontWeight: 700,
    textTransform: 'uppercase',
    letterSpacing: '0.14em',
    color: '#8f83b8',
  },
  customerName: {
    fontSize: '14px',
    fontWeight: 800,
    letterSpacing: '-0.01em',
    color: '#d8cef5',
  },
  features: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))',
    gap: '16px',
    padding: '44px 26px',
  },
  card: {
    padding: '22px',
    borderRadius: '16px',
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
    borderRadius: '20px',
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
    fontSize: 'clamp(1.6rem, 4vw, 2.3rem)',
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
  tierHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  tierName: { fontSize: '13px', fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.06em', color: '#12e6b4' },
  tierNameFeatured: { color: '#ffffff' },
  tierBadge: {
    fontSize: '10.5px',
    fontWeight: 800,
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
    color: '#241a02',
    backgroundColor: '#ffd23f',
    padding: '3px 9px',
    borderRadius: '9999px',
  },
  price: { fontSize: '34px', fontWeight: 850, letterSpacing: '-0.03em' },
  priceUnit: { fontSize: '13px', fontWeight: 500, color: '#a99fce' },
  priceUnitFeatured: { color: '#e4dcff' },
  tierList: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  tierItem: { display: 'flex', gap: '8px', alignItems: 'baseline', fontSize: '12.5px', lineHeight: 1.4, color: '#d6cdf2' },
  tierItemFeatured: { color: '#f1ecff' },
  tierCheck: { color: '#12e6b4', fontWeight: 800, fontSize: '12px' },
  tierCheckFeatured: { color: '#ffd23f' },
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
          <FauxLink>Pricing</FauxLink>
          <FauxLink>Docs</FauxLink>
        </nav>
        <FauxButton className={s.navCta}>Start free</FauxButton>
      </header>

      <section className={s.hero}>
        <div>
          <span className={s.eyebrow}>Product journey analytics</span>
          <h1 className={s.h1}>
            Find the exact step where users <span className={s.h1AccentPink}>turn back.</span>
          </h1>
          <p className={s.lede}>
            Lumenpath stitches every session into a route map — so you can see which turns
            convert, which ones stall, and exactly who drops off.
          </p>
          <div className={s.heroCtas}>
            <FauxButton className={s.ctaPrimary}>Get started free</FauxButton>
            <FauxControl className={s.ctaGhost}>See a sample map</FauxControl>
          </div>
          <p className={s.heroProof}>First map in under a minute. No SDK rewrite, no tracking plan to design.</p>
        </div>
        <div className={s.heroArt}>
          <svg width="300" height="240" viewBox="0 0 300 240" role="img" aria-label="Journey route map">
            <rect x="14" y="18" width="272" height="204" rx="16" fill="#1e1140" stroke="#3a2470" />
            <line x1="24" y1="120" x2="276" y2="120" stroke="#2a1a52" strokeWidth="1" strokeDasharray="3 7" />
            <path d="M48 176 L104 138 L168 150 L246 74" stroke="#12e6b4" strokeWidth="4" fill="none" strokeLinecap="round" strokeLinejoin="round" />
            <path d="M168 150 L214 200" stroke="#ff5c8a" strokeWidth="3.5" fill="none" strokeLinecap="round" strokeDasharray="2 7" />
            <circle cx="48" cy="176" r="6.5" fill="#ffd23f" />
            <circle cx="104" cy="138" r="6" fill="#7b5cff" stroke="#f4efff" strokeWidth="1.5" />
            <circle cx="168" cy="150" r="7" fill="#7b5cff" stroke="#ff5c8a" strokeWidth="2.5" />
            <circle cx="246" cy="74" r="8" fill="#12e6b4" stroke="#052019" strokeWidth="2" />
            <circle cx="214" cy="200" r="5" fill="#ff5c8a" />
            <text x="48" y="196" fill="#a99fce" fontSize="8.5" textAnchor="middle" fontFamily="Segoe UI">Landing</text>
            <text x="104" y="126" fill="#a99fce" fontSize="8.5" textAnchor="middle" fontFamily="Segoe UI">Browse</text>
            <text x="168" y="170" fill="#cabff0" fontSize="8.5" textAnchor="middle" fontFamily="Segoe UI">Cart</text>
            <text x="246" y="60" fill="#8fe9d4" fontSize="8.5" textAnchor="middle" fontWeight="700" fontFamily="Segoe UI">Checkout</text>
            <text x="222" y="214" fill="#ff89ad" fontSize="8" textAnchor="middle" fontFamily="Segoe UI">Exit</text>
            <rect x="118" y="84" width="70" height="34" rx="8" fill="#140a2b" stroke="#ff5c8a" />
            <text x="127" y="101" fill="#ff5c8a" fontSize="12" fontWeight="800" fontFamily="Segoe UI">−38%</text>
            <text x="127" y="113" fill="#a99fce" fontSize="7.5" fontFamily="Segoe UI">drop at Cart</text>
          </svg>
        </div>
      </section>

      <div className={s.customers}>
        <span className={s.customersLabel}>Reading trails for</span>
        <span className={s.customerName}>Northwind</span>
        <span className={s.customerName}>Aperture</span>
        <span className={s.customerName}>Hollowtide</span>
        <span className={s.customerName}>Brightfold</span>
        <span className={s.customerName}>Kestrel</span>
      </div>

      <section className={s.features}>
        <article className={`${s.card} ${s.cardBig}`}>
          <svg className={s.cardIcon} viewBox="0 0 38 38" aria-hidden="true">
            <rect width="38" height="38" rx="11" fill="#241a02" />
            <path d="M10 26 L16 16 L22 22 L28 12" stroke="#ffd23f" strokeWidth="3" fill="none" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <span className={s.cardTitle}>One map, every surface</span>
          <span className={s.cardTextDark}>
            Stitch web, mobile, and email sessions into one route map. See the exact step
            where momentum breaks — and the segment it breaks for.
          </span>
        </article>
        <article className={s.card}>
          <svg className={s.cardIcon} viewBox="0 0 38 38" aria-hidden="true">
            <rect width="38" height="38" rx="11" fill="#12e6b4" />
            <circle cx="19" cy="19" r="8" fill="none" stroke="#0a2a22" strokeWidth="3" />
            <path d="M25 25 L30 30" stroke="#0a2a22" strokeWidth="3" strokeLinecap="round" />
          </svg>
          <span className={s.cardTitle}>Ask in plain language</span>
          <span className={s.cardText}>Type a question about a route. Get the cohort, the chart, and the reason it stalled.</span>
        </article>
        <article className={s.card}>
          <svg className={s.cardIcon} viewBox="0 0 38 38" aria-hidden="true">
            <rect width="38" height="38" rx="11" fill="#ff5c8a" />
            <path d="M19 9 L27 14 V24 L19 29 L11 24 V14 Z" fill="none" stroke="#2a0713" strokeWidth="2.6" strokeLinejoin="round" />
          </svg>
          <span className={s.cardTitle}>Guardrails on every rollout</span>
          <span className={s.cardText}>Lumenpath flags a release the moment it starts hurting a route you care about.</span>
        </article>
      </section>

      <section className={s.pricing}>
        <div className={s.pricingHead}>
          <h2 className={s.pricingTitle}>Pay for the sessions you map.</h2>
        </div>
        <div className={s.tierGrid}>
          <article className={s.tier}>
            <div className={s.tierHead}>
              <span className={s.tierName}>Spark</span>
            </div>
            <span className={s.price}>$0<span className={s.priceUnit}> /mo</span></span>
            <ul className={s.tierList}>
              <li className={s.tierItem}><span className={s.tierCheck}>✓</span>Up to 10k sessions</li>
              <li className={s.tierItem}><span className={s.tierCheck}>✓</span>3 saved routes</li>
              <li className={s.tierItem}><span className={s.tierCheck}>✓</span>Community support</li>
            </ul>
            <FauxControl className={s.tierCta}>Start free</FauxControl>
          </article>
          <article className={`${s.tier} ${s.tierFeatured}`}>
            <div className={s.tierHead}>
              <span className={`${s.tierName} ${s.tierNameFeatured}`}>Trail</span>
              <span className={s.tierBadge}>Most popular</span>
            </div>
            <span className={s.price}>$79<span className={`${s.priceUnit} ${s.priceUnitFeatured}`}> /mo</span></span>
            <ul className={s.tierList}>
              <li className={`${s.tierItem} ${s.tierItemFeatured}`}><span className={`${s.tierCheck} ${s.tierCheckFeatured}`}>✓</span>Up to 1M sessions</li>
              <li className={`${s.tierItem} ${s.tierItemFeatured}`}><span className={`${s.tierCheck} ${s.tierCheckFeatured}`}>✓</span>Unlimited routes</li>
              <li className={`${s.tierItem} ${s.tierItemFeatured}`}><span className={`${s.tierCheck} ${s.tierCheckFeatured}`}>✓</span>Plain-language insights</li>
              <li className={`${s.tierItem} ${s.tierItemFeatured}`}><span className={`${s.tierCheck} ${s.tierCheckFeatured}`}>✓</span>Rollout guardrails</li>
            </ul>
            <FauxControl className={`${s.tierCta} ${s.tierCtaFeatured}`}>Choose Trail</FauxControl>
          </article>
          <article className={s.tier}>
            <div className={s.tierHead}>
              <span className={s.tierName}>Beacon</span>
            </div>
            <span className={s.price}>Custom</span>
            <ul className={s.tierList}>
              <li className={s.tierItem}><span className={s.tierCheck}>✓</span>Unlimited volume</li>
              <li className={s.tierItem}><span className={s.tierCheck}>✓</span>SSO &amp; audit logs</li>
              <li className={s.tierItem}><span className={s.tierCheck}>✓</span>Named success engineer</li>
            </ul>
            <FauxControl className={s.tierCta}>Talk to sales</FauxControl>
          </article>
        </div>
      </section>

      <section className={s.finalCta}>
        <h2 className={s.finalTitle}>Your users left a trail today. Come read it.</h2>
        <FauxButton className={s.finalBtn}>Create your workspace →</FauxButton>
      </section>
    </div>
  );
}
