import { useState } from 'react';
import { Button, ResourceTagEditor, type AzfResourceTagRow } from '..';
const initialRows: AzfResourceTagRow[] = [
  { id: 'tag-1', name: 'env', value: 'sample', resourceId: 'vm-01' },
  { id: 'tag-2', name: 'owner', value: 'platform', resourceId: 'vm-02' },
];

const resources = [
  { id: 'vm-01', label: 'vm-sample-01' },
  { id: 'vm-02', label: 'vm-sample-02' },
];

export function ResourceTagEditorExample() {
  const [rows, setRows] = useState<AzfResourceTagRow[]>(initialRows);

  return (
    <div>
      <ResourceTagEditor
        rows={rows}
        resources={resources}
        validation={{
          'tag-2:value': rows[1]?.value ? '' : 'Value is required.',
        }}
        onRowChange={(rowId, patch) =>
          setRows((currentRows) =>
            currentRows.map((row) => (row.id === rowId ? { ...row, ...patch } : row)),
          )
        }
        onAddRow={() =>
          setRows((currentRows) => [
            ...currentRows,
            { id: `tag-${currentRows.length + 1}`, name: '', value: '', resourceId: resources[0]?.id },
          ])
        }
        onDeleteRow={(rowId) => setRows((currentRows) => currentRows.filter((row) => row.id !== rowId))}
      />
      <Button appearance="subtle" onClick={() => setRows(initialRows)}>
        Reset sample rows
      </Button>
    </div>
  );
}
