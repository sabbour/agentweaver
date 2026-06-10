import { memo } from 'react';
import { Divider, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { RecordRegular, CheckmarkCircleFilled } from '@fluentui/react-icons';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingTop: '2px',
    paddingBottom: '2px',
  },
  label: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  completedIcon: {
    color: tokens.colorPaletteGreenForeground1,
  },
  activeIcon: {
    color: tokens.colorBrandForeground1,
  },
});

interface TurnDividerProps {
  turnIndex: number;
  stepCount: number;
  active: boolean;
}

export const TurnDivider = memo(function TurnDivider({ turnIndex, stepCount, active }: TurnDividerProps) {
  const styles = useStyles();
  const stepWord = stepCount === 1 ? 'step' : 'steps';
  return (
    <div className={styles.root}>
      <Divider style={{ flexGrow: 1 }} />
      <Text as="span" className={styles.label}>
        {active ? (
          <>
            <RecordRegular className={styles.activeIcon} aria-hidden="true" />
            <Spinner size="extra-tiny" aria-hidden="true" />
          </>
        ) : (
          <CheckmarkCircleFilled className={styles.completedIcon} aria-hidden="true" />
        )}
        Turn {turnIndex}
        {stepCount > 0 && ` \u00b7 ${stepCount} ${stepWord}`}
      </Text>
      <Divider style={{ flexGrow: 1 }} />
    </div>
  );
});
