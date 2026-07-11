import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  SubtractCircleRegular,
  } from '../copilot-fluent-system';
import { StatusIconText,
  makeStyles,
  tokens,
} from '../copilot-fluent-system';
import type { AzfTone } from '../copilot-fluent-system';
import type { WorkflowStepItem } from '../timeline/types';
const useStyles = makeStyles({
  row: {
    paddingBlock: '2px',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  labelMuted: {
    color: tokens.colorNeutralForeground4,
  },
});

interface WorkflowStepCardProps {
  item: WorkflowStepItem;
}

export function WorkflowStepCard({ item }: WorkflowStepCardProps) {
  const styles = useStyles();

  const { icon, tone, labelClass } = (() => {
    switch (item.status) {
      case 'started':
        return {
          icon: <ArrowSyncRegular fontSize={14} />,
          tone: 'info' as AzfTone,
          labelClass: styles.label,
        };
      case 'completed':
        return {
          icon: <CheckmarkCircleRegular fontSize={14} />,
          tone: 'success' as AzfTone,
          labelClass: styles.labelMuted,
        };
      case 'skipped':
        return {
          icon: <SubtractCircleRegular fontSize={14} />,
          tone: 'neutral' as AzfTone,
          labelClass: styles.labelMuted,
        };
      case 'failed':
        return {
          icon: <DismissCircleRegular fontSize={14} />,
          tone: 'danger' as AzfTone,
          labelClass: styles.label,
        };
      default:
        return {
          icon: <CircleRegular fontSize={14} />,
          tone: 'neutral' as AzfTone,
          labelClass: styles.labelMuted,
        };
    }
  })();

  return (
    <div className={`${styles.row} azf-row azf-gap-s`} aria-label={`${item.label}: ${item.status}`}>
      <StatusIconText status={tone} icon={icon} className={labelClass}>
        {item.agentName != null ? `${item.agentName} (${item.label})` : item.label}
      </StatusIconText>
    </div>
  );
}
