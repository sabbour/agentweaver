import { makeStyles, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    position: 'relative',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },
  svg: {
    display: 'block',
    imageRendering: 'pixelated',
  },
  badge: {
    position: 'absolute',
    bottom: '-2px',
    right: '-2px',
    width: '14px',
    height: '14px',
    borderRadius: '50%',
    background: tokens.colorBrandBackground,
    border: `2px solid ${tokens.colorNeutralBackground1}`,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
});

/** 5×5 pixel-art grid deterministically generated from a name string */
function generatePixels(name: string): boolean[][] {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = Math.imul(31, hash) + name.charCodeAt(i) | 0;
  }
  // Mirror left half to right half for symmetry (GitHub-identicon style)
  const grid: boolean[][] = [];
  for (let row = 0; row < 5; row++) {
    const rowBits: boolean[] = [];
    for (let col = 0; col < 5; col++) {
      const mirrorCol = col < 3 ? col : 4 - col;
      const bit = (hash >>> (row * 3 + mirrorCol)) & 1;
      rowBits.push(bit === 1);
    }
    grid.push(rowBits);
  }
  return grid;
}

/** Pastel palette derived from name hash */
function avatarColor(name: string): { bg: string; fg: string } {
  let hash = 5381;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) + hash) + name.charCodeAt(i) | 0;
  }
  const hue = Math.abs(hash) % 360;
  return {
    bg: `hsl(${hue}, 60%, 90%)`,
    fg: `hsl(${hue}, 60%, 30%)`,
  };
}

interface AgentAvatarProps {
  name: string;
  size?: number;
  isBuiltIn?: boolean;
  isRetired?: boolean;
}

export function AgentAvatar({ name, size = 40, isBuiltIn = false, isRetired = false }: AgentAvatarProps) {
  const styles = useStyles();
  const pixels = generatePixels(name);
  const { bg, fg } = avatarColor(name);
  const cellSize = Math.floor(size / 5);
  const svgSize = cellSize * 5;
  const opacity = isRetired ? 0.45 : 1;

  return (
    <div className={styles.root} style={{ width: size, height: size }}>
      <svg
        className={styles.svg}
        width={svgSize}
        height={svgSize}
        viewBox={`0 0 5 5`}
        aria-hidden="true"
        style={{ opacity, borderRadius: '4px' }}
      >
        <rect x="0" y="0" width="5" height="5" fill={bg} />
        {pixels.map((row, rowIdx) =>
          row.map((on, colIdx) =>
            on ? (
              <rect
                key={`${rowIdx}-${colIdx}`}
                x={colIdx}
                y={rowIdx}
                width="1"
                height="1"
                fill={fg}
              />
            ) : null,
          ),
        )}
      </svg>
      {isBuiltIn && (
        <div className={styles.badge} title="Built-in system agent">
          <svg width="8" height="8" viewBox="0 0 8 8" aria-hidden="true">
            <path d="M4 1 L5.2 3.2 L7.6 3.6 L5.8 5.4 L6.2 7.8 L4 6.7 L1.8 7.8 L2.2 5.4 L0.4 3.6 L2.8 3.2 Z" fill="white" />
          </svg>
        </div>
      )}
    </div>
  );
}
