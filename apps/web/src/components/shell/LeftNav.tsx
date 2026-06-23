import { Fragment, useState } from 'react';
import type { ReactElement } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  NavDrawer,
  NavDrawerBody,
  NavItem,
  NavSectionHeader,
  Tooltip,
  Button,
  mergeClasses,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { OnNavItemSelectData } from '@fluentui/react-components';
import { PanelLeftContract24Regular, PanelLeftExpand24Regular } from '@fluentui/react-icons';
import { NAV_SECTIONS, NAV_ITEMS, GLOBAL_NAV_ITEMS, navItemPath } from './navConfig';

// Persistent left sidebar. Global destinations (Overview / Projects) sit at the
// top; project-scoped sections (WORK / SQUAD / OPERATIONS / SYSTEM) render only
// when a project is in scope, with SYSTEM anchored to the bottom. The rail is
// collapsible to an icon-only mini rail (state persisted in localStorage).

const NAV_WIDTH = '180px';
const NAV_WIDTH_COLLAPSED = '52px';
const COLLAPSE_KEY = 'aw.nav.collapsed';

const useStyles = makeStyles({
  navDrawer: {
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    height: '100%',
    overflowX: 'hidden',
    transitionProperty: 'width, min-width, max-width',
    transitionDuration: tokens.durationNormal,
    transitionTimingFunction: tokens.curveEasyEase,
  },
  navDrawerExpanded: {
    width: NAV_WIDTH,
    minWidth: NAV_WIDTH,
    maxWidth: NAV_WIDTH,
    '& nav': {
      display: 'flex',
      flexDirection: 'column',
      height: '100%',
      width: NAV_WIDTH,
      minWidth: NAV_WIDTH,
    },
  },
  navDrawerCollapsed: {
    width: NAV_WIDTH_COLLAPSED,
    minWidth: NAV_WIDTH_COLLAPSED,
    maxWidth: NAV_WIDTH_COLLAPSED,
    '& nav': {
      display: 'flex',
      flexDirection: 'column',
      height: '100%',
      width: NAV_WIDTH_COLLAPSED,
      minWidth: NAV_WIDTH_COLLAPSED,
    },
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflowX: 'hidden',
  },
  brandRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    // Align the rail header height with the top bar (spacingVerticalS padding + 32px mark).
    minHeight: '32px',
    padding: `${tokens.spacingVerticalS} 0`,
    marginBottom: tokens.spacingVerticalXS,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
  },
  brandRowCollapsed: {
    justifyContent: 'center',
  },
  brandLogo: {
    height: '28px',
    width: 'auto',
    display: 'block',
    flexShrink: 0,
  },
  brandName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    whiteSpace: 'nowrap',
  },
  toggleRow: {
    display: 'flex',
    paddingBottom: tokens.spacingVerticalXS,
  },
  toggleRowCollapsed: {
    justifyContent: 'center',
  },
  toggleRowExpanded: {
    justifyContent: 'flex-end',
  },
  spacer: {
    flex: 1,
    minHeight: tokens.spacingVerticalM,
  },
  miniDivider: {
    height: '1px',
    margin: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralStroke2,
  },
});

export interface LeftNavProps {
  projectId: string | undefined;
  activeKey: string;
}

export function LeftNav({ projectId, activeKey }: LeftNavProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(COLLAPSE_KEY) === '1';
    } catch {
      return false;
    }
  });

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

  function handleSelect(_: unknown, data: OnNavItemSelectData) {
    const value = data.value as string;
    const globalItem = GLOBAL_NAV_ITEMS.find((i) => i.key === value);
    if (globalItem) {
      navigate(globalItem.path);
      return;
    }
    if (!projectId) return;
    const item = NAV_ITEMS.find((i) => i.key === value);
    if (item) navigate(navItemPath(projectId, item));
  }

  // A NavItem rendered with an icon-only label + tooltip when the rail is collapsed.
  const renderNavItem = (key: string, label: string, icon: ReactElement) => {
    const item = (
      <NavItem icon={icon} value={key} aria-label={label}>
        {collapsed ? '' : label}
      </NavItem>
    );
    if (!collapsed) return <Fragment key={key}>{item}</Fragment>;
    return (
      <Tooltip content={label} relationship="label" positioning="after" key={key}>
        {item}
      </Tooltip>
    );
  };

  return (
    <NavDrawer
      open
      type="inline"
      size="small"
      selectedValue={activeKey}
      onNavItemSelect={handleSelect}
      className={mergeClasses(
        styles.navDrawer,
        collapsed ? styles.navDrawerCollapsed : styles.navDrawerExpanded,
      )}
      aria-label="Primary navigation"
    >
      <NavDrawerBody className={styles.body}>
        <Link
          to="/"
          aria-label="Agentweaver home"
          className={mergeClasses(styles.brandRow, collapsed && styles.brandRowCollapsed)}
        >
          <img src="/agentweaver.png" alt="Agentweaver" className={styles.brandLogo} />
          {!collapsed && <span className={styles.brandName}>Agentweaver</span>}
        </Link>
        <div
          className={mergeClasses(
            styles.toggleRow,
            collapsed ? styles.toggleRowCollapsed : styles.toggleRowExpanded,
          )}
        >
          <Button
            appearance="subtle"
            icon={collapsed ? <PanelLeftExpand24Regular /> : <PanelLeftContract24Regular />}
            aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
            aria-expanded={!collapsed}
            onClick={toggleCollapsed}
          />
        </div>

        {GLOBAL_NAV_ITEMS.map((item) => renderNavItem(item.key, item.label, item.icon))}

        {projectId &&
          NAV_SECTIONS.map((section) => (
            <Fragment key={section.heading}>
              {section.anchorBottom && <div className={styles.spacer} />}
              {collapsed ? (
                <div className={styles.miniDivider} aria-hidden="true" />
              ) : (
                <NavSectionHeader>{section.heading}</NavSectionHeader>
              )}
              {section.items.map((item) => renderNavItem(item.key, item.label, item.icon))}
            </Fragment>
          ))}
      </NavDrawerBody>
    </NavDrawer>
  );
}
