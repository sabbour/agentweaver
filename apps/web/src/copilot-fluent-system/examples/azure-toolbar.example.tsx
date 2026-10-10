import { AddRegular, ArrowClockwiseRegular, DeleteRegular, EditRegular } from '@fluentui/react-icons';
import { AzureToolbar } from '..';
export function AzureToolbarExample() {
  return (
    <AzureToolbar
      topOfPage
      ariaLabel="Resource commands"
      actions={[
        { id: 'create', label: 'Create', icon: <AddRegular />, appearance: 'primary' },
        { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular /> },
        { id: 'divider', label: '|' },
        { id: 'edit', label: 'Edit', icon: <EditRegular /> },
        { id: 'delete', label: 'Delete', icon: <DeleteRegular /> },
      ]}
    />
  );
}
