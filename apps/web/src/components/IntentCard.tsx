import { memo } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { LightbulbRegular, WarningFilled } from '@fluentui/react-icons';
import type { ToolCallItem } from '../timeline/types';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: '1px 0',
    color: tokens.colorNeutralForeground3,
  },
  icon: {
    flexShrink: 0,
    fontSize: tokens.fontSizeBase200,
  },
  warningIcon: {
    flexShrink: 0,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteYellowForeground1,
  },
});

interface IntentCardProps {
  item: ToolCallItem;
  hasFollowingErrors?: boolean;
}

export const IntentCard = memo(function IntentCard({ item, hasFollowingErrors }: IntentCardProps) {
  const styles = useStyles();

  return (
    <div className={styles.row}>
      {hasFollowingErrors
        ? <WarningFilled className={styles.warningIcon} aria-label="Intent not fulfilled" />
        : <LightbulbRegular className={styles.icon} aria-hidden="true" />}
      <Text size={100} style={{ color: 'inherit' }}>{item.humanTitle}</Text>
    </div>
  );
});
