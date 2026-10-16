import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, WarningRegular } from '@fluentui/react-icons';
import { memo } from 'react';
import type { RunSandboxInfo } from '../api/types';

const useStyles = makeStyles({
  row: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
  },
  iconOk: {
    color: tokens.colorStatusSuccessForeground1,
    fontSize: '14px',
    flexShrink: 0,
  },
  iconWarn: {
    color: tokens.colorStatusWarningForeground1,
    fontSize: '14px',
    flexShrink: 0,
  },
});

interface SandboxBadgeProps {
  sandbox: RunSandboxInfo;
}

export const SandboxBadge = memo(function SandboxBadge({ sandbox }: SandboxBadgeProps) {
  const styles = useStyles();
  const badgeLabel = sandbox.isRealIsolation
    ? sandbox.backend
    : `${sandbox.backend} \u2014 no isolation`;

  return (
    // SECURITY (Y-3): backend name rendered as text — no HTML.
    <span className={styles.row}>
      {sandbox.isRealIsolation
        ? <CheckmarkCircleRegular className={styles.iconOk} aria-hidden="true" />
        : <WarningRegular className={styles.iconWarn} aria-hidden="true" />
      }
      <Text>Sandbox · {badgeLabel}</Text>
    </span>
  );
});
