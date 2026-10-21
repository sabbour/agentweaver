/**
 * Top-level Sessions page in the 0.x layout: a list of runs across every
 * project the user can see, newest first. The service lists runs per project,
 * so the page reads the project list and then each project's newest runs.
 */
import { Button, MessageBar, MessageBarBody, makeStyles, tokens } from '@fluentui/react-components';
import { AddRegular, ArrowSyncRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { EmptyState, ErrorState } from '../components/ui/States';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import { errorMessage } from './errors';
import { Loading, PageHeading } from './pageParts';
import { RunList } from './RunList';
import { runListErrorMessage, useCrossProjectRunList } from './runListData';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXL },
});

/** Runs read per project for each page of the merged list. */
const SESSIONS_PAGE_SIZE = 10;

/** Renders the signed-in user's runs across projects. */
export function SessionsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { apiCall } = useAuth();
  const [projects, setProjects] = useState<ProjectSummary[] | null>(null);
  const [projectsLoading, setProjectsLoading] = useState(true);
  const [projectsError, setProjectsError] = useState<unknown>(null);
  const runs = useCrossProjectRunList(projects, SESSIONS_PAGE_SIZE);

  // Loads the project list with the unbound token. A new list also makes the
  // run list read again, so this is the whole refresh. On failure the last
  // good list stays on screen.
  const loadProjects = useCallback(async () => {
    setProjectsLoading(true);
    try {
      setProjects(await apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null));
      setProjectsError(null);
    } catch (reason) {
      setProjectsError(reason);
    } finally {
      setProjectsLoading(false);
    }
  }, [apiCall]);

  // Start the first load in a zero-delay timer so state updates stay out of
  // the effect body. The cleanup cancels the timer on unmount.
  useEffect(() => {
    const timer = window.setTimeout(() => { void loadProjects(); }, 0);
    return () => window.clearTimeout(timer);
  }, [loadProjects]);

  const loading = projectsLoading || runs.loading;
  const hasRows = runs.items.length > 0;

  return (
    <div className={styles.root}>
      <PageHeading
        title="Sessions"
        description="Runs across your projects, newest first. Open one to continue."
        actions={<>
          <Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={loading} onClick={() => { void loadProjects(); }}>Refresh</Button>
          {/* 0.x primary action: start a new chat on the New session page. */}
          <Button appearance="primary" icon={<AddRegular />} onClick={() => navigate('/assistant')}>New session</Button>
        </>}
      />
      {projectsError != null && (
        <ErrorState title="Couldn't load sessions" message={errorMessage(projectsError)} onRetry={() => { void loadProjects(); }} />
      )}
      {runs.error != null && (
        <ErrorState title="Couldn't load sessions" message={runListErrorMessage(runs.error)} onRetry={() => { void loadProjects(); }} />
      )}
      {/* Some projects can fail while others load, for example without read access. */}
      {runs.unreadableProjects.length > 0 && runs.error == null && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Runs from {runs.unreadableProjects.length === 1 ? 'one project' : `${runs.unreadableProjects.length} projects`} could not be read: {runs.unreadableProjects.join(', ')}.
          </MessageBarBody>
        </MessageBar>
      )}
      {/* Show the loader only when no rows are on screen. A refresh keeps the old rows. */}
      {loading && !hasRows && <Loading label="Loading sessions" />}
      {!loading && projectsError == null && runs.error == null && !hasRows && (
        <EmptyState
          title="No sessions yet"
          description={projects?.length === 0 ? 'You have no projects yet. Create one in Projects.' : 'Runs started in your projects appear here.'}
        />
      )}
      {hasRows && (
        <RunList
          title="All sessions"
          data={runs}
          emptyText="No sessions yet."
          projectNames={runs.projectNames}
        />
      )}
    </div>
  );
}
