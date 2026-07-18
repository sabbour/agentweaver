import { makeStyles } from '@fluentui/react-components';

/**
 * Non-playable Space Invaders-style demo. Pure SVG scene animated with CSS
 * transform/opacity keyframes only (no JS timers, no layout thrashing):
 *   - the invader formation marches side-to-side and drifts downward,
 *   - each invader does the classic two-frame "leg" shuffle,
 *   - enemy projectiles rain down while the player cannon returns fire,
 *   - a parallax starfield twinkles and drifts.
 *
 * All controls are decorative and non-focusable. With prefers-reduced-motion,
 * every animation is frozen at a composed pose (marching mid-step, shots in
 * flight) so the still frame still reads as a real scene — never blank.
 */

const BG = '#05060f';
const GREEN = '#41e86b';

const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: BG,
    color: '#e6ffe9',
    minWidth: 0,
    padding: '18px 20px 22px',
  },
  hud: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    fontFamily: 'ui-monospace, "Cascadia Code", monospace',
    fontSize: '13px',
    letterSpacing: '0.08em',
    color: GREEN,
    paddingBottom: '12px',
  },
  hudLives: { display: 'flex', gap: '5px', alignItems: 'center' },
  screen: {
    position: 'relative',
    borderRadius: '10px',
    backgroundColor: '#020309',
    border: '1px solid #16351f',
    overflow: 'hidden',
  },
  svg: { display: 'block', width: '100%', height: 'auto' },
  caption: {
    marginTop: '10px',
    fontSize: '11.5px',
    color: '#5f7a66',
    fontFamily: 'ui-monospace, monospace',
    textAlign: 'center',
    letterSpacing: '0.05em',
  },

  // Formation: side-to-side march (discrete steps) nested inside a slow descent.
  descent: {
    animationName: {
      '0%': { transform: 'translateY(0)' },
      '100%': { transform: 'translateY(16px)' },
    },
    animationDuration: '9s',
    animationTimingFunction: 'ease-in-out',
    animationIterationCount: 'infinite',
    animationDirection: 'alternate',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
      transform: 'translateY(8px)',
    },
  },
  march: {
    animationName: {
      '0%': { transform: 'translateX(-14px)' },
      '100%': { transform: 'translateX(14px)' },
    },
    animationDuration: '3.6s',
    animationTimingFunction: 'steps(11, end)',
    animationIterationCount: 'infinite',
    animationDirection: 'alternate',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
      transform: 'translateX(6px)',
    },
  },
  // The two-frame leg shuffle, applied to the inner group of each invader.
  legs: {
    transformBox: 'fill-box',
    transformOrigin: 'center',
    animationName: {
      '0%': { transform: 'scaleX(1)' },
      '100%': { transform: 'scaleX(0.82)' },
    },
    animationDuration: '0.72s',
    animationTimingFunction: 'steps(2, end)',
    animationIterationCount: 'infinite',
    animationDirection: 'alternate',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
  },
  enemyShot: {
    animationName: {
      '0%': { transform: 'translateY(0)', opacity: 1 },
      '88%': { opacity: 1 },
      '100%': { transform: 'translateY(120px)', opacity: 0 },
    },
    animationDuration: '1.5s',
    animationTimingFunction: 'linear',
    animationIterationCount: 'infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
      transform: 'translateY(60px)',
    },
  },
  enemyShot2: { animationDelay: '0.6s' },
  enemyShot3: { animationDelay: '1s' },
  playerShot: {
    animationName: {
      '0%': { transform: 'translateY(0)', opacity: 1 },
      '85%': { opacity: 1 },
      '100%': { transform: 'translateY(-92px)', opacity: 0 },
    },
    animationDuration: '1s',
    animationTimingFunction: 'linear',
    animationIterationCount: 'infinite',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
      transform: 'translateY(-46px)',
    },
  },
  ship: {
    animationName: {
      '0%': { transform: 'translateX(-12px)' },
      '100%': { transform: 'translateX(12px)' },
    },
    animationDuration: '4.2s',
    animationTimingFunction: 'ease-in-out',
    animationIterationCount: 'infinite',
    animationDirection: 'alternate',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
      transform: 'translateX(0)',
    },
  },
  star: {
    animationName: {
      '0%': { opacity: 0.28, transform: 'translateY(0)' },
      '100%': { opacity: 1, transform: 'translateY(6px)' },
    },
    animationDuration: '3.4s',
    animationTimingFunction: 'ease-in-out',
    animationIterationCount: 'infinite',
    animationDirection: 'alternate',
    '@media (prefers-reduced-motion: reduce)': {
      animationName: 'none',
      opacity: 0.7,
    },
  },
});

function Invader({ x, y, fill, legsClass }: { x: number; y: number; fill: string; legsClass: string }) {
  return (
    <g transform={`translate(${x}, ${y})`}>
      <g className={legsClass} fill={fill}>
        <rect x={6} y={0} width={4} height={4} />
        <rect x={16} y={0} width={4} height={4} />
        <rect x={2} y={4} width={22} height={4} />
        <rect x={0} y={8} width={26} height={4} />
        <rect x={0} y={12} width={6} height={4} />
        <rect x={10} y={12} width={6} height={4} />
        <rect x={20} y={12} width={6} height={4} />
        <rect x={4} y={16} width={4} height={4} />
        <rect x={18} y={16} width={4} height={4} />
      </g>
    </g>
  );
}

function Shield({ x }: { x: number }) {
  return (
    <g transform={`translate(${x}, 250)`} fill="#2f7d43">
      <rect x={0} y={8} width={48} height={20} rx={2} />
      <rect x={0} y={0} width={48} height={12} rx={6} />
      <rect x={16} y={16} width={16} height={14} fill="#020309" />
      <rect x={6} y={14} width={6} height={5} fill="#0a2913" />
      <rect x={36} y={14} width={5} height={5} fill="#0a2913" />
    </g>
  );
}

const ROWS: { y: number; fill: string; n: number }[] = [
  { y: 40, fill: '#ff5c8a', n: 8 },
  { y: 72, fill: '#ffd23f', n: 8 },
  { y: 104, fill: '#41e86b', n: 8 },
  { y: 136, fill: '#54c8ff', n: 8 },
];

const STARS: [number, number][] = [
  [30, 20], [120, 60], [210, 30], [300, 90], [400, 40], [70, 180], [350, 200], [180, 220], [430, 260],
];

export function SpaceInvadersArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <div className={s.hud} aria-hidden="true">
        <span>SCORE 04820</span>
        <span>HI 19750</span>
        <span className={s.hudLives}>
          LIVES
          <svg width={16} height={12} viewBox="0 0 16 12" fill={GREEN}><polygon points="8,0 16,12 0,12" /></svg>
          <svg width={16} height={12} viewBox="0 0 16 12" fill={GREEN}><polygon points="8,0 16,12 0,12" /></svg>
          <svg width={16} height={12} viewBox="0 0 16 12" fill={GREEN}><polygon points="8,0 16,12 0,12" /></svg>
        </span>
      </div>

      <div className={s.screen}>
        <svg
          className={s.svg}
          viewBox="0 0 460 320"
          role="img"
          aria-label="Illustrative Space Invaders-style arcade scene: four rows of alien invaders march above defensive shields while a player cannon fires."
        >
          {/* parallax starfield */}
          {STARS.map(([cx, cy], i) => (
            <circle
              key={i}
              className={s.star}
              cx={cx}
              cy={cy}
              r={i % 3 === 0 ? 1.4 : 0.8}
              fill="#2a5236"
              style={{ animationDelay: `${(i % 5) * 0.4}s` }}
            />
          ))}

          {/* marching enemy formation */}
          <g className={s.descent}>
            <g className={s.march}>
              {ROWS.map((row) =>
                Array.from({ length: row.n }).map((_, c) => (
                  <Invader key={`${row.y}-${c}`} x={40 + c * 48} y={row.y} fill={row.fill} legsClass={s.legs} />
                )),
              )}
            </g>
          </g>

          {/* descending enemy projectiles */}
          <rect className={s.enemyShot} x={112} y={168} width={3} height={12} fill="#ffd23f" />
          <rect className={`${s.enemyShot} ${s.enemyShot2}`} x={256} y={196} width={3} height={12} fill="#ff5c8a" />
          <rect className={`${s.enemyShot} ${s.enemyShot3}`} x={352} y={182} width={3} height={12} fill="#54c8ff" />

          {/* shields */}
          <Shield x={54} />
          <Shield x={166} />
          <Shield x={278} />
          <Shield x={390} />

          {/* player cannon + rising shot */}
          <g className={s.ship}>
            <rect x={214} y={292} width={32} height={10} rx={2} fill={GREEN} />
            <rect x={226} y={284} width={8} height={8} fill={GREEN} />
            <rect className={s.playerShot} x={229} y={244} width={3} height={30} fill="#b6ffcb" />
          </g>
        </svg>
      </div>
      <div className={s.caption}>Animated visual demo — not playable</div>
    </div>
  );
}
