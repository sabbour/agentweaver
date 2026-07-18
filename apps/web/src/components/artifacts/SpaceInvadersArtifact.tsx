import { makeStyles } from '@fluentui/react-components';

/** Non-playable Space Invaders-style visual. Pure SVG/CSS scene: HUD, enemy
 *  rows, player ship, shields, projectiles. No focusable or interactive controls. */
const useStyles = makeStyles({
  root: {
    fontFamily: '"Segoe UI", ui-sans-serif, system-ui, sans-serif',
    backgroundColor: '#05060f',
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
    color: '#41e86b',
    paddingBottom: '12px',
  },
  hudLives: { display: 'flex', gap: '5px', alignItems: 'center' },
  screen: {
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
});

function Invader({ x, y, fill }: { x: number; y: number; fill: string }) {
  return (
    <g transform={`translate(${x}, ${y})`} fill={fill}>
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

export function SpaceInvadersArtifact() {
  const s = useStyles();
  return (
    <div className={s.root}>
      <div className={s.hud} aria-hidden="true">
        <span>SCORE 04820</span>
        <span>HI 19750</span>
        <span className={s.hudLives}>
          LIVES
          <svg width={16} height={12} viewBox="0 0 16 12" fill="#41e86b"><polygon points="8,0 16,12 0,12" /></svg>
          <svg width={16} height={12} viewBox="0 0 16 12" fill="#41e86b"><polygon points="8,0 16,12 0,12" /></svg>
          <svg width={16} height={12} viewBox="0 0 16 12" fill="#41e86b"><polygon points="8,0 16,12 0,12" /></svg>
        </span>
      </div>

      <div className={s.screen}>
        <svg className={s.svg} viewBox="0 0 460 320" role="img" aria-label="Illustrative Space Invaders-style arcade scene: four rows of alien invaders above defensive shields and a player cannon.">
          {/* starfield */}
          {[[30, 20], [120, 60], [210, 30], [300, 90], [400, 40], [70, 180], [350, 200], [180, 220], [430, 260]].map(([cx, cy], i) => (
            <circle key={i} cx={cx} cy={cy} r={i % 3 === 0 ? 1.4 : 0.8} fill="#1d3a26" />
          ))}

          {/* enemy rows */}
          {ROWS.map((row) =>
            Array.from({ length: row.n }).map((_, c) => (
              <Invader key={`${row.y}-${c}`} x={40 + c * 48} y={row.y} fill={row.fill} />
            )),
          )}

          {/* descending enemy projectiles */}
          <rect x={112} y={168} width={3} height={12} fill="#ffd23f" />
          <rect x={256} y={196} width={3} height={12} fill="#ff5c8a" />
          <rect x={352} y={182} width={3} height={12} fill="#54c8ff" />

          {/* shields */}
          <Shield x={54} />
          <Shield x={166} />
          <Shield x={278} />
          <Shield x={390} />

          {/* player cannon + rising shot */}
          <rect x={214} y={292} width={32} height={10} rx={2} fill="#41e86b" />
          <rect x={226} y={284} width={8} height={8} fill="#41e86b" />
          <rect x={229} y={228} width={3} height={52} fill="#b6ffcb" />
        </svg>
      </div>
      <div className={s.caption}>Static visual demo — not playable</div>
    </div>
  );
}
