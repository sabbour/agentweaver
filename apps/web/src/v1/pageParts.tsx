/**
 * Page building blocks for v1 routes, matching the 0.x page header, breadcrumb,
 * notice, and panel patterns.
 */
import { Badge, MessageBar, MessageBarBody, Spinner, Tooltip, makeStyles, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { PageHeader } from '../components/ui/PageHeader';
import { PageSection } from '../components/ui/PageSection';
import {
  accessSummary,
  PERMISSION_LABELS,
  permissionsForProject,
  useAuthorizationContext,
  useProjectSummary,
} from './projectData';

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
export function ProjectBreadcrumb({ projectId, page }: { projectId: string; page?: string }) {
  const styles = useStyles();
  const project = useProjectSummary(projectId);
  // Show the ID until the project name loads, or if it cannot load.
  const name = project.data?.name ?? projectId;
  return (
    <nav className={styles.breadcrumb} aria-label="Breadcrumb">
      <Link to="/projects" className={styles.breadcrumbLink}>Projects</Link>
      <span aria-hidden="true">/</span>
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
 * Shows what the signed-in user can do in a project, from the owner's
 * authorization context. The tooltip lists each permission and where it comes from.
 */
export function ProjectAccessBadge({ projectId }: { projectId: string }) {
  const context = useAuthorizationContext();
  // Render nothing until permissions load, so the badge never shows a wrong guess.
  if (!context.data) return null;
  const permissions = permissionsForProject(context.data, projectId);
  const summary = accessSummary(permissions);
  // Tooltip text: each permission with its scope (project, tenant, or platform).
  const detail = permissions.length === 0
    ? 'You have no permissions in this project.'
    : permissions.map((item) => `${PERMISSION_LABELS[item.permission]} (${item.scope})`).join(', ');
  return (
    <Tooltip content={detail} relationship="description">
      <Badge appearance="outline" color={summary === 'Can edit' ? 'brand' : 'informative'}>{summary}</Badge>
    </Tooltip>
  );
}

/**
 * Page header used by every v1 page. On project routes it adds the 0.x
 * breadcrumb and the user's access badge.
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
  // Breadcrumb and access badge only make sense on project routes. A page can
  // pass breadcrumb={false} to hide the breadcrumb.
  return <PageHeader
    title={title}
    description={description}
    breadcrumbs={projectId && breadcrumb ? <ProjectBreadcrumb projectId={projectId} page={page} /> : undefined}
    actions={<>
      {projectId && <ProjectAccessBadge projectId={projectId} />}
      {actions}
    </>}
  />;
}
