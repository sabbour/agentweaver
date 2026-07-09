import { useMemo, useState } from 'react';
import { AzureEmptyState, BrowseResourcePattern, Button, type AzfColumn, type AzfFilter, type AzfPagerState } from '..';

interface ResourceRecord {
  id: string;
  name: string;
  type: string;
  region: string;
  status: 'Healthy' | 'Needs attention';
}

const resources: ResourceRecord[] = [
  { id: 'aks-prod', name: 'aks-contoso-prod', type: 'AKS cluster', region: 'West US 2', status: 'Healthy' },
  { id: 'aks-dr', name: 'aks-contoso-dr', type: 'AKS cluster', region: 'East US', status: 'Needs attention' },
  { id: 'kv-shared', name: 'kv-contoso-shared', type: 'Key vault', region: 'West US 2', status: 'Healthy' },
  { id: 'vnet-prod', name: 'vnet-contoso-prod', type: 'Virtual network', region: 'Central US', status: 'Healthy' },
];

const columns: AzfColumn<ResourceRecord>[] = [
  { columnId: 'name', header: 'Name', sortable: true, sortValue: (item) => item.name, renderCell: (item) => item.name },
  { columnId: 'type', header: 'Type', sortable: true, sortValue: (item) => item.type, renderCell: (item) => item.type },
  { columnId: 'region', header: 'Region', sortable: true, sortValue: (item) => item.region, renderCell: (item) => item.region },
  { columnId: 'status', header: 'Status', sortable: true, sortValue: (item) => item.status, renderCell: (item) => item.status },
];

export function BrowseResourcePatternExample() {
  const [searchValue, setSearchValue] = useState('');
  const [selectedRegion, setSelectedRegion] = useState<string | undefined>('West US 2');
  const [showError, setShowError] = useState(false);
  const [pager, setPager] = useState<AzfPagerState>({ page: 1, pageSize: 2, totalItems: resources.length });

  const filteredItems = useMemo(() => {
    return resources.filter((resource) => {
      const matchesSearch = !searchValue.trim() || resource.name.toLowerCase().includes(searchValue.toLowerCase());
      const matchesRegion = !selectedRegion || resource.region === selectedRegion;
      return matchesSearch && matchesRegion;
    });
  }, [searchValue, selectedRegion]);

  const pagedItems = useMemo(() => {
    const start = (pager.page - 1) * pager.pageSize;
    return filteredItems.slice(start, start + pager.pageSize);
  }, [filteredItems, pager.page, pager.pageSize]);

  const filters: AzfFilter[] = [
    {
      id: 'region',
      label: 'Region',
      value: selectedRegion ?? 'All',
      selected: Boolean(selectedRegion),
      removable: Boolean(selectedRegion),
      onRemove: () => setSelectedRegion(undefined),
    },
  ];

  return (
    <div className="azf-stack azf-gap-s">
      <div className="azf-row azf-wrap azf-gap-s">
        <Button appearance="subtle" onClick={() => setSelectedRegion('West US 2')}>
          West US 2 filter
        </Button>
        <Button appearance="subtle" onClick={() => setSearchValue('missing')}>
          Empty state
        </Button>
        <Button appearance="subtle" onClick={() => setShowError((current) => !current)}>
          {showError ? 'Hide error' : 'Show error'}
        </Button>
      </div>

      <BrowseResourcePattern
        title="Platform resources"
        subtitle="Use the browse pattern when the page combines header, toolbar, filters, grid, and pager."
        items={pagedItems}
        columns={columns}
        filters={filters}
        toolbarActions={[
          { id: 'create', label: 'Create resource', appearance: 'primary', onClick: () => undefined },
          { id: 'export', label: 'Export CSV', onClick: () => undefined },
        ]}
        headerActions={[
          { id: 'refresh', label: 'Refresh', onClick: () => undefined },
        ]}
        pager={{ ...pager, totalItems: filteredItems.length }}
        onPageChange={(page) => setPager((current) => ({ ...current, page }))}
        onPageSizeChange={(pageSize) => setPager({ page: 1, pageSize, totalItems: filteredItems.length })}
        searchValue={searchValue}
        onSearchChange={(value) => {
          setSearchValue(value);
          setPager((current) => ({ ...current, page: 1 }));
        }}
        loading={false}
        error={showError ? 'Resource provider discovery timed out. Retry or narrow the scope.' : undefined}
        emptyState={
          <AzureEmptyState
            title="No resources matched the selected scope."
            body="Try clearing the region filter or broadening the text query."
            action={<Button appearance="subtle" onClick={() => { setSearchValue(''); setSelectedRegion(undefined); }}>Reset filters</Button>}
          />
        }
      />
    </div>
  );
}
