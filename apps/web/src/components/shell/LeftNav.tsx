import { Fragment, useEffect, useMemo, useState } from 'react';
import type { ReactElement } from 'react';
import { Link } from 'react-router-dom';
import {
  Button,
  Tooltip,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { PanelLeftContract24Regular, PanelLeftExpand24Regular } from '@fluentui/react-icons';
import { NAV_SECTIONS, GLOBAL_NAV_ITEMS, navItemPath } from './navConfig';

// Persistent left navigation. The rail has fixed chrome (brand + collapse) and
// a separate scrollable item area so collapsed mode never shows a browser
// scrollbar jammed against the icons.

const NAV_WIDTH = '216px';
const NAV_WIDTH_COLLAPSED = '64px';
const COLLAPSE_KEY = 'aw.nav.collapsed';

const useStyles = makeStyles({
  root: {
    width: NAV_WIDTH,
    minWidth: NAV_WIDTH,
    maxWidth: NAV_WIDTH,
    height: '100%',
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
    boxShadow: tokens.shadow2,
    transitionProperty: 'width, min-width, max-width',
    transitionDuration: tokens.durationNormal,
    transitionTimingFunction: tokens.curveEasyEase,
    overflow: 'hidden',
    '@media (prefers-reduced-motion: reduce)': {
      transitionDuration: '0ms',
    },
  },
  rootCollapsed: {
    width: NAV_WIDTH_COLLAPSED,
    minWidth: NAV_WIDTH_COLLAPSED,
    maxWidth: NAV_WIDTH_COLLAPSED,
  },
  chrome: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    minHeight: '52px',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  chromeCollapsed: {
    flexDirection: 'column',
    justifyContent: 'flex-start',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS} ${tokens.spacingVerticalXS}`,
  },
  brandRow: {
    minWidth: 0,
    minHeight: '40px',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    borderRadius: tokens.borderRadiusMedium,
    padding: `0 ${tokens.spacingHorizontalXS}`,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      textDecorationLine: 'none',
    },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
      outlineOffset: '2px',
    },
  },
  brandRowCollapsed: {
    width: '40px',
    justifyContent: 'center',
    padding: 0,
  },
  brandLogo: {
    height: '28px',
    width: '28px',
    display: 'block',
    objectFit: 'contain',
    flexShrink: 0,
  },
  brandName: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '-0.01em',
  },
  collapseButton: {
    flexShrink: 0,
  },
  navScroll: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    overflowX: 'hidden',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS} ${tokens.spacingVerticalM}`,
    scrollbarWidth: 'thin',
    scrollbarColor: 'transparent transparent',
    maskImage: 'linear-gradient(to bottom, transparent 0, black 14px, black calc(100% - 14px), transparent 100%)',
    ':hover': {
      scrollbarColor: `${tokens.colorNeutralStroke1} transparent`,
    },
    ':focus-within': {
      scrollbarColor: `${tokens.colorNeutralStroke1} transparent`,
    },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
      outlineOffset: '-2px',
    },
    '&::-webkit-scrollbar': {
      width: '8px',
    },
    '&::-webkit-scrollbar-track': {
      backgroundColor: 'transparent',
    },
    '&::-webkit-scrollbar-thumb': {
      backgroundColor: 'transparent',
      borderRadius: tokens.borderRadiusCircular,
      border: '2px solid transparent',
      backgroundClip: 'content-box',
    },
    '&:hover::-webkit-scrollbar-thumb': {
      backgroundColor: tokens.colorNeutralStroke1,
    },
    '&:focus-within::-webkit-scrollbar-thumb': {
      backgroundColor: tokens.colorNeutralStroke1,
    },
  },
  navScrollCollapsed: {
    alignItems: 'center',
    gap: tokens.spacingVerticalXXS,
    padding: `${tokens.spacingVerticalS} 0 ${tokens.spacingVerticalM}`,
    scrollbarWidth: 'none',
    msOverflowStyle: 'none',
    '&::-webkit-scrollbar': {
      display: 'none',
      width: 0,
      height: 0,
    },
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '0px',
  },
  sectionExpanded: {
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalXXS}`,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  sectionBottom: {
    marginTop: 'auto',
    paddingTop: tokens.spacingVerticalM,
  },
  sectionHeading: {
    padding: `0 ${tokens.spacingHorizontalS} ${tokens.spacingVerticalXXS}`,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase200,
  },
  collapsedDivider: {
    width: '28px',
    height: '1px',
    margin: `${tokens.spacingVerticalXXS} 0`,
    backgroundColor: tokens.colorNeutralStroke2,
  },
  navLink: {
    minHeight: '36px',
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    borderRadius: tokens.borderRadiusMedium,
    padding: `0 ${tokens.spacingHorizontalS}`,
    color: tokens.colorNeutralForeground2,
    textDecorationLine: 'none',
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightRegular,
    lineHeight: tokens.lineHeightBase300,
    transitionProperty: 'background-color, color',
    transitionDuration: tokens.durationFast,
    transitionTimingFunction: tokens.curveEasyEase,
    position: 'relative',
    '@media (prefers-reduced-motion: reduce)': {
      transitionDuration: '0ms',
    },
    ':hover': {
      color: tokens.colorNeutralForeground1,
      backgroundColor: tokens.colorNeutralBackground1Hover,
      textDecorationLine: 'none',
    },
    ':active': {
      backgroundColor: tokens.colorNeutralBackground1Pressed,
    },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
      outlineOffset: '2px',
    },
    '@media (pointer: coarse)': {
      minHeight: '44px',
    },
  },
  navLinkCollapsed: {
    width: '40px',
    height: '40px',
    minHeight: '40px',
    justifyContent: 'center',
    padding: 0,
    '@media (pointer: coarse)': {
      width: '44px',
      height: '44px',
      minHeight: '44px',
    },
  },
  navLinkActive: {
    color: tokens.colorBrandForeground1,
    backgroundColor: tokens.colorBrandBackground2,
    fontWeight: tokens.fontWeightSemibold,
    boxShadow: `inset 3px 0 0 ${tokens.colorBrandStroke1}`,
    ':hover': {
      color: tokens.colorBrandForeground1,
      backgroundColor: tokens.colorBrandBackground2Hover,
    },
  },
  iconSlot: {
    width: '22px',
    height: '22px',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },
  label: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
});

export interface LeftNavProps {
  projectId: string | undefined;
  activeKey: string;
}

function sectionLabel(heading: string): string {
  return heading
    .toLowerCase()
    .replace(/\b\w/g, (match) => match.toUpperCase());
}

export function LeftNav({ projectId, activeKey }: LeftNavProps) {
  const styles = useStyles();
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

  const renderNavLink = (
    key: string,
    label: string,
    icon: ReactElement,
    to: string,
  ) => {
    const active = activeKey === key;
    const link = (
      <Link
        key={key}
        to={to}
        aria-label={label}
        aria-current={active ? 'page' : undefined}
        className={mergeClasses(
          styles.navLink,
          collapsed && styles.navLinkCollapsed,
          active && styles.navLinkActive,
        )}
      >
        <span className={styles.iconSlot} aria-hidden="true">{icon}</span>
        {!collapsed && <span className={styles.label}>{label}</span>}
      </Link>
    );

    if (!collapsed) return link;
    return (
      <Tooltip content={label} relationship="label" positioning="after" key={key}>
        {link}
      </Tooltip>
    );
  };

  const renderSection = (section: (typeof NAV_SECTIONS)[number], bottom = false) => (
    <div
      key={section.heading}
      role="group"
      className={mergeClasses(
        styles.section,
        !collapsed && styles.sectionExpanded,
        bottom && styles.sectionBottom,
      )}
      aria-label={sectionLabel(section.heading)}
    >
      {collapsed ? (
        <div className={styles.collapsedDivider} aria-hidden="true" />
      ) : (
        <div className={styles.sectionHeading}>{sectionLabel(section.heading)}</div>
      )}
      {section.items.map((item) => renderNavLink(
        item.key,
        item.label,
        item.icon,
        navItemPath(projectId as string, item),
      ))}
    </div>
  );

  return (
    <aside
      className={mergeClasses(styles.root, collapsed && styles.rootCollapsed)}
      aria-label="Primary navigation"
      data-testid="app-navigation-menu"
      data-collapsed={collapsed ? 'true' : 'false'}
    >
      <div className={mergeClasses(styles.chrome, collapsed && styles.chromeCollapsed)}>
        <Link
          to="/"
          aria-label="Agentweaver home"
          className={mergeClasses(styles.brandRow, collapsed && styles.brandRowCollapsed)}
        >
          <img src="/agentweaver.png" alt="Agentweaver" className={styles.brandLogo} />
          {!collapsed && <span className={styles.brandName}>Agentweaver</span>}
        </Link>
        <Button
          className={styles.collapseButton}
          appearance="subtle"
          icon={collapsed ? <PanelLeftExpand24Regular /> : <PanelLeftContract24Regular />}
          aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
          aria-expanded={!collapsed}
          onClick={toggleCollapsed}
        />
      </div>

      <nav
        className={mergeClasses(styles.navScroll, collapsed && styles.navScrollCollapsed)}
        aria-label="Primary navigation links"
        data-testid="app-navigation-scroll"
        data-scrollbar-mode={collapsed ? 'hidden' : 'hover'}
        tabIndex={0}
      >
        <div
          className={mergeClasses(styles.section, !collapsed && styles.sectionExpanded)}
          role="group"
          aria-label="Global"
        >
          {GLOBAL_NAV_ITEMS.map((item) => renderNavLink(item.key, item.label, item.icon, item.path))}
        </div>

        {projectId && (
          <>
            {primarySections.map((section) => renderSection(section))}
            {bottomSections.map((section) => (
              <Fragment key={section.heading}>{renderSection(section, true)}</Fragment>
            ))}
          </>
        )}
      </nav>
    </aside>
  );
}
