import { BoxRegular } from '@fluentui/react-icons';
import { AzureIcon, AzureIconProvider, createIconCloudRegistry, createIconCloudRegistryFromManifest } from '..';

const staticRegistry = createIconCloudRegistry(['VirtualMachine'] as const, {
  basePath: '/azure-icons',
  prefix: 'compute/',
});

const manifestRegistry = createIconCloudRegistryFromManifest(
  {
    icons: [
      { name: 'Storage Accounts', collection: 'Storage', category: 'Storage', file: 'storage/storage-accounts.svg' },
      { name: 'Virtual Machines', collection: 'Compute', category: 'Compute', file: 'compute/virtual-machines.svg' },
    ],
  },
  {
    basePath: '/azure-icons',
    getKey: (icon) => `${icon.category}/${icon.name}`,
  },
);

const combinedRegistry = {
  ...staticRegistry,
  ...manifestRegistry,
  FluentFallback: { element: <BoxRegular />, alt: 'Fallback icon' },
};

export function IconRegistryExample() {
  return (
    <AzureIconProvider registry={combinedRegistry}>
      <div>
        <AzureIcon name="VirtualMachine" label="Virtual machine" size={18} />
        <AzureIcon name="Compute/Virtual Machines" label="Compute virtual machines" size={20} />
        <AzureIcon name="Storage/Storage Accounts" label="Storage accounts" size={20} />
        <AzureIcon name="FluentFallback" label="Fallback icon" size={16} />
      </div>
    </AzureIconProvider>
  );
}
