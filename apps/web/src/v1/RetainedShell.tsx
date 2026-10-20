import { Avatar, Button, MessageBar, MessageBarBody, Popover, PopoverSurface, PopoverTrigger } from '@fluentui/react-components';
import { PersonRegular, SignOutRegular } from '@fluentui/react-icons';
import { useEffect, useState, type ReactNode } from 'react';
import { useLocation, useMatch, useNavigate } from 'react-router-dom';
import { LeftNavView } from '../components/shell/LeftNav';
import { ProjectSwitcherView } from '../components/shell/ProjectSwitcher';
import { resolveActiveKey } from '../components/shell/navConfig';
import { PageContainer } from '../components/ui/PageContainer';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import packageInfo from '../../package.json';

/** Keeps the incumbent persona menu without requesting legacy identity APIs. */
function BrokerIdentity({ collapsed }: { collapsed: boolean }) {
  const { signOut } = useAuth();
  return (
    <Popover positioning="above-start">
      <PopoverTrigger disableButtonEnhancement>
        <Button appearance="subtle" icon={<Avatar icon={<PersonRegular />} size={28} />} aria-label="Account">
          {!collapsed && 'Account'}
        </Button>
      </PopoverTrigger>
      <PopoverSurface className="v1-stack">
        <strong>Signed in</strong>
        <p className="v1-muted">Your sign-in stays in this tab. Account connections are in Settings.</p>
        <Button appearance="subtle" icon={<SignOutRegular />} onClick={signOut}>Clear session</Button>
      </PopoverSurface>
    </Popover>
  );
}

/** Adapts Gateway data to the existing rail; no monolith API consumers are mounted. */
export function RetainedShell({ children }: { children: ReactNode }) {
  const location = useLocation();
  const navigate = useNavigate();
  const { session, apiCall } = useAuth();
  const match = useMatch('/projects/:projectId/*');
  const projectId = match?.params.projectId;
  const [directory, setDirectory] = useState<{
    scope: string;
    projects: ProjectSummary[];
    error: string | null;
  } | null>(null);
  const scope = session?.accessToken ?? '';

  useEffect(() => {
    let active = true;
    void apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null)
      .then((projects) => {
        if (active) setDirectory({ scope, projects, error: null });
      })
      .catch((error: unknown) => {
        if (active) setDirectory({
          scope, projects: [],
          error: error instanceof Error ? error.message : 'Projects could not load. Try again from Projects.',
        });
      });
    return () => { active = false; };
  }, [apiCall, scope, projectId]);

  const current = directory?.scope === scope ? directory : null;
  return (
    <div className="aw-app-shell">
      <LeftNavView
        visibleGlobalKeys={['projects']}
        visibleProjectKeys={['dashboard', 'orchestrations', 'agents', 'skills', 'memories']}
        showNewSession={false}
        projectId={projectId}
        pathname={location.pathname}
        activeKey={location.pathname.includes('/knowledge') ? 'memories'
          : location.pathname.includes('/runs/') ? 'orchestrations'
          : resolveActiveKey(location.pathname, projectId)}
        version={packageInfo.version}
        projectSwitcher={(
          <ProjectSwitcherView
            projectId={projectId}
            pathname={location.pathname}
            projects={(current?.projects ?? []).map((project) => ({ project_id: project.projectId, name: project.name }))}
            loading={!current}
            authError={false}
            loadError={Boolean(current?.error)}
          />
        )}
        identity={(collapsed) => <BrokerIdentity collapsed={collapsed} />}
        sessionAction={session?.binding ? (
          <Button appearance="subtle"
            onClick={() => {
              if (session.binding) navigate(`/projects/${encodeURIComponent(session.binding.projectId)}/runs/${encodeURIComponent(session.binding.runId)}?view=chat`);
            }}>
            Run chat
          </Button>
        ) : undefined}
        onNewSession={() => navigate(projectId ? `/projects/${encodeURIComponent(projectId)}/orchestrations` : '/assistant')}
      />
      <div className="aw-shell-canvas">
        <main className="aw-shell-content" aria-label="Main content">
          <div className="aw-shell-scroll">
            <PageContainer>
              {current?.error && <MessageBar intent="error"><MessageBarBody>{current.error}</MessageBarBody></MessageBar>}
              {children}
            </PageContainer>
          </div>
        </main>
      </div>
    </div>
  );
}
