import { InfoLabel, makeStyles, mergeClasses, Switch, tokens } from '@fluentui/react-components';
import { useId } from 'react';
// A Switch paired with a visible InfoLabel (i) info affordance, so the meaning of
// automation toggles (Autopilot / Auto-approve tools) is discoverable on the UI
// rather than hidden behind a hover-only tooltip.

const useStyles = makeStyles({
  row: {
    display: 'flex',
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
  const id = useId();
  const styles = useStyles();

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
    <div className={mergeClasses(styles.row)}>
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
