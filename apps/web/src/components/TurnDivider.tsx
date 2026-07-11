import {
  Divider,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { makeStyles,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleFilled } from '@fluentui/react-icons';
import { memo } from 'react';
const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
  },
  label: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    fontSize: tokens.fontSizeBase200,
    flexShrink: 0,
  },
  labelIcon: {
    display: 'inline-flex',
    flexShrink: 0,
    alignItems: 'center',
    fontSize: '14px',
    lineHeight: '1',
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
      <span className={styles.label}>
        <span className={styles.labelIcon}>
          {active ? <Spinner size="extra-tiny" aria-hidden="true" /> : <CheckmarkCircleFilled aria-hidden="true" />}
        </span>
        <Text size={200}>
          Turn {turnIndex}
          {stepCount > 0 && ` \u00b7 ${stepCount} ${stepWord}`}
        </Text>
      </span>
      <Divider style={{ flexGrow: 1 }} />
    </div>
  );
});
