import { Tab, TabList, makeStyles, tokens } from '@fluentui/react-components';
import { Link, useNavigate } from 'react-router-dom';
import type { ReactNode } from 'react';
import { PageContainer, PageHeader } from '../ui';

const useStyles = makeStyles({
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
});

export function ObservabilityLayout({
  projectId,
  projectName,
  activeTab,
  title,
  description,
  actions,
  children,
}: {
  projectId: string;
  projectName?: string | null;
  activeTab: 'overview' | 'traces' | 'agents';
  title: string;
  description?: string;
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
    <PageContainer>
      <PageHeader
        title={title}
        description={description}
        breadcrumbs={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {projectName ?? projectId}
            </Link>
            <span>/</span>
            <span>Observability</span>
          </div>
        }
        actions={actions}
      />
      <TabList
        selectedValue={activeTab}
        onTabSelect={(_, data) => {
          const selected = tabs.find((tab) => tab.key === data.value);
          if (selected) navigate(selected.href);
        }}
        aria-label="Observability sections"
      >
        {tabs.map((tab) => (
          <Tab key={tab.key} value={tab.key}>{tab.label}</Tab>
        ))}
      </TabList>
      {children}
    </PageContainer>
  );
}