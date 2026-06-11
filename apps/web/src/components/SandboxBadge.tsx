import { memo } from 'react';
import { Badge, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { RunSandboxInfo } from '../api/types';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  label: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

interface SandboxBadgeProps {
  sandbox: RunSandboxInfo;
}

export const SandboxBadge = memo(function SandboxBadge({ sandbox }: SandboxBadgeProps) {
  const styles = useStyles();

  const badgeColor = sandbox.isRealIsolation ? 'informative' : 'warning';
  const badgeLabel = sandbox.isRealIsolation
    ? sandbox.backend
    : `${sandbox.backend} \u2014 no isolation`;

  return (
    <div className={styles.row}>
      {/* SECURITY (Y-3): backend name rendered as text — no HTML */}
      <Text className={styles.label}>Sandbox</Text>
      <Badge color={badgeColor} shape="rounded" size="small">
        {badgeLabel}
      </Badge>
    </div>
  );
});
