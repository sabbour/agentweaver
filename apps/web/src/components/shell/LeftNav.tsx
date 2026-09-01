import { Badge, Button, Tooltip } from '@fluentui/react-components';
import {
  AddRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  PanelLeftContract24Regular,
  PanelLeftExpand24Regular,
} from '@fluentui/react-icons';
import { GLOBAL_NAV_ITEMS, NAV_SECTIONS, navItemPath } from './navConfig';
import { useAppVersion } from '../../hooks/useAppVersion';
import { NotificationBell } from './NotificationBell';
import { ProjectSwitcher } from './ProjectSwitcher';
import { StatusDot } from './StatusDot';
import { GitHubIdentityBadge } from '../GitHubIdentityBadge';
import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import type { GlobalNavItemDef, NavItemDef, NavSectionDef } from './navConfig';
// Persistent left navigation. Native FluentUI rebuild — no copilot-fluent-system
// kit imports. Copilot-style single rail: chrome (brand + collapse), a header
// slot (project switcher), a scrollable nav area, and a bottom persona footer.
// No top bar. "Start task" floats top-right of the content panel (see AppShell).
// Visual styling via shell.css + agentweaverLightTheme.

const NAV_WIDTH = '260px';
const NAV_WIDTH_COLLAPSED = '64px';
const COLLAPSE_KEY = 'aw.nav.collapsed';
const SESSIONS_EXPANDED_KEY = 'aw.nav.sessions.expanded';

export interface LeftNavProps {
  projectId: string | undefined;
  activeKey: string;
  pathname: string;
  isFallbackProject?: boolean;
  onFallbackProjectMissing?: () => void;
  isPlatformAdmin?: boolean;
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

export function LeftNav({
  projectId,
  activeKey,
  pathname,
  isFallbackProject,
  onFallbackProjectMissing,
  isPlatformAdmin = false,
}: LeftNavProps) {
  const version = useAppVersion();
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
  const [sessionsExpanded, setSessionsExpanded] = useState<boolean>(() => {
    try {
      return localStorage.getItem(SESSIONS_EXPANDED_KEY) !== '0';
    } catch {
      return true;
    }
  });

  const { globalPrimaryItems, globalSessionsItem, primarySections, bottomSections } = useMemo(() => ({
    globalPrimaryItems: GLOBAL_NAV_ITEMS.filter(
      (item) => item.key !== 'sessions' && (!item.requiresPlatformAdmin || isPlatformAdmin),
    ),
    globalSessionsItem: GLOBAL_NAV_ITEMS.find((item) => item.key === 'sessions'),
    primarySections: NAV_SECTIONS.filter((section) => !section.anchorBottom),
    bottomSections: NAV_SECTIONS.filter((section) => section.anchorBottom),
  }), [isPlatformAdmin]);

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

  const toggleSessionsExpanded = () => {
    setSessionsExpanded((prev) => {
      const next = !prev;
      try {
        localStorage.setItem(SESSIONS_EXPANDED_KEY, next ? '1' : '0');
      } catch {
        /* localStorage unavailable — fall back to in-memory state only */
      }
      return next;
    });
  };

  const startNewSession = () => {
    const search = projectId ? `?project=${encodeURIComponent(projectId)}` : '';
    navigate(`/assistant${search}`);
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
    const linkEl = (
      <Link
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

  function renderGlobalChildItem(item: GlobalNavItemDef, label: string) {
    const selected = activeKey === item.key;
    return (
      <Link
        to={item.path}
        aria-label={label}
        aria-current={selected ? 'page' : undefined}
        className={`aw-nav-item aw-nav-item--child${selected ? ' aw-nav-item--selected' : ''}`}
      >
        <span className="aw-nav-item__label">{label}</span>
      </Link>
    );
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
      className={`aw-left-nav${collapsed ? ' aw-left-nav--collapsed' : ''}`}
    >
      {/* Brand + collapse toggle chrome */}
      <div className="aw-rail-chrome">
        <Link to="/" aria-label="Agentweaver home" className="aw-rail-brand">
          <img src="/agentweaver.png" alt="" className="aw-rail-brand__icon" />
          {!collapsed && <span className="aw-rail-brand__label">Agentweaver</span>}
        </Link>
        <div className="aw-rail-chrome__actions">
          <NotificationBell />
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
          <ProjectSwitcher
            projectId={projectId}
            pathname={pathname}
            isFallbackProject={isFallbackProject}
            onFallbackProjectMissing={onFallbackProjectMissing}
          />
        </div>
      )}

      {/* Scrollable nav area */}
      <div
        className="aw-rail-scroll"
        data-testid="app-navigation-scroll"
        data-scrollbar-mode={collapsed ? 'hidden' : 'hover'}
        tabIndex={0}
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
                <Tooltip content="New session" relationship="label" positioning="after">
                  <Button
                    appearance="subtle"
                    icon={<AddRegular />}
                    aria-label="New session"
                    onClick={startNewSession}
                    className="aw-nav-action-button"
                  />
                </Tooltip>
              </div>
            ) : (
              <div role="group" aria-label="Sessions" className="aw-nav-section aw-nav-section--disclosure">
                <div className={`aw-nav-disclosure${activeKey === globalSessionsItem.key ? ' aw-nav-disclosure--selected' : ''}`}>
                  <button
                    type="button"
                    className="aw-nav-disclosure__toggle"
                    aria-expanded={sessionsExpanded}
                    aria-controls="aw-nav-sessions-panel"
                    onClick={toggleSessionsExpanded}
                  >
                    <span className="aw-nav-disclosure__chevron" aria-hidden="true">
                      {sessionsExpanded ? <ChevronDownRegular /> : <ChevronRightRegular />}
                    </span>
                    <span className="aw-nav-item__icon" aria-hidden="true">{globalSessionsItem.icon}</span>
                    <span className="aw-nav-item__label">{globalSessionsItem.label}</span>
                  </button>
                  <Tooltip content="New session" relationship="label" positioning="after">
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<AddRegular />}
                      aria-label="New session"
                      onClick={startNewSession}
                      className="aw-nav-action-button"
                    />
                  </Tooltip>
                </div>
                {sessionsExpanded && (
                  <div id="aw-nav-sessions-panel" className="aw-nav-subitems">
                    {renderGlobalChildItem(globalSessionsItem, 'All sessions')}
                  </div>
                )}
              </div>
            )}
          </>
        )}

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

      {/* Footer: signed-in identity, status dot + version badge */}
      <div className="aw-rail-footer">
        <GitHubIdentityBadge projectId={projectId} collapsed={collapsed} />
        <div className="aw-rail-footer__meta">
          <StatusDot />
          <Badge
            className="aw-rail-footer__version"
            appearance="tint"
            color="warning"
            title={version ? `Agentweaver is alpha software under active development. Full version: v${version}` : 'Agentweaver is alpha software under active development.'}
          >
            <span className="aw-rail-footer__version-text">{footerVersionText}</span>
          </Badge>
        </div>
      </div>
    </nav>
  );
}
