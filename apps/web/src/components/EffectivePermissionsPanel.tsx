import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { apiClient } from '../api/apiClient';
import { formatApiError } from '../api/errors';
import type { EffectivePermissionInspection, EffectivePermissionPolicySummary } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  metadata: {
    display: 'grid',
    gridTemplateColumns: 'max-content minmax(0, 1fr)',
    gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    margin: 0,
  },
  term: {
    color: tokens.colorNeutralForeground3,
  },
  value: {
    margin: 0,
    overflowWrap: 'anywhere',
  },
  operationList: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },
  coverage: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    margin: 0,
    padding: 0,
    listStyleType: 'none',
  },
  coverageItem: {
    display: 'grid',
    gridTemplateColumns: 'max-content minmax(0, 1fr)',
    gap: tokens.spacingHorizontalS,
    paddingBottom: tokens.spacingVerticalS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  coverageDetail: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  code: {
    fontFamily: 'monospace',
    overflowWrap: 'anywhere',
  },
});

function PolicySummary({
  label,
  policy,
}: {
  label: string;
  policy: EffectivePermissionPolicySummary;
}) {
  const styles = useStyles();
  return (
    <section className={styles.section} aria-label={label}>
      <Text className={styles.sectionTitle}>{label}</Text>
      <Text size={200}>
        Shell {policy.shell_enabled ? 'enabled' : 'disabled'} · Network {policy.network_enabled ? 'enabled' : 'disabled'} · Direct execution {policy.direct_execution ? 'enabled' : 'disabled'}
      </Text>
      <Text size={200}>
        Repository roots {policy.allowed_repository_root_count} · Approval patterns {policy.destructive_command_pattern_count} · Output limit {policy.max_output_bytes} bytes
      </Text>
      <div className={styles.operationList}>
        {policy.allowed_operations.map((operation) => (
          <Badge key={operation} appearance="tint" color="informative">{operation}</Badge>
        ))}
      </div>
    </section>
  );
}

export function EffectivePermissionsPanel({
  runId,
}: {
  runId: string;
}) {
  const styles = useStyles();
  const [inspection, setInspection] = useState<EffectivePermissionInspection | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (inspection !== null || error !== null) return;
    let cancelled = false;
    void apiClient.getRunEffectivePermissions(runId)
      .then((value) => {
        if (!cancelled) setInspection(value);
      })
      .catch((err) => {
        if (!cancelled) {
          setError(formatApiError(err, 'Effective permissions could not be loaded.').message);
        }
      });
    return () => { cancelled = true; };
  }, [error, inspection, runId]);

  if (inspection === null && error === null) {
    return <Spinner label="Loading effective permissions" />;
  }

  if (error && inspection === null) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>{error}</MessageBarBody>
        <Button appearance="transparent" size="small" onClick={() => setError(null)}>Retry</Button>
      </MessageBar>
    );
  }

  if (inspection === null) return null;

  return (
    <div className={styles.root} data-testid="effective-permissions-inspection">
      <section className={styles.section} aria-label="Permission binding">
        <Text className={styles.sectionTitle}>Effective binding</Text>
        <dl className={styles.metadata}>
          <dt className={styles.term}>Identity</dt>
          <dd className={styles.value}><Text className={styles.code}>{inspection.binding.binding_id}</Text></dd>
          <dt className={styles.term}>Version</dt>
          <dd className={styles.value}><Text className={styles.code}>{inspection.binding.version}</Text></dd>
          <dt className={styles.term}>Source</dt>
          <dd className={styles.value}>{inspection.binding.source}</dd>
          <dt className={styles.term}>Attempt</dt>
          <dd className={styles.value}>{inspection.binding.attempt}</dd>
        </dl>
      </section>

      <PolicySummary label="Configured policy" policy={inspection.configured_policy} />
      <PolicySummary label="Effective narrowed policy" policy={inspection.effective_policy} />

      <section className={styles.section} aria-label="Policy overrides">
        <Text className={styles.sectionTitle}>Overrides and inheritance</Text>
        <Text>
          {inspection.overrides.is_narrowed
            ? `Narrowed: ${
              [...inspection.overrides.removed_operations, ...inspection.overrides.tightened_controls]
                .join(', ')
            }`
            : 'No effective narrowing from the configured policy.'}
        </Text>
        <Text size={200}>
          Launch ceiling {inspection.overrides.launch_ceiling_active ? 'active' : 'unchanged'} · Parent restriction {inspection.overrides.parent_restriction_active ? 'active' : 'not present'}
        </Text>
      </section>

      <section className={styles.section} aria-label="Current revocation">
        <Text className={styles.sectionTitle}>Current revocation</Text>
        <Text>
          {inspection.current_revocation.active
            ? `Active: ${
              [...inspection.current_revocation.removed_since_launch, ...inspection.current_revocation.tightened_controls]
                .join(', ')
            }`
            : 'No permissions have been revoked since launch.'}
        </Text>
      </section>

      {inspection.latest_denial && (
        <MessageBar intent="error" data-testid="effective-permission-latest-denial">
          <MessageBarBody>
            <strong>Latest denial:</strong> {inspection.latest_denial.reason}
            {inspection.latest_denial.tool_name ? ` Tool: ${inspection.latest_denial.tool_name}.` : ''}
          </MessageBarBody>
        </MessageBar>
      )}

      <section className={styles.section} aria-label="Enforcement coverage">
        <Text className={styles.sectionTitle}>Enforcement coverage</Text>
        <ul className={styles.coverage}>
          {inspection.coverage.map((item) => (
            <li key={item.operation} className={styles.coverageItem}>
              <Badge color={item.allowed ? 'success' : 'danger'}>
                {item.allowed ? 'Allowed' : 'Denied'}
              </Badge>
              <span className={styles.coverageDetail}>
                <Text className={styles.code}>{item.operation}</Text>
                <Text size={200}>{item.tool_family} · {item.enforcement_gate}</Text>
              </span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
