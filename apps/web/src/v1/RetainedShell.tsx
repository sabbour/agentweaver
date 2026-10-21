import { Badge, Button, Divider, MessageBar, MessageBarBody, Popover, PopoverSurface, PopoverTrigger, Text, mergeClasses } from '@fluentui/react-components';
import { ChevronDownRegular, PersonRegular, SignOutRegular } from '@fluentui/react-icons';
import { useIdentityMenuStyles } from '../components/identityMenuStyles';
import { useEffect, useState, type ReactNode } from 'react';
import { useLocation, useMatch } from 'react-router-dom';
import { LeftNavView } from '../components/shell/LeftNav';
import { ProjectSwitcherView } from '../components/shell/ProjectSwitcher';
import { resolveActiveKey } from '../components/shell/navConfig';
import { PageContainer } from '../components/ui/PageContainer';
import { AttentionBell } from './AttentionList';
import { useAuth } from './AuthContext';
import { useRunAttention } from './runAttention';
import { gatewayClient } from './api';
import type { ProjectSummary } from './contracts';
import packageInfo from '../../package.json';

/**
 * Keeps the 0.x persona trigger and menu layout without requesting legacy identity APIs.
 * The Broker token carries no display name, so the label stays "Account". The 0.x status
 * dot polls a legacy health route, so the menu footer shows only the version badge.
 */
export function BrokerIdentity({ collapsed }: { collapsed: boolean }) {
  const { signOut } = useAuth();
  const styles = useIdentityMenuStyles();
  return (
    <Popover positioning="above-start">
      <PopoverTrigger disableButtonEnhancement>
        <Button
          appearance="transparent"
          type="button"
          aria-label="Account"
          className={collapsed ? mergeClasses(styles.trigger, styles.triggerCollapsed) : styles.trigger}
        >
          <span className={collapsed ? mergeClasses(styles.triggerInner, styles.triggerInnerCollapsed) : styles.triggerInner}>
            <span className={styles.fallbackAvatar} aria-hidden="true"><PersonRegular /></span>
            {!collapsed && (
              <>
                <Text className={styles.label}>Account</Text>
                <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
              </>
            )}
          </span>
        </Button>
      </PopoverTrigger>
      <PopoverSurface>
        <div className={styles.popover}>
          <div className={styles.section}>
            <Text weight="semibold">Signed in</Text>
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

/** Adapts Gateway data to the existing rail; no monolith API consumers are mounted. */
export function RetainedShell({ children }: { children: ReactNode }) {
  const location = useLocation();
  const { session, apiCall } = useAuth();
  const attention = useRunAttention();
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
        visibleGlobalKeys={['overview', 'projects']}
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
        notifications={<AttentionBell attention={attention} />}
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
