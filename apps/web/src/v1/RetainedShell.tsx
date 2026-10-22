import { Badge, Button, Divider, MessageBar, MessageBarBody, Popover, PopoverSurface, PopoverTrigger, Text, mergeClasses } from '@fluentui/react-components';
import { ChevronDownRegular, PersonRegular, SignOutRegular } from '@fluentui/react-icons';
import { useIdentityMenuStyles } from '../components/identityMenuStyles';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useLocation, useMatch } from 'react-router-dom';
import { LeftNavView } from '../components/shell/LeftNav';
import { ProjectSwitcherView } from '../components/shell/ProjectSwitcher';
import { NEW_SESSION_NAV_KEY, SETTINGS_NAV_KEY, resolveActiveKey } from '../components/shell/navConfig';
import { PageContainer } from '../components/ui/PageContainer';
import { AttentionBell } from './AttentionList';
import { useAuth } from './AuthContext';
import { RunAttentionProvider, useSharedRunAttention } from './RunAttentionContext';
import { StartTaskAction } from './StartTaskAction';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import packageInfo from '../../package.json';

/**
 * Keeps the 0.x persona trigger and menu layout without requesting legacy identity APIs.
 * The label is the display name from the Broker ID token (`name`, else `preferred_username`);
 * when the token carries neither, it stays "Account". The 0.x status dot polls a legacy
 * health route, so the menu footer shows only the version badge.
 */
export function BrokerIdentity({ collapsed }: { collapsed: boolean }) {
  const { session, signOut } = useAuth();
  const styles = useIdentityMenuStyles();
  const displayName = session?.displayName ?? null;
  const label = displayName ?? 'Account';
  return (
    <Popover positioning="above-start">
      <PopoverTrigger disableButtonEnhancement>
        <Button
          appearance="transparent"
          type="button"
          // Keep "Account" in the accessible name so the control is findable either way.
          aria-label={displayName ? `Account: ${displayName}` : 'Account'}
          className={collapsed ? mergeClasses(styles.trigger, styles.triggerCollapsed) : styles.trigger}
        >
          <span className={collapsed ? mergeClasses(styles.triggerInner, styles.triggerInnerCollapsed) : styles.triggerInner}>
            <span className={styles.fallbackAvatar} aria-hidden="true"><PersonRegular /></span>
            {!collapsed && (
              <>
                <Text className={styles.label}>{label}</Text>
                <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
              </>
            )}
          </span>
        </Button>
      </PopoverTrigger>
      <PopoverSurface>
        <div className={styles.popover}>
          <div className={styles.section}>
            <Text weight="semibold">{displayName ? `Signed in as ${displayName}` : 'Signed in'}</Text>
          </div>
          <Divider />
          <div className={styles.footer} data-testid="identity-menu-footer">
            <Button appearance="subtle" icon={<SignOutRegular />} onClick={signOut} className={styles.signOut}>Sign out</Button>
            <div className={styles.footerMeta}>
              <Badge
                className="aw-identity-menu-footer__version"
                appearance="tint"
                color="warning"
                title={`Agentweaver is alpha software under active development. Full version: v${packageInfo.version}`}
              >
                <span className="aw-identity-menu-footer__version-text">v{packageInfo.version}</span>
              </Badge>
            </div>
          </div>
        </div>
      </PopoverSurface>
    </Popover>
  );
}

/** The rail bell. It reads the shared notifications state from RunAttentionProvider. */
function SharedAttentionBell({ projectNames }: { projectNames: ReadonlyMap<string, string> }) {
  const attention = useSharedRunAttention();
  return <AttentionBell attention={attention} projectNames={projectNames} />;
}

/**
 * Picks the rail item to highlight. v1 routes that the shared nav config does
 * not know are mapped here, so they never fall back to Overview.
 */
function shellActiveKey(pathname: string, projectId: string | undefined, hash: string): string {
  // The rail shortcut points to a section of Overview rather than a new page.
  if (pathname === '/overview' && hash === '#chief-of-staff') return 'chief-of-staff';
  // The new session page highlights the New session button.
  if (pathname === '/assistant') return NEW_SESSION_NAV_KEY;
  // Settings pages highlight the Settings menu button.
  if (pathname === '/platform-settings' || pathname === '/settings') return SETTINGS_NAV_KEY;
  // Knowledge pages highlight Memories, and run pages highlight Orchestrations.
  if (pathname.includes('/knowledge')) return 'memories';
  if (pathname.includes('/runs/')) return 'orchestrations';
  return resolveActiveKey(pathname, projectId);
}

/** Adapts Gateway data to the existing rail; no monolith API consumers are mounted. */
export function RetainedShell({ children }: { children: ReactNode }) {
  const location = useLocation();
  const { session, apiCall } = useAuth();
  const match = useMatch('/projects/:projectId/*');
  const projectId = match?.params.projectId;
  // The new session page carries its project in `?project=`. The switcher
  // shows that project, so it matches the project the page starts in.
  const switcherProjectId = projectId
    ?? (location.pathname === '/assistant' ? new URLSearchParams(location.search).get('project') ?? undefined : undefined);
  // The loaded project list is tagged with the access token it was loaded
  // with. After sign-out or a token change, the old list is ignored.
  const [directory, setDirectory] = useState<{
    scope: string;
    projects: ProjectSummary[];
    error: string | null;
  } | null>(null);
  // The token identifies the signed-in user. When it changes (sign-in, sign-out,
  // or switching user), the old list must not be shown.
  const scope = session?.accessToken ?? '';

  // Reload the project list on sign-in changes and on project navigation, so a
  // project created or renamed elsewhere shows up in the switcher.
  useEffect(() => {
    // Ignore the response if the effect re-ran or the shell unmounted first.
    let active = true;
    void apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null)
      .then((projects) => {
        if (active) setDirectory({ scope, projects, error: null });
      })
      .catch((error: unknown) => {
        // Keep the rail usable with an empty list and show the error above the page.
        if (active) setDirectory({
          scope, projects: [],
          error: error instanceof Error ? error.message : 'Projects could not load. Try again from Projects.',
        });
      });
    return () => { active = false; };
  }, [apiCall, scope, projectId]);

  // Use the list only if it was loaded with the current token. Otherwise treat
  // it as still loading.
  const current = directory?.scope === scope ? directory : null;
  // Project names for the bell items. Unknown projects show their ID.
  const projectNames = useMemo(
    () => new Map((current?.projects ?? []).map((project) => [project.projectId, project.name])),
    [current],
  );
  return (
    // One provider for the bell and the page, so they share one notifications
    // state and one poll. A dismiss in either place shows in both.
    <RunAttentionProvider>
      <div className="aw-app-shell">
        {/* The rail shows the 0.x pages, plus the Sessions group with its New
            session button. Operations pages show what current reads support. */}
        <LeftNavView
          visibleGlobalKeys={['overview', 'chief-of-staff', 'projects', 'sessions']}
          visibleProjectKeys={['dashboard', 'board', 'flow', 'orchestrations', 'workspace', 'agents', 'skills', 'memories', 'workflows', 'observability', 'diagnostics', 'heartbeat', 'cluster']}
          showNewSession
          projectId={projectId}
          pathname={location.pathname}
          activeKey={shellActiveKey(location.pathname, projectId, location.hash)}
          version={packageInfo.version}
          projectSwitcher={(
            <ProjectSwitcherView
              projectId={switcherProjectId}
              pathname={location.pathname}
              // Map v1 project summaries to the switcher's 0.x shape.
              projects={(current?.projects ?? []).map((project) => ({ project_id: project.projectId, name: project.name }))}
              loading={!current}
              // Sign-in problems are handled by the auth layer, so the switcher
              // never shows its own sign-in error here.
              authError={false}
              loadError={Boolean(current?.error)}
            />
          )}
          identity={(collapsed) => <BrokerIdentity collapsed={collapsed} />}
          notifications={<SharedAttentionBell projectNames={projectNames} />}
        />
        <div className="aw-shell-canvas">
          <main className="aw-shell-content" aria-label="Main content">
            {/* 0.x placed Start task above the page. Inside a project it adds a task
                to that project's backlog; outside a project there is no target, so it is hidden. */}
            {projectId && <StartTaskAction projectId={projectId} projectName={projectNames.get(projectId)} />}
            <div className="aw-shell-scroll">
              <PageContainer>
                {current?.error && <MessageBar intent="error"><MessageBarBody>{current.error}</MessageBarBody></MessageBar>}
                {children}
              </PageContainer>
            </div>
          </main>
        </div>
      </div>
    </RunAttentionProvider>
  );
}
