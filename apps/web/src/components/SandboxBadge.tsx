import { memo } from 'react';
import { Badge, MessageBar, MessageBarBody, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { RunSandboxInfo } from '../api/types';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  label: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  warning: {
    marginTop: tokens.spacingVerticalXS,
  },
});

interface SandboxBadgeProps {
  sandbox: RunSandboxInfo;
  /** Optional warning message from a sandbox.warning event. */
  warningMessage?: string | null;
}

export const SandboxBadge = memo(function SandboxBadge({ sandbox, warningMessage }: SandboxBadgeProps) {
  const styles = useStyles();

  const badgeColor = sandbox.isRealIsolation ? 'informative' : 'warning';
  const badgeLabel = sandbox.isRealIsolation
    ? sandbox.backend
    : `${sandbox.backend} \u2014 no isolation`;

  return (
    <div>
      <div className={styles.row}>
        {/* SECURITY (Y-3): backend name rendered as text — no HTML */}
        <Text className={styles.label}>Sandbox</Text>
        <Badge color={badgeColor} shape="rounded" size="small">
          {badgeLabel}
        </Badge>
      </div>
      {warningMessage && (
        <MessageBar intent="warning" className={styles.warning}>
          <MessageBarBody>{warningMessage}</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
});
