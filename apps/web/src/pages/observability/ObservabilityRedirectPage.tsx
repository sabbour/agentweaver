import { Link, Navigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  tokens,
} from '@fluentui/react-components';
import { Pulse24Regular } from '@fluentui/react-icons';
import { getLastActiveProjectId } from '../../components/shell/projectContext';
import {
  EmptyState,
  PageContainer,
  PageHeader,
} from '../../components/ui';

const useStyles = makeStyles({
  root: {
    minHeight: 'min(560px, calc(100vh - 160px))',
    justifyContent: 'center',
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '760px',
    marginInline: 'auto',
  },
});

export function ObservabilityRedirectPage({ suffix = '' }: { suffix?: string }) {
  const styles = useStyles();
  const projectId = getLastActiveProjectId();
  if (projectId) {
    return <Navigate to={`/projects/${projectId}/observability${suffix}`} replace />;
  }

  return (
    <PageContainer className={styles.root}>
      <div className={styles.content}>
        <PageHeader
          title="Observability"
          description="Select a project to view observability."
        />
        <MessageBar intent="warning">
          <MessageBarBody>Select a project to view observability data.</MessageBarBody>
        </MessageBar>
        <EmptyState
          title="No project selected"
          description="Select a project to continue. You'll be redirected automatically once a project is active."
          icon={<Pulse24Regular />}
          action={(
            <Link to="/projects" style={{ textDecoration: 'none' }}>
              <Button appearance="primary">Browse projects</Button>
            </Link>
          )}
        />
      </div>
    </PageContainer>
  );
}
