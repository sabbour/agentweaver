import { makeStyles } from '@fluentui/react-components';
import { FauxControl, FauxLink } from './primitives';

/** A single, standalone, beautifully set article page. No release bundle, no
 *  social variants — just one well-typeset long-form post. */
const useStyles = makeStyles({
  root: {
    fontFamily: 'Georgia, "Iowan Old Style", "Times New Roman", serif',
    backgroundColor: '#fbf9f5',
    color: '#211d1a',
    minWidth: 0,
  },
  topbar: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '14px 32px',
    borderBottom: '1px solid #e6ded3',
    fontFamily: '"Segoe UI", system-ui, sans-serif',
  },
  masthead: { fontWeight: 800, letterSpacing: '0.14em', fontSize: '13px', textTransform: 'uppercase' },
  topLinks: { display: 'flex', gap: '18px', fontSize: '12.5px', color: '#726a60', fontWeight: 600 },
  article: {
    maxWidth: '660px',
    margin: '0 auto',
    padding: '40px 32px 52px',
  },
  kicker: {
    fontFamily: '"Segoe UI", system-ui, sans-serif',
    textTransform: 'uppercase',
    letterSpacing: '0.14em',
    fontSize: '12px',
    fontWeight: 700,
    color: '#b0442a',
    marginBottom: '16px',
  },
  h1: {
    margin: 0,
    fontSize: 'clamp(2.1rem, 5vw, 3rem)',
    lineHeight: 1.08,
    fontWeight: 700,
    letterSpacing: '-0.01em',
  },
  standfirst: {
    marginTop: '18px',
    fontSize: '18px',
    lineHeight: 1.5,
    color: '#4c453d',
    fontStyle: 'italic',
  },
  byline: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    marginTop: '24px',
    paddingBottom: '22px',
    borderBottom: '1px solid #e6ded3',
    fontFamily: '"Segoe UI", system-ui, sans-serif',
  },
  avatar: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: '#b0442a',
    color: '#fff',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 800,
    fontSize: '15px',
    flexShrink: 0,
  },
  bylineMeta: { display: 'flex', flexDirection: 'column', fontSize: '13px' },
  bylineName: { fontWeight: 700, color: '#211d1a' },
  bylineSub: { color: '#847a6e', fontSize: '12px' },
  hero: {
    margin: '28px 0',
    borderRadius: '12px',
    overflow: 'hidden',
    border: '1px solid #e6ded3',
  },
  p: {
    fontSize: '18px',
    lineHeight: 1.72,
    margin: '0 0 22px',
  },
  dropcapFirst: {
    '::first-letter': {
      float: 'left',
      fontSize: '58px',
      lineHeight: '46px',
      paddingRight: '10px',
      paddingTop: '4px',
      fontWeight: 700,
      color: '#b0442a',
    },
  },
  h2: {
    fontSize: '24px',
    fontWeight: 700,
    margin: '34px 0 14px',
    letterSpacing: '-0.01em',
  },
  quote: {
    margin: '34px 0',
    padding: '18px 0 0',
    borderTop: '2px solid #b0442a',
    fontSize: '25px',
    lineHeight: 1.4,
    fontStyle: 'italic',
    color: '#3a342d',
    textAlign: 'center',
  },
  list: { margin: '0 0 22px', paddingLeft: '22px' },
  li: { fontSize: '18px', lineHeight: 1.7, marginBottom: '8px' },
  tags: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    marginTop: '32px',
    paddingTop: '22px',
    borderTop: '1px solid #e6ded3',
    fontFamily: '"Segoe UI", system-ui, sans-serif',
  },
  tag: {
    padding: '5px 12px',
    borderRadius: '9999px',
    backgroundColor: '#f0e9de',
    color: '#6a6055',
    fontSize: '12px',
    fontWeight: 600,
  },
});

export function BlogPostArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <header className={s.topbar}>
        <span className={s.masthead}>The Long Loop</span>
        <nav className={s.topLinks}>
          <FauxLink>Essays</FauxLink>
          <FauxLink>Field notes</FauxLink>
          <FauxLink>Subscribe</FauxLink>
        </nav>
      </header>
      <article className={s.article}>
        <p className={s.kicker}>Engineering culture</p>
        <h1 className={s.h1}>The quiet cost of the ten-minute code review</h1>
        <p className={s.standfirst}>
          We optimised our pipelines for speed and our reviews for throughput. Then we
          wondered why nobody trusted the merge button anymore.
        </p>
        <div className={s.byline}>
          <span className={s.avatar}>DL</span>
          <span className={s.bylineMeta}>
            <span className={s.bylineName}>Dana Lund</span>
            <span className={s.bylineSub}>9 min read · Illustrative byline</span>
          </span>
        </div>

        <figure className={s.hero} aria-hidden="true">
          <svg width="100%" height="200" viewBox="0 0 660 200" role="img" aria-label="Abstract review timeline">
            <rect width="660" height="200" fill="#f3ece0" />
            <line x1="40" y1="150" x2="620" y2="150" stroke="#cabca6" strokeWidth="2" />
            {[80, 190, 300, 410, 520].map((x, i) => (
              <g key={x}>
                <line x1={x} y1="60" x2={x} y2="150" stroke="#d8cbb6" strokeWidth="10" strokeLinecap="round" opacity={0.4 + i * 0.12} />
                <circle cx={x} cy="150" r="6" fill={i === 3 ? '#b0442a' : '#8a7c66'} />
              </g>
            ))}
            <path d="M80 60 Q 300 12 520 44" stroke="#b0442a" strokeWidth="2.5" fill="none" strokeDasharray="3 6" />
          </svg>
        </figure>

        <p className={`${s.p} ${s.dropcapFirst}`}>
          There is a particular kind of exhaustion that comes from approving code you did
          not really read. It starts small — a green check, a familiar author, a diff that
          looks about right — and it compounds until the review is a ritual rather than a
          judgement. The team is fast. The team is also, quietly, guessing.
        </p>
        <p className={s.p}>
          When we measured it, the median review on our busiest repository lasted just under
          eleven minutes and touched four hundred lines. No human reads four hundred lines of
          unfamiliar logic with real comprehension in eleven minutes. What we had built was a
          machine for manufacturing the appearance of scrutiny.
        </p>

        <h2 className={s.h2}>Speed was never the enemy</h2>
        <p className={s.p}>
          The instinct is to slow down, but that misreads the problem. Reviewers were not
          careless; they were overloaded and under-context. The diff arrived without the story
          that produced it — the goal, the constraints, the paths not taken. So they reviewed
          the letters and missed the meaning.
        </p>
        <blockquote className={s.quote}>
          A review is only as good as the context that arrives with the change.
        </blockquote>
        <p className={s.p}>
          The teams that recovered trust did three unglamorous things:
        </p>
        <ul className={s.list}>
          <li className={s.li}>They made the intent of a change reviewable, not just its lines.</li>
          <li className={s.li}>They shrank the unit of review until comprehension was possible.</li>
          <li className={s.li}>They kept a human decision at the gate, and protected the time to make it.</li>
        </ul>
        <p className={s.p}>
          None of that is a tool you can buy in an afternoon. It is a posture: treat the merge
          button as a promise, and give the person pressing it what they need to keep it.
        </p>

        <div className={s.tags}>
          <FauxControl className={s.tag}>#code-review</FauxControl>
          <FauxControl className={s.tag}>#engineering</FauxControl>
          <FauxControl className={s.tag}>#trust</FauxControl>
        </div>
      </article>
    </div>
  );
}
