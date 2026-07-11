import { StatusIconText } from '../copilot-fluent-system';
import { memo } from 'react';
import type { RunSandboxInfo } from '../api/types';
interface SandboxBadgeProps {
  sandbox: RunSandboxInfo;
}

export const SandboxBadge = memo(function SandboxBadge({ sandbox }: SandboxBadgeProps) {
  const badgeLabel = sandbox.isRealIsolation
    ? sandbox.backend
    : `${sandbox.backend} \u2014 no isolation`;

  return (
    // SECURITY (Y-3): backend name rendered as text — no HTML.
    <StatusIconText status={sandbox.isRealIsolation ? 'info' : 'warning'}>
      Sandbox · {badgeLabel}
    </StatusIconText>
  );
});
