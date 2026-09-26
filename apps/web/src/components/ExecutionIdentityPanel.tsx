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
import type { ExecutionIdentityProjection } from '../api/types';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  section: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  title: { fontWeight: tokens.fontWeightSemibold },
  metadata: {
    display: 'grid',
    gridTemplateColumns: 'max-content minmax(0, 1fr)',
    gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    margin: 0,
  },
  term: { color: tokens.colorNeutralForeground3 },
  value: { margin: 0, overflowWrap: 'anywhere', fontFamily: 'monospace' },
  decisions: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  decision: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    paddingBottom: tokens.spacingVerticalS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

export function ExecutionIdentityPanel({ runId }: { runId: string }) {
  const styles = useStyles();
  const [record, setRecord] = useState<ExecutionIdentityProjection | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (record !== null || error !== null) return;
    let cancelled = false;
    void apiClient.getRunExecutionIdentity(runId)
      .then((value) => { if (!cancelled) setRecord(value); })
      .catch((err) => {
        if (!cancelled) setError(formatApiError(err, 'Execution identity could not be loaded.').message);
      });
    return () => { cancelled = true; };
  }, [error, record, runId]);

  if (record === null && error === null) return <Spinner label="Loading execution identity" />;
  if (error && record === null) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>{error}</MessageBarBody>
        <Button appearance="transparent" size="small" onClick={() => setError(null)}>Retry</Button>
      </MessageBar>
    );
  }
  if (record === null) return null;
  if (record.descriptor === null) {
    return (
      <MessageBar intent="warning" data-testid="execution-identity-missing">
        <MessageBarBody>This legacy run has no immutable execution identity record.</MessageBarBody>
      </MessageBar>
    );
  }

  const descriptor = record.descriptor;
  return (
    <div className={styles.root} data-testid="execution-identity">
      <section className={styles.section} aria-label="Execution descriptor">
        <Text className={styles.title}>Immutable descriptor</Text>
        <dl className={styles.metadata}>
          <dt className={styles.term}>Identity</dt><dd className={styles.value}>{descriptor.descriptor_id}</dd>
          <dt className={styles.term}>Attempt</dt><dd className={styles.value}>{descriptor.attempt}</dd>
          <dt className={styles.term}>Principal</dt><dd className={styles.value}>{descriptor.principal_ref}</dd>
          <dt className={styles.term}>Service</dt><dd className={styles.value}>{descriptor.executing_service}</dd>
          <dt className={styles.term}>Assignment</dt><dd className={styles.value}>{descriptor.agent_assignment_id}</dd>
          <dt className={styles.term}>Role</dt><dd className={styles.value}>{descriptor.agent_role ?? 'unknown'}</dd>
          <dt className={styles.term}>Parent</dt><dd className={styles.value}>{descriptor.parent_descriptor_id ?? 'none'}</dd>
          <dt className={styles.term}>Retry of</dt><dd className={styles.value}>{descriptor.retry_of_descriptor_id ?? 'none'}</dd>
        </dl>
      </section>

      <section className={styles.section} aria-label="Execution authority">
        <Text className={styles.title}>Execution authority</Text>
        <Text>Backend: {record.backend?.kind ?? 'unknown'} ({record.backend?.evidence_state ?? 'missing'})</Text>
        <Text>
          Launch binding: {record.launch_permission_binding?.binding_id ?? 'missing'}
        </Text>
        <Text>
          Current binding: {record.permission_binding?.binding_id ?? 'missing'}
        </Text>
        <Text size={200}>
          Current revocation is evaluated by the Permissions view; this historical record cannot restore access.
        </Text>
      </section>

      <section className={styles.section} aria-label="Decision outcomes">
        <Text className={styles.title}>Tool and gate decisions</Text>
        {record.decisions.length === 0 ? (
          <Text>No consequential decision evidence was recorded.</Text>
        ) : (
          <div className={styles.decisions}>
            {record.decisions.map((decision) => (
              <div className={styles.decision} key={`${decision.sequence}-${decision.tool_call_id ?? 'missing'}`}>
                <span>
                  <Badge color={decision.outcome === 'approved' || decision.outcome === 'succeeded' ? 'success' : 'danger'}>
                    {decision.outcome}
                  </Badge>{' '}
                  <Text>{decision.tool_name ?? 'unknown tool'} · {decision.gate}</Text>
                </span>
                <Text size={200}>
                  Call {decision.tool_call_id ?? 'missing'} · {decision.correlation_state}
                  {decision.reason_code ? ` · ${decision.reason_code}` : ''}
                </Text>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
