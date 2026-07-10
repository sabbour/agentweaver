import { useMemo, useState } from 'react';
import {
  AppsListRegular,
  DataTrendingRegular,
  HomeRegular,
  NavigationRegular,
  PersonCircleRegular,
  SettingsRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import {
  AzureDataGrid,
  BladeHeader,
  CommandBar,
  FeedbackFooter,
  FilterBar,
  PortalLayout,
  PortalRail,
  PortalTopNav,
  StatusIconText,
  Text,
  type AzfColumn,
  type AzfFilter,
} from '..';

interface ResourceRow {
  id: string;
  name: string;
  owner: string;
  status: 'Healthy' | 'Needs attention';
}

const rows: ResourceRow[] = [
  { id: 'run-1', name: 'AKS fleet rollout', owner: 'Platform operations', status: 'Healthy' },
  { id: 'run-2', name: 'Storage backup review', owner: 'Operations', status: 'Needs attention' },
];

const columns: AzfColumn<ResourceRow>[] = [
  { columnId: 'name', header: 'Resource', sortable: true, sortValue: (item) => item.name, renderCell: (item) => item.name },
  { columnId: 'owner', header: 'Owner', sortable: true, sortValue: (item) => item.owner, renderCell: (item) => item.owner },
  {
    columnId: 'status',
    header: 'Status',
    sortable: true,
    sortValue: (item) => item.status,
    renderCell: (item) => (
      <StatusIconText status={item.status === 'Healthy' ? 'success' : 'warning'}>
        {item.status}
      </StatusIconText>
    ),
  },
];

export function PortalShellExample() {
  const [searchValue, setSearchValue] = useState('');

  const filteredRows = useMemo(
    () => rows.filter((row) => !searchValue.trim() || row.name.toLowerCase().includes(searchValue.toLowerCase())),
    [searchValue],
  );

  const filters: AzfFilter[] = [
    {
      id: 'owner',
      label: 'Owner',
      value: 'Platform + Operations',
      selected: true,
      removable: false,
    },
  ];

  return (
    <PortalLayout
      topNav={
        <PortalTopNav
          brand={{ product: 'Microsoft Azure', area: 'Portal' }}
          startActions={[
            { id: 'all-services', label: 'All services', icon: <AppsListRegular /> },
            { id: 'toggle-nav', label: 'Toggle navigation', icon: <NavigationRegular /> },
          ]}
          searchValue={searchValue}
          onSearchChange={setSearchValue}
          copilotAction={{ id: 'copilot', label: 'Copilot', icon: <SparkleRegular /> }}
          endActions={[{ id: 'settings', label: 'Settings', icon: <SettingsRegular /> }]}
          persona={{ name: 'Signed-in user', secondaryText: 'Organization directory', icon: <PersonCircleRegular /> }}
        />
      }
      rail={
        <PortalRail
          items={[
            { id: 'home', label: 'Home', icon: <HomeRegular />, selected: true },
            { id: 'insights', label: 'Insights', icon: <DataTrendingRegular /> },
          ]}
        />
      }
      breadcrumb={<Text>Home / Kubernetes services</Text>}
      header={<BladeHeader title="Kubernetes resources" subtitle="Organization directory" />}
      commandBar={
        <CommandBar
          primaryActions={[
            { id: 'create', label: 'Create', appearance: 'primary', onClick: () => undefined },
            { id: 'refresh', label: 'Refresh', onClick: () => undefined },
          ]}
          secondaryActions={[{ id: 'open-docs', label: 'Open docs', onClick: () => undefined }]}
        />
      }
      filters={<FilterBar filters={filters} searchValue={searchValue} onSearchChange={setSearchValue} />}
      footer={
        <FeedbackFooter
          body="Use the shell slots to keep page chrome consistent while each blade owns its own grid or form content."
          action={{ id: 'give-feedback', label: 'Give feedback', onClick: () => undefined }}
        />
      }
    >
      <AzureDataGrid
        items={filteredRows}
        columns={columns}
        emptyState="No resources matched the current shell query."
      />
    </PortalLayout>
  );
}
