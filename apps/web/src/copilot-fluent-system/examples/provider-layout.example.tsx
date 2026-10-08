import { useMemo, useState } from 'react';
import { CubeRegular, DataTrendingRegular, SettingsRegular } from '@fluentui/react-icons';
import { AzureFluentProvider, BladeHeader, ManageResourcePattern, ServiceMenu, type AzfServiceMenuGroup } from '..';
const serviceMenuGroups: AzfServiceMenuGroup[] = [
  {
    id: 'overview',
    label: 'Overview',
    items: [
      { id: 'overview', label: 'Overview', icon: <CubeRegular /> },
      { id: 'activity', label: 'Activity log', icon: <DataTrendingRegular /> },
      { id: 'settings', label: 'Settings', icon: <SettingsRegular /> },
    ],
    defaultOpen: true,
  },
];

export function ProviderLayoutExample() {
  const [selectedId, setSelectedId] = useState('overview');

  const content = useMemo(() => {
    switch (selectedId) {
      case 'activity':
        return <p>Use the main content area for data surfaces, charts, and activity details.</p>;
      case 'settings':
        return <p>Keep app-specific forms and data fetching outside the library; compose them inside the layout shell.</p>;
      default:
        return <p>Wrap the page once with AzureFluentProvider so tokens, density, and Fluent v9 theme wiring stay consistent.</p>;
    }
  }, [selectedId]);

  return (
    <AzureFluentProvider density="compact">
      <ManageResourcePattern
        header={
          <BladeHeader
            title="Virtual machines"
            subtitle="Sample subscription A subscription"
            actions={[
              { id: 'refresh', label: 'Refresh', onClick: () => undefined },
              { id: 'pin', label: 'Pin', appearance: 'subtle', onClick: () => undefined },
            ]}
          />
        }
        serviceMenu={
          <ServiceMenu
            groups={serviceMenuGroups}
            selectedId={selectedId}
            onSelect={setSelectedId}
            searchValue=""
            onSearchChange={() => undefined}
          />
        }
      >
        <section aria-label="Resource content">
          <h2>{selectedId === 'overview' ? 'Overview' : selectedId === 'activity' ? 'Activity log' : 'Settings'}</h2>
          {content}
        </section>
      </ManageResourcePattern>
    </AzureFluentProvider>
  );
}
