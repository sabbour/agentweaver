import {
  Divider,
  Spinner,
  StatusIconText } from '../copilot-fluent-system';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../copilot-fluent-system';
import { CheckmarkCircleFilled } from '../copilot-fluent-system';
import { memo } from 'react';
const useStyles = makeStyles({
  root: {
    paddingTop: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalXXS,
  },
  label: {
    fontSize: tokens.fontSizeBase200,
    flexShrink: 0,
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
    <div className={mergeClasses('azf-row azf-gap-s', styles.root)}>
      <Divider style={{ flexGrow: 1 }} />
      <StatusIconText
        className={styles.label}
        status={active ? 'info' : 'success'}
        icon={active ? <Spinner size="extra-tiny" aria-hidden="true" /> : <CheckmarkCircleFilled aria-hidden="true" />}
      >
        Turn {turnIndex}
        {stepCount > 0 && ` \u00b7 ${stepCount} ${stepWord}`}
      </StatusIconText>
      <Divider style={{ flexGrow: 1 }} />
    </div>
  );
});
