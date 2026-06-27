import { makeStyles, tokens, Tooltip } from '@fluentui/react-components';
import { ServerRegular } from '@fluentui/react-icons';

const useStyles = makeStyles({
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 6px',
    borderRadius: '999px',
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground3,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    maxWidth: '180px',
    overflow: 'hidden',
    whiteSpace: 'nowrap',
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
          className={s.pill}
          aria-label={`Executing in pod ${podName}`}
          role="status"
        >
          <ServerRegular fontSize={10} aria-hidden="true" />
          <span className={s.label}>{podName}</span>
        </span>
      </Tooltip>
    </div>
  );
}
