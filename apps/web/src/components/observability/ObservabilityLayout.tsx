import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { PageHeader } from '../PageHeader';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  tabs: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
});

export function ObservabilityLayout({
  projectId,
  projectName,
  activeTab,
  title,
  subtitle,
  actions,
  children,
}: {
  projectId: string;
  projectName?: string | null;
  activeTab: 'overview' | 'traces' | 'agents';
  title: string;
  subtitle: string;
  actions?: ReactNode;
  children: ReactNode;
}) {
  const styles = useStyles();
  const tabs = [
    { key: 'overview', label: 'Overview', href: `/projects/${projectId}/observability` },
    { key: 'traces', label: 'Traces', href: `/projects/${projectId}/observability/traces` },
    { key: 'agents', label: 'Agents', href: `/projects/${projectId}/observability/agents` },
  ] as const;

  return (
    <div className={styles.root}>
      <PageHeader
        title={title}
        subtitle={subtitle}
        breadcrumb={(
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {projectName ?? projectId}
            </Link>
            <span>/</span>
            <span>Observability</span>
          </div>
        )}
        actions={actions}
      />
      <div className={styles.tabs}>
        {tabs.map((tab) => (
          <Link key={tab.key} to={tab.href} style={{ textDecoration: 'none' }}>
            <Button appearance={activeTab === tab.key ? 'primary' : 'secondary'}>{tab.label}</Button>
          </Link>
        ))}
      </div>
      {children}
    </div>
  );
}
