import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  SubtractCircleRegular,
  DismissCircleRegular,
  CircleRegular,
  ArrowSyncRegular,
} from '@fluentui/react-icons';
import type { WorkflowStepItem } from '../timeline/types';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
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
  iconStarted: {
    color: tokens.colorBrandForeground1,
    display: 'flex',
  },
  iconCompleted: {
    color: tokens.colorPaletteGreenForeground1,
    display: 'flex',
  },
  iconSkipped: {
    color: tokens.colorNeutralForeground4,
    display: 'flex',
  },
  iconFailed: {
    color: tokens.colorPaletteRedForeground1,
    display: 'flex',
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
          iconClass: styles.iconSkipped,
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
          iconClass: styles.iconSkipped,
          labelClass: styles.labelMuted,
        };
    }
  })();

  return (
    <div className={styles.row} aria-label={`${item.label}: ${item.status}`}>
      <span className={mergeClasses(iconClass)} aria-hidden="true">
        {icon}
      </span>
      <span className={labelClass}>{item.label}</span>
    </div>
  );
}
