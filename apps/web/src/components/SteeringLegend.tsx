import { Caption1, makeStyles, tokens } from '@fluentui/react-components';
import { STEERING_VERBS } from './steeringHelp';

// Compact, always-visible legend that explains the steering verbs (Send / Redirect /
// Amend) so the user can tell them apart at a glance, rather than relying on a
// hover-only tooltip.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    marginTop: tokens.spacingVerticalXS,
  },
  row: {
    color: tokens.colorNeutralForeground3,
  },
  verb: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
});

export function SteeringLegend({ className }: { className?: string }) {
  const styles = useStyles();
  return (
    <div className={className ?? styles.root} role="note" aria-label="Steering verbs">
      {STEERING_VERBS.map((v) => (
        <Caption1 key={v.kind} className={styles.row} data-testid={`steering-help-${v.kind}`}>
          <span className={styles.verb}>{v.label}</span> — {v.help}
        </Caption1>
      ))}
    </div>
  );
}
