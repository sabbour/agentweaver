import {
  StatusIconText,
  Tooltip } from '../copilot-fluent-system';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import { ServerRegular } from '../copilot-fluent-system';
const useStyles = makeStyles({
  pill: {
    padding: '2px 6px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground3,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    maxWidth: '180px',
    overflow: 'hidden',
    whiteSpace: 'nowrap',
  },
  statusText: {
    minWidth: 0,
    color: tokens.colorNeutralForeground3,
    fontFamily: tokens.fontFamilyMonospace,
  },
  label: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flex: 1,
    minWidth: 0,
  },
  wrapper: {
    display: 'flex',
    justifyContent: 'center',
    marginBottom: '4px',
  },
});

interface PodIndicatorProps {
  podName: string | null | undefined;
}

/**
 * Renders a compact Kubernetes pod pill above/over an agent card.
 * Renders nothing when podName is falsy (local/dev mode).
 */
export function PodIndicator({ podName }: PodIndicatorProps) {
  const s = useStyles();
  if (!podName) return null;

  return (
    <div className={s.wrapper}>
      <Tooltip
        content={`Executing in pod ${podName}`}
        relationship="label"
        withArrow
      >
        <span
          className={mergeClasses('azf-row azf-gap-xs', s.pill)}
          aria-label={`Executing in pod ${podName}`}
          role="status"
        >
          <StatusIconText status="info" icon={<ServerRegular />} className={s.statusText}>
            <span className={s.label}>{podName}</span>
          </StatusIconText>
        </span>
      </Tooltip>
    </div>
  );
}
