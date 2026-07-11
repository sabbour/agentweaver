import { makeStyles, tokens } from '@fluentui/react-components';
import { Body, EmptyState, TitleText } from '../ui';
import type { DailyInvocationPointDto } from '../../api/types';

const CHART_LINE = '#635c57';
const CHART_FILL = 'rgba(99, 92, 87, 0.12)';

const useStyles = makeStyles({
  panel: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
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
      <path d={`${path} L${x(points.length - 1)},${pad.top + innerH} L${x(0)},${pad.top + innerH} Z`} fill={CHART_FILL} />
      <path d={path} fill="none" stroke={CHART_LINE} strokeWidth={2} />
    </svg>
  );
}

export function AgentInvocationChart({
  points,
  title = 'Run creation count',
  subtitle = 'Daily project run creations across the selected range.',
  emptyLabel = 'No run-creation telemetry for this range yet.',
}: {
  points?: DailyInvocationPointDto[];
  title?: string;
  subtitle?: string;
  emptyLabel?: string;
}) {
  const styles = useStyles();
  const series = points ?? [];

  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <TitleText as="h2">{title}</TitleText>
        <Body tone="muted">{subtitle}</Body>
      </div>
      {series.length === 0 ? (
        <EmptyState title={emptyLabel} />
      ) : (
        <LineChart points={series} label={title} />
      )}
    </div>
  );
}
