import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  CircleRegular,
  DismissCircleRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import type { WorkflowStepItem } from '../timeline/types';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingBlock: '2px',
  },
  label: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  labelMuted: {
    color: tokens.colorNeutralForeground4,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  icon: {
    display: 'flex',
    flexShrink: 0,
  },
  iconStarted: {
    color: tokens.colorPaletteMarigoldForeground2,
  },
  iconCompleted: {
    color: tokens.colorPaletteGreenForeground1,
  },
  iconFailed: {
    color: tokens.colorPaletteRedForeground1,
  },
  iconMuted: {
    color: tokens.colorNeutralForeground4,
  },
});

interface WorkflowStepCardProps {
  item: WorkflowStepItem;
}

export function WorkflowStepCard({ item }: WorkflowStepCardProps) {
  const styles = useStyles();

  const { icon, iconClass, labelClass } = (() => {
    switch (item.status) {
      case 'started':
        return {
          icon: <ArrowSyncRegular fontSize={14} />,
          iconClass: styles.iconStarted,
          labelClass: styles.label,
        };
      case 'completed':
        return {
          icon: <CheckmarkCircleRegular fontSize={14} />,
          iconClass: styles.iconCompleted,
          labelClass: styles.labelMuted,
        };
      case 'skipped':
        return {
          icon: <SubtractCircleRegular fontSize={14} />,
          iconClass: styles.iconMuted,
          labelClass: styles.labelMuted,
        };
      case 'failed':
        return {
          icon: <DismissCircleRegular fontSize={14} />,
          iconClass: styles.iconFailed,
          labelClass: styles.label,
        };
      default:
        return {
          icon: <CircleRegular fontSize={14} />,
          iconClass: styles.iconMuted,
          labelClass: styles.labelMuted,
        };
    }
  })();

  return (
    <div className={styles.row} aria-label={`${item.label}: ${item.status}`}>
      <span className={mergeClasses(styles.icon, iconClass)} aria-hidden="true">{icon}</span>
      <Text className={labelClass}>
        {item.agentName != null ? `${item.agentName} (${item.label})` : item.label}
      </Text>
    </div>
  );
}
