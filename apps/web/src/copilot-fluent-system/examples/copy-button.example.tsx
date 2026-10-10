import { CopyButton } from '..';
export function CopyButtonExample() {
  return (
    <div className="azf-row azf-gap-s azf-wrap">
      <CopyButton value="resource-12345" />
      <CopyButton value="az aks show --name aks-cluster-sample" label="Click here to copy" />
    </div>
  );
}
