/**
 * Page building blocks for v1 routes, matching the 0.x page header, breadcrumb,
 * notice, and panel patterns.
 */
import { Badge, MessageBar, MessageBarBody, Spinner, Tooltip, makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { PageHeader } from '../components/ui/PageHeader';
import { PageSection } from '../components/ui/PageSection';
import type { EffectiveProjectRole } from './contracts';
import { useProjectSummary } from './projectData';

const useStyles = makeStyles({
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
    minWidth: 0,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    fontWeight: tokens.fontWeightSemibold,
    ':hover': { textDecorationLine: 'underline' },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  current: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
});

/** Accessible error banner shared by authentication and data pages. */
export function ErrorNotice({ children, code }: { children: ReactNode; code?: string }) {
  return (
    <MessageBar intent="error" role="alert">
      <MessageBarBody>
        {code && <strong>{code}: </strong>}
        {children}
      </MessageBarBody>
    </MessageBar>
  );
}

/** Shared loading indicator for route content awaiting Gateway data. */
export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <div className="v1-loading"><Spinner label={label} /></div>;
}

/** Standard titled content panel with an optional action area. */
export function Panel({ title, children, actions, description }: {
  title: string;
  children: ReactNode;
  actions?: ReactNode;
  description?: string;
}) {
  return <PageSection title={title} description={description} actions={actions}>{children}</PageSection>;
}

/**
 * 0.x breadcrumb: `Projects / project` on the project home and
 * `Projects / project / page` on other project pages.
 */
export function ProjectBreadcrumb({ projectId, page, name }: { projectId: string; page?: string; name: string }) {
  const styles = useStyles();
  return (
    <nav className={styles.breadcrumb} aria-label="Breadcrumb">
      <Link to="/projects" className={styles.breadcrumbLink}>Projects</Link>
      <span aria-hidden="true">/</span>
      {/* On a sub-page the project name is a link and the page name is current.
          On the project home the project name itself is current. */}
      {page ? (
        <>
          <Link to={`/projects/${encodeURIComponent(projectId)}`} className={styles.breadcrumbLink}>{name}</Link>
          <span aria-hidden="true">/</span>
          <span className={styles.current} aria-current="page">{page}</span>
        </>
      ) : <span className={styles.current} aria-current="page">{name}</span>}
    </nav>
  );
}

/**
 * Shows the role names and resource scopes returned by the Projects owner.
 * This display is informational; protected operations still perform their own owner checks.
 */
export function ProjectRoleBadge({ roles }: { roles: readonly EffectiveProjectRole[] }) {
  if (roles.length === 0) return null;
  const label = (role: EffectiveProjectRole['role']) => `${role[0].toUpperCase()}${role.slice(1)}`;
  const names = roles.map((role) => label(role.role)).join(' · ');
  const detail = roles.map((role) =>
    `${label(role.role)} (${role.resourceType === 'tenant' ? 'tenant' : 'project'}: ${role.resourceId})`,
  ).join(', ');
  return (
    <Tooltip content={detail} relationship="description">
      <Badge appearance="outline" color="informative">{names}</Badge>
    </Tooltip>
  );
}

/**
 * Page header used by every v1 page. On project routes it adds the 0.x
 * breadcrumb and current owner-confirmed role display.
 */
export function PageHeading({ title, description, actions, page, breadcrumb = true }: {
  title: string;
  description?: string;
  actions?: ReactNode;
  /** Last breadcrumb segment. Omit on the project home. */
  page?: string;
  breadcrumb?: boolean;
}) {
  const { projectId } = useParams();
  const project = useProjectSummary(projectId ?? '');
  // One owner read supplies both the breadcrumb name and role display. No role
  // is shown until that project's current summary arrives.
  return <PageHeader
    title={title}
    description={description}
    breadcrumbs={projectId && breadcrumb ? (
      <ProjectBreadcrumb projectId={projectId} page={page} name={project.data?.name ?? projectId} />
    ) : undefined}
    actions={<>
      {projectId && project.data && <ProjectRoleBadge roles={project.data.effectiveRoles} />}
      {actions}
    </>}
  />;
}
