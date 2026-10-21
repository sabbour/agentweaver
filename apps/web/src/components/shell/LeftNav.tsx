import {
  Badge,
  Button,
  Divider,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  PanelLeftContract24Regular,
  PanelLeftExpand24Regular,
  Settings24Regular,
} from '@fluentui/react-icons';
import { GLOBAL_NAV_ITEMS, NAV_SECTIONS, NEW_SESSION_NAV_KEY, SETTINGS_NAV_KEY, navItemPath } from './navConfig';
import { useAppVersion } from '../../hooks/useAppVersion';
import { NotificationBell } from './NotificationBell';
import { ProjectSwitcher } from './ProjectSwitcher';
import { StatusDot } from './StatusDot';
import { GitHubIdentityBadge } from '../GitHubIdentityBadge';
import { createNewAssistantSessionState } from '../../routes/assistantNavigation';
import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import type { GlobalNavItemDef, NavItemDef, NavSectionDef } from './navConfig';
import type { ReactNode, RefObject } from 'react';
// Persistent left navigation. Native FluentUI rebuild — no copilot-fluent-system
// kit imports. Copilot-style single rail: chrome (brand + collapse), a header
// slot (project switcher), a scrollable nav area, and a bottom persona footer.
// No top bar. "Start task" floats top-right of the content panel (see AppShell).
// Visual styling via shell.css + agentweaverLightTheme.

const NAV_WIDTH = '260px';
const NAV_WIDTH_COLLAPSED = '64px';
const COLLAPSE_KEY = 'aw.nav.collapsed';

const useStyles = makeStyles({
  settingsMenuSurface: {
    minWidth: '220px',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  settingsMenuItem: {
    justifyContent: 'flex-start',
  },
});

export interface LeftNavProps {
  projectId: string | undefined;
  activeKey: string;
  pathname: string;
  isFallbackProject?: boolean;
  onFallbackProjectMissing?: () => void;
  isPlatformAdmin?: boolean;
  tourTargets?: {
    projects: RefObject<HTMLAnchorElement | null>;
    sessions: RefObject<HTMLAnchorElement | null>;
    settings: RefObject<HTMLButtonElement | null>;
  };
  onTakeProductTour?: () => void;
}

function sectionLabel(heading: string): string {
  return heading
    .toLowerCase()
    .replace(/\b\w/g, (match) => match.toUpperCase());
}

function formatVersionBadge(version: string): string {
  if (!version) return '';
  const [base, hash] = version.split('+', 2);
  if (!hash) return version;
  return `${base}+${hash.slice(0, 7)}`;
}

/**
 * Renders the primary navigation using the configured app routes and current project context.
 * This wrapper fills the rail's slots with the 0.x data sources: the app version, the
 * project switcher with its own project list, the notification bell, and the GitHub identity.
 */
export function LeftNav({
  ...props
}: LeftNavProps) {
  const version = useAppVersion();
  // All drawing happens in LeftNavView. This wrapper only supplies the parts
  // that call 0.x APIs, so the v1 shell can reuse the same rail with its own parts.
  return (
    <LeftNavView
      {...props}
      version={version}
      projectSwitcher={<ProjectSwitcher {...props} />}
      notifications={<NotificationBell />}
      identity={(collapsed, footerMeta) => (
        <GitHubIdentityBadge projectId={props.projectId} collapsed={collapsed} footerMeta={footerMeta} />
      )}
    />
  );
}

/**
 * Props for the rail view. The caller supplies every part that needs data
 * (version, project switcher, bell, identity), so the rail itself makes no API calls.
 */
export interface LeftNavViewProps extends LeftNavProps {
  version: string;
  projectSwitcher: ReactNode;
  notifications?: ReactNode;
  /** Renders the footer identity. Receives the collapsed state and the status and version badges for its menu. */
  identity: (collapsed: boolean, footerMeta: ReactNode) => ReactNode;
  /** Replaces the default "go to /assistant" behavior of the new chat button. */
  onNewSession?: () => void;
  /** Extra content placed under the Sessions entry. */
  sessionAction?: ReactNode;
  /** When set, only these global entries are shown. When omitted, all are shown. */
  visibleGlobalKeys?: readonly string[];
  /** When set, only these project entries are shown. When omitted, all are shown. */
  visibleProjectKeys?: readonly string[];
  showNewSession?: boolean;
}

/** Renders the 0.x rail unchanged; callers supply identity and project data. */
export function LeftNavView({
  projectId,
  activeKey,
  isPlatformAdmin = false,
  tourTargets,
  onTakeProductTour,
  version,
  projectSwitcher,
  notifications,
  identity,
  onNewSession,
  sessionAction,
  visibleGlobalKeys,
  visibleProjectKeys,
  showNewSession = true,
}: LeftNavViewProps) {
  const styles = useStyles();
  const versionLabel = formatVersionBadge(version);
  const footerVersionText = versionLabel ? `v${versionLabel}` : 'Alpha';
  const navigate = useNavigate();
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(COLLAPSE_KEY) === '1';
    } catch {
      return false;
    }
  });
  // Split the configured entries into the parts of the rail. When the caller
  // passes visible keys, drop every entry not in the list, then drop sections
  // left with no entries so no empty heading or divider is drawn.
  const { globalPrimaryItems, globalSessionsItem, primarySections, bottomSections } = useMemo(() => ({
    globalPrimaryItems: GLOBAL_NAV_ITEMS.filter((item) => item.key !== 'sessions' && (!visibleGlobalKeys || visibleGlobalKeys.includes(item.key))),
    globalSessionsItem: GLOBAL_NAV_ITEMS.find((item) => item.key === 'sessions' && (!visibleGlobalKeys || visibleGlobalKeys.includes(item.key))),
    primarySections: NAV_SECTIONS.filter((section) => !section.anchorBottom)
      .map((section) => ({ ...section, items: section.items.filter((item) => !visibleProjectKeys || visibleProjectKeys.includes(item.key)) }))
      .filter((section) => section.items.length > 0),
    bottomSections: NAV_SECTIONS.filter((section) => section.anchorBottom)
      .map((section) => ({ ...section, items: section.items.filter((item) => !visibleProjectKeys || visibleProjectKeys.includes(item.key)) }))
      .filter((section) => section.items.length > 0),
  }), [visibleGlobalKeys, visibleProjectKeys]);

  const toggleCollapsed = () => {
    setCollapsed((prev) => {
      const next = !prev;
      try {
        localStorage.setItem(COLLAPSE_KEY, next ? '1' : '0');
      } catch {
        /* localStorage unavailable — fall back to in-memory state only */
      }
      return next;
    });
  };

  const startNewSession = () => {
    // A caller-supplied handler wins. Otherwise open a new assistant chat,
    // scoped to the current project when there is one.
    if (onNewSession) {
      onNewSession();
      return;
    }
    const search = projectId ? `?project=${encodeURIComponent(projectId)}` : '';
    navigate(`/assistant${search}`, { state: createNewAssistantSessionState() });
  };

  const [settingsMenuOpen, setSettingsMenuOpen] = useState(false);
  // The New session and Settings buttons show as selected on their own pages,
  // like the nav links, so those pages do not fall back to Overview.
  const newSessionSelected = activeKey === NEW_SESSION_NAV_KEY;
  const settingsSelected = activeKey === SETTINGS_NAV_KEY;
  const newSessionClassName = `aw-nav-action-button${newSessionSelected ? ' aw-nav-action-button--selected' : ''}`;

  const goTo = (path: string) => {
    setSettingsMenuOpen(false);
    navigate(path);
  };

  // Keep --app-nav-width in sync so fixed-position panels can offset correctly.
  useEffect(() => {
    document.documentElement.style.setProperty(
      '--app-nav-width',
      collapsed ? NAV_WIDTH_COLLAPSED : NAV_WIDTH,
    );
  }, [collapsed]);

  function renderGlobalItem(item: GlobalNavItemDef) {
    const selected = activeKey === item.key;
    const tourRef = item.key === 'projects'
      ? tourTargets?.projects
      : item.key === 'sessions'
        ? tourTargets?.sessions
        : undefined;
    const linkEl = (
      <Link
        ref={tourRef}
        to={item.path}
        aria-label={item.label}
        aria-current={selected ? 'page' : undefined}
        className={`aw-nav-item${selected ? ' aw-nav-item--selected' : ''}`}
      >
        <span className="aw-nav-item__icon" aria-hidden="true">{item.icon}</span>
        {!collapsed && <span className="aw-nav-item__label">{item.label}</span>}
      </Link>
    );
    if (collapsed) {
      return (
        <Tooltip key={item.key} content={item.label} relationship="label" positioning="after">
          {linkEl}
        </Tooltip>
      );
    }
    return <Fragment key={item.key}>{linkEl}</Fragment>;
  }

  function renderProjectItem(item: NavItemDef, pId: string) {
    const selected = activeKey === item.key;
    const linkEl = (
      <Link
        to={navItemPath(pId, item)}
        aria-label={item.label}
        aria-current={selected ? 'page' : undefined}
        className={`aw-nav-item${selected ? ' aw-nav-item--selected' : ''}`}
      >
        <span className="aw-nav-item__icon" aria-hidden="true">{item.icon}</span>
        {!collapsed && <span className="aw-nav-item__label">{item.label}</span>}
      </Link>
    );
    if (collapsed) {
      return (
        <Tooltip key={item.key} content={item.label} relationship="label" positioning="after">
          {linkEl}
        </Tooltip>
      );
    }
    return <Fragment key={item.key}>{linkEl}</Fragment>;
  }

  function renderSection(section: NavSectionDef, pId: string) {
    const label = sectionLabel(section.heading);
    return (
      <div
        key={section.heading}
        role="group"
        aria-label={label}
        className={`aw-nav-section${section.anchorBottom ? ' aw-nav-section--bottom' : ''}`}
        style={{ gap: '2px' }}
      >
        {!collapsed && <div className="aw-nav-section__heading">{label}</div>}
        {section.items.map((item) => renderProjectItem(item, pId))}
      </div>
    );
  }

  return (
    <nav
      aria-label="Primary navigation"
      data-testid="app-navigation-menu"
      data-collapsed={collapsed ? 'true' : 'false'}
      tabIndex={collapsed ? 0 : undefined}
      className={`aw-left-nav${collapsed ? ' aw-left-nav--collapsed' : ''}`}
    >
      {/* Brand + collapse toggle chrome */}
      <div className="aw-rail-chrome">
        <Link to="/" aria-label="Agentweaver home" className="aw-rail-brand">
          <img src="/agentweaver.png" alt="" className="aw-rail-brand__icon" />
          {!collapsed && <span className="aw-rail-brand__label">Agentweaver</span>}
        </Link>
        <div className="aw-rail-chrome__actions">
          <Popover
            open={settingsMenuOpen}
            onOpenChange={(_, data) => setSettingsMenuOpen(data.open)}
            positioning="below-end"
          >
            <PopoverTrigger disableButtonEnhancement>
              <Tooltip content="Settings" relationship="label">
                <Button
                  ref={tourTargets?.settings}
                  appearance="subtle"
                  icon={<Settings24Regular />}
                  aria-label="Settings"
                  aria-current={settingsSelected ? 'page' : undefined}
                  className={settingsSelected ? 'aw-nav-action-button--selected' : undefined}
                  data-testid="settings-menu-button"
                />
              </Tooltip>
            </PopoverTrigger>
            <PopoverSurface className={styles.settingsMenuSurface} data-testid="settings-menu-popover">
              {isPlatformAdmin && (
                <Button
                  appearance="subtle"
                  className={styles.settingsMenuItem}
                  onClick={() => goTo('/platform-settings')}
                >
                  Platform settings
                </Button>
              )}
              <Button
                appearance="subtle"
                className={styles.settingsMenuItem}
                onClick={() => goTo('/settings')}
              >
                Account settings
              </Button>
              {projectId && (
                <>
                  <Divider />
                  <Button
                    appearance="subtle"
                    className={styles.settingsMenuItem}
                    onClick={() => goTo(`/projects/${projectId}/settings`)}
                  >
                    Project settings
                  </Button>
                </>
              )}
              {onTakeProductTour && (
                <>
                  <Divider />
                  <Button
                    appearance="subtle"
                    className={styles.settingsMenuItem}
                    onClick={() => {
                      setSettingsMenuOpen(false);
                      onTakeProductTour();
                    }}
                  >
                    Take product tour
                  </Button>
                </>
              )}
            </PopoverSurface>
          </Popover>
          {notifications}
          <Button
            appearance="subtle"
            icon={collapsed ? <PanelLeftExpand24Regular /> : <PanelLeftContract24Regular />}
            aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
            aria-expanded={!collapsed}
            onClick={toggleCollapsed}
            className="aw-rail-collapse-btn"
          />
        </div>
      </div>

      {/* Header slot: project switcher (hidden when collapsed). Assistant entry
          points now live in the global Sessions hub / /assistant route. */}
      {!collapsed && (
        <div className="aw-rail-header">
          {projectSwitcher}
        </div>
      )}

      {/* Scrollable nav area */}
      <div
        className="aw-rail-scroll"
        data-testid="app-navigation-scroll"
        data-scrollbar-mode={collapsed ? 'hidden' : 'hover'}
        tabIndex={collapsed ? -1 : 0}
      >
        {/* Global destinations (Overview, Projects) — no section heading */}
        <div role="group" aria-label="Global" className="aw-nav-section" style={{ gap: '2px' }}>
          {globalPrimaryItems.map(renderGlobalItem)}
        </div>

        {globalSessionsItem && (
          <>
            <hr aria-hidden="true" className="aw-nav-divider" />
            {collapsed ? (
              <div role="group" aria-label="Sessions" className="aw-nav-section" style={{ gap: '2px' }}>
                {renderGlobalItem(globalSessionsItem)}
                {showNewSession && <Tooltip content="New session" relationship="label" positioning="after">
                  <Button
                    appearance="subtle"
                    icon={<AddRegular />}
                    aria-label="New session"
                    aria-current={newSessionSelected ? 'page' : undefined}
                    onClick={startNewSession}
                    className={newSessionClassName}
                  />
                </Tooltip>}
              </div>
            ) : (
              <div role="group" aria-label="Sessions" className="aw-nav-section">
                <div className={`aw-nav-disclosure${activeKey === globalSessionsItem.key ? ' aw-nav-disclosure--selected' : ''}`}>
                  <Link
                    ref={tourTargets?.sessions}
                    to={globalSessionsItem.path}
                    aria-label={globalSessionsItem.label}
                    aria-current={activeKey === globalSessionsItem.key ? 'page' : undefined}
                    className="aw-nav-item aw-nav-disclosure__toggle"
                  >
                    <span className="aw-nav-item__icon" aria-hidden="true">{globalSessionsItem.icon}</span>
                    <span className="aw-nav-item__label">{globalSessionsItem.label}</span>
                  </Link>
                  {showNewSession && <Tooltip content="New session" relationship="label" positioning="after">
                    <Button
                      appearance="subtle"
                      icon={<AddRegular />}
                      aria-label="New session"
                      aria-current={newSessionSelected ? 'page' : undefined}
                      onClick={startNewSession}
                      className={newSessionClassName}
                    />
                  </Tooltip>}
                </div>
              </div>
            )}
          </>
        )}

        {sessionAction}

        {/* Project-scoped primary sections, each preceded by a thin divider */}
        {projectId && primarySections.map((section) => (
          <Fragment key={section.heading}>
            <hr aria-hidden="true" className="aw-nav-divider" />
            {renderSection(section, projectId)}
          </Fragment>
        ))}

        {/* Bottom-anchored sections — first divider carries margin-top:auto to anchor group */}
        {projectId && bottomSections.map((section, i) => (
          <Fragment key={section.heading}>
            <hr aria-hidden="true" className={`aw-nav-divider${i === 0 ? ' aw-nav-divider--bottom-anchor' : ''}`} />
            {renderSection(section, projectId)}
          </Fragment>
        ))}
      </div>

      {/* Footer: signed-in identity. Status and version appear in its menu footer. */}
      <div className="aw-rail-footer">
        {/* The caller decides which identity control to show. The rail passes
            the status dot and version badge so they keep the 0.x footer layout. */}
        {identity(collapsed, (
            <>
              <StatusDot />
              <Badge
                className="aw-identity-menu-footer__version"
                appearance="tint"
                color="warning"
                title={version ? `Agentweaver is alpha software under active development. Full version: v${version}` : 'Agentweaver is alpha software under active development.'}
              >
                <span className="aw-identity-menu-footer__version-text">{footerVersionText}</span>
              </Badge>
            </>
        ))}
      </div>
    </nav>
  );
}
