import { Link, Navigate } from 'react-router-dom';
import {
  Badge,
  BladeHeader,
  Button,
  EmptyState,
  MessageBar,
  MessageBarBody,
  StatusIconText,
  Text,
  makeStyles,
  tokens,
  Pulse24Regular,
} from '../../copilot-fluent-system';
import { getLastActiveProjectId } from '../../components/shell/projectContext';

const useStyles = makeStyles({
  root: {
    minHeight: 'min(560px, calc(100vh - 160px))',
    justifyContent: 'center',
  },
  blade: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '760px',
    marginInline: 'auto',
  },
  statusBand: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    alignItems: 'center',
  },
  copy: {
    color: tokens.colorNeutralForeground2,
  },
});

export function ObservabilityRedirectPage({ suffix = '' }: { suffix?: string }) {
  const styles = useStyles();
  const projectId = getLastActiveProjectId();
  if (projectId) {
    return <Navigate to={`/projects/${projectId}/observability${suffix}`} replace />;
  }

  return (
    <div className={['azf-stack azf-page azf-pattern-shell', styles.root].join(' ')}>
      <section className={['azf-surface azf-surface--panel azf-surface--padding-spacious', styles.blade].join(' ')} aria-label="Observability project selection required">
        <BladeHeader
          title="Observability"
          subtitle="Select a project before opening the Azure Monitor-style observability blade."
          resourceIcon={<Pulse24Regular />}
          menuLabel={<Badge appearance="outline">Project scope required</Badge>}
        />
        <div className={['azf-surface azf-surface--subtle azf-surface--padding-compact', styles.statusBand].join(' ')}>
          <StatusIconText status="warning">No active project context</StatusIconText>
          <Text className={styles.copy}>The portal route will redirect automatically after a project is active.</Text>
        </div>
        <MessageBar intent="warning">
          <MessageBarBody>Select a project to view observability data.</MessageBarBody>
        </MessageBar>
        <EmptyState
          title="Choose a project to continue"
          body="Observability uses project-scoped telemetry, filters, traces, and agent usage dimensions."
          action={(
            <Link to="/projects" style={{ textDecoration: 'none' }}>
              <Button appearance="primary">Browse projects</Button>
            </Link>
          )}
        />
      </section>
    </div>
  );
}
