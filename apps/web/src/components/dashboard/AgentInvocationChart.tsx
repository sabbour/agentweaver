import { Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import type { DailyInvocationPointDto } from '../../api/types';

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

function LineChart({ points, label }: { points: DailyInvocationPointDto[]; label: string }) {
  const width = 720;
  const height = 180;
  const pad = { top: 12, right: 12, bottom: 24, left: 28 };
  const innerW = width - pad.left - pad.right;
  const innerH = height - pad.top - pad.bottom;
  const max = Math.max(1, ...points.map((point) => point.count));
  const x = (index: number) => pad.left + ((points.length <= 1 ? 0 : index / (points.length - 1)) * innerW);
  const y = (value: number) => pad.top + innerH - ((value / max) * innerH);
  const path = points
    .map((point, index) => `${index === 0 ? 'M' : 'L'}${x(index).toFixed(1)},${y(point.count).toFixed(1)}`)
    .join(' ');

  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} role="img" aria-label={label}>
      <line
        x1={pad.left}
        y1={pad.top + innerH}
        x2={pad.left + innerW}
        y2={pad.top + innerH}
        stroke={tokens.colorNeutralStroke2}
        strokeWidth={1}
      />
      <text x={pad.left - 6} y={pad.top + 8} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {max}
      </text>
      <text x={pad.left} y={pad.top + innerH + 16} fontSize={10} fill={tokens.colorNeutralForeground3}>
        {points[0]?.date ?? ''}
      </text>
      <text x={pad.left + innerW} y={pad.top + innerH + 16} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {points.at(-1)?.date ?? ''}
      </text>
      <path d={`${path} L${x(points.length - 1)},${pad.top + innerH} L${x(0)},${pad.top + innerH} Z`} fill="rgba(12, 124, 243, 0.12)" />
      <path d={path} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth={2} />
    </svg>
  );
}

export function AgentInvocationChart({
  points,
  title = 'Agent invocation count',
  subtitle = 'Coordinator and child run invocations across the selected range.',
}: {
  points?: DailyInvocationPointDto[];
  title?: string;
  subtitle?: string;
}) {
  const styles = useStyles();
  const series = points ?? [];

  return (
    <div className={styles.panel}>
      <div>
        <Title3>{title}</Title3>
        <Text className={styles.subtitle}>{subtitle}</Text>
      </div>
      {series.length === 0 ? (
        <Text>No invocation data yet.</Text>
      ) : (
        <LineChart points={series} label={title} />
      )}
    </div>
  );
}
