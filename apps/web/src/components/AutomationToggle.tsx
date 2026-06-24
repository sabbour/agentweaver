import { useId } from 'react';
import { InfoLabel, Switch, makeStyles, tokens } from '@fluentui/react-components';

// A Switch paired with a visible InfoLabel (i) info affordance, so the meaning of
// automation toggles (Autopilot / Auto-approve tools) is discoverable on the UI
// rather than hidden behind a hover-only tooltip.

const useStyles = makeStyles({
  root: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
});

export interface AutomationToggleProps {
  label: string;
  info: string;
  checked: boolean;
  disabled?: boolean;
  onChange: (checked: boolean) => void;
  // Where the label sits relative to the switch. Defaults to after.
  labelPosition?: 'before' | 'after';
}

export function AutomationToggle({
  label,
  info,
  checked,
  disabled,
  onChange,
  labelPosition = 'after',
}: AutomationToggleProps) {
  const styles = useStyles();
  const id = useId();

  const switchEl = (
    <Switch
      id={id}
      checked={checked}
      disabled={disabled}
      aria-label={label}
      onChange={(_, d) => onChange(d.checked)}
    />
  );
  const labelEl = (
    <InfoLabel htmlFor={id} info={info} infoButton={{ 'aria-label': `About ${label}` }}>
      {label}
    </InfoLabel>
  );

  return (
    <div className={styles.root}>
      {labelPosition === 'before' ? (
        <>
          {labelEl}
          {switchEl}
        </>
      ) : (
        <>
          {switchEl}
          {labelEl}
        </>
      )}
    </div>
  );
}
