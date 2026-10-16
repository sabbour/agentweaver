import { useMemo, useState } from 'react';
import { AzureDataGrid, DataToolbar, FilterBar, Pager, type AzfColumn, type AzfFilter, type AzfPagerState } from '..';
interface ResourceRecord {
  id: string;
  name: string;
  type: string;
  region: string;
}

const allResources: ResourceRecord[] = [
  { id: 'vm-01', name: 'vm-sample-01', type: 'Virtual machine', region: 'West US 2' },
  { id: 'db-01', name: 'sql-sample-prod-01', type: 'SQL database', region: 'East US' },
  { id: 'st-01', name: 'stsampleshared01', type: 'Storage account', region: 'West US 2' },
];

const columns: AzfColumn<ResourceRecord>[] = [
  { columnId: 'name', header: 'Name', sortable: true, sortValue: (item) => item.name, renderCell: (item) => item.name },
  { columnId: 'type', header: 'Type', sortable: true, sortValue: (item) => item.type, renderCell: (item) => item.type },
  { columnId: 'region', header: 'Region', sortable: true, sortValue: (item) => item.region, renderCell: (item) => item.region },
];

export function AzureDataGridFilteringExample() {
  const [searchValue, setSearchValue] = useState('');
  const [selectedRegion, setSelectedRegion] = useState<string | undefined>('West US 2');
  const [pager, setPager] = useState<AzfPagerState>({ page: 1, pageSize: 10, totalItems: allResources.length });

  const filteredResources = useMemo(() => {
    return allResources.filter((resource) => {
      const matchesSearch = searchValue.length === 0 || resource.name.toLowerCase().includes(searchValue.toLowerCase());
      const matchesRegion = !selectedRegion || resource.region === selectedRegion;
      return matchesSearch && matchesRegion;
    });
  }, [searchValue, selectedRegion]);

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
    <section>
      <DataToolbar
        title="Resources"
        actions={[
          { id: 'create', label: 'Create', appearance: 'primary', onClick: () => undefined },
          { id: 'export', label: 'Export', onClick: () => undefined },
        ]}
      />
      <FilterBar filters={filters} searchValue={searchValue} onSearchChange={setSearchValue} searchPlaceholder="Search resources" />
      <AzureDataGrid
        items={filteredResources}
        columns={columns}
        getRowId={(resource) => resource.id}
        selectedRowId={filteredResources[0]?.id}
        onRowClick={(resource) => setSelectedRegion(resource.region)}
      />
      <Pager
        {...pager}
        totalItems={filteredResources.length}
        onPageChange={(page) => setPager((current) => ({ ...current, page }))}
        onPageSizeChange={(pageSize) => setPager((current) => ({ ...current, pageSize }))}
      />
    </section>
  );
}
