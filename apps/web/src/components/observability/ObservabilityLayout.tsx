import {
  AzureTabList } from '../../copilot-fluent-system';
import { PageHeader } from '../PageHeader';
import { makeStyles,
  tokens,
} from '../../copilot-fluent-system';
import { Link, useNavigate } from 'react-router-dom';
import type { ReactNode } from 'react';
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
  const navigate = useNavigate();
  const tabs = [
    { key: 'overview', label: 'Overview', href: `/projects/${projectId}/observability` },
    { key: 'traces', label: 'Traces', href: `/projects/${projectId}/observability/traces` },
    { key: 'agents', label: 'Agents', href: `/projects/${projectId}/observability/agents` },
  ] as const;

  return (
    <div className={['azf-stack azf-page azf-pattern-shell', styles.root].filter(Boolean).join(' ')}>
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
      <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.tabs].filter(Boolean).join(' ')}>
        <AzureTabList
          ariaLabel="Observability sections"
          selectedValue={activeTab}
          onTabSelect={(value) => {
            const selected = tabs.find((tab) => tab.key === value);
            if (selected) navigate(selected.href);
          }}
          tabs={tabs.map((tab) => ({ id: tab.key, label: tab.label }))}
        />
      </div>
      {children}
    </div>
  );
}
