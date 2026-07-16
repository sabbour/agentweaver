import { Badge, Button, Tooltip } from '@fluentui/react-components';
import {
  PanelLeftContract24Regular,
  PanelLeftExpand24Regular,
} from '@fluentui/react-icons';
import { GLOBAL_NAV_ITEMS, NAV_SECTIONS, navItemPath } from './navConfig';
import { useAppVersion } from '../../hooks/useAppVersion';
import { GitHubSignIn } from '../GitHubSignIn';
import { NotificationBell } from './NotificationBell';
import { ProjectSwitcher } from './ProjectSwitcher';
import { StatusDot } from './StatusDot';
import { isAssistantFlagEnabled } from '../../utils/assistantFlag';
import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import type { GlobalNavItemDef, NavItemDef, NavSectionDef } from './navConfig';
// Persistent left navigation. Native FluentUI rebuild — no copilot-fluent-system
// kit imports. Copilot-style single rail: chrome (brand + collapse), a header
// slot (project switcher), a scrollable nav area, and a bottom persona footer.
// No top bar. "Start task" floats top-right of the content panel (see AppShell).
// Visual styling via shell.css + agentweaverLightTheme.

const NAV_WIDTH = '260px';
const NAV_WIDTH_COLLAPSED = '64px';
const COLLAPSE_KEY = 'aw.nav.collapsed';

export interface LeftNavProps {
  projectId: string | undefined;
  activeKey: string;
  pathname: string;
  isFallbackProject?: boolean;
  onFallbackProjectMissing?: () => void;
}

function sectionLabel(heading: string): string {
  return heading
    .toLowerCase()
    .replace(/\b\w/g, (match) => match.toUpperCase());
}

export function LeftNav({ projectId, activeKey, pathname, isFallbackProject, onFallbackProjectMissing }: LeftNavProps) {
  const version = useAppVersion();
  // Recomputed on every render (not cached in state) so a flag flip elsewhere (e.g.
  // visiting /assistant?assistant=1) is picked up the next time this persistent rail
  // re-renders, without needing a dedicated storage-event listener.
  const assistantEnabled = isAssistantFlagEnabled();
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(COLLAPSE_KEY) === '1';
    } catch {
      return false;
    }
  });

  const { primarySections, bottomSections } = useMemo(() => ({
    primarySections: NAV_SECTIONS.filter((section) => !section.anchorBottom),
    bottomSections: NAV_SECTIONS.filter((section) => section.anchorBottom),
  }), []);

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

  function renderSection(section: NavSectionDef, pId: string) {
    const label = sectionLabel(section.heading);
    const visibleItems = section.items.filter((item) => !item.assistantFlagged || assistantEnabled);
    if (visibleItems.length === 0) return null;
    return (
      <div
        key={section.heading}
        role="group"
        aria-label={label}
        className={`aw-nav-section${section.anchorBottom ? ' aw-nav-section--bottom' : ''}`}
        style={{ gap: '2px' }}
      >
        {visibleItems.map((item) => renderProjectItem(item, pId))}
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

      {/* Header slot: project switcher (hidden when collapsed). The old "Operator
          dock" trigger that lived here was removed in favor of the Sessions page
          under Projects (#4/#5) — see navConfig.tsx's `sessions` item + SessionsPage. */}
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
        <div
          role="group"
          aria-label="Global"
          className="aw-nav-section"
          style={{ gap: '2px' }}
        >
          {GLOBAL_NAV_ITEMS.map(renderGlobalItem)}
        </div>

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

      {/* Footer: signed-in persona + status dot + version badge */}
      <div className="aw-rail-footer">
        <GitHubSignIn />
        <div className="aw-rail-footer__meta">
          <StatusDot />
          <Badge
            appearance="tint"
            color="warning"
            title="Agentweaver is alpha software under active development."
          >
            Alpha{version ? ` v${version}` : ''}
          </Badge>
        </div>
      </div>
    </nav>
  );
}
