import { AzureIcon, AzureIconProvider, createIconCloudRegistry, createIconCloudRegistryFromManifest } from '..';
const staticRegistry = createIconCloudRegistry(['VirtualMachine'] as const, {
  basePath: '/azure-icons',
  prefix: 'compute/',
});

const manifestRegistry = createIconCloudRegistryFromManifest(
  {
    icons: [
      { name: 'Storage Accounts', collection: 'Storage', category: 'Storage', file: 'storage/storage-accounts.svg' },
      { name: 'Virtual Machine', collection: 'Compute', category: 'Compute', file: 'compute/virtual-machine.svg' },
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
};

export function IconRegistryExample() {
  return (
    <AzureIconProvider registry={combinedRegistry}>
      <div>
        <AzureIcon name="VirtualMachine" label="Virtual machine" size={18} />
        <AzureIcon name="Compute/Virtual Machine" label="Compute virtual machine" size={20} />
        <AzureIcon name="Storage/Storage Accounts" label="Storage accounts" size={20} />
      </div>
    </AzureIconProvider>
  );
}
