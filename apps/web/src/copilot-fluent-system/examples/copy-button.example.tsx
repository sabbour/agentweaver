import { CopyButton } from '..';
/** Shows icon-only and labeled copy controls using fixed sample values. */
export function CopyButtonExample() {
  return (
    <div className="azf-row azf-gap-s azf-wrap">
      <CopyButton value="resource-12345" />
      <CopyButton value="az aks show --name aks-cluster-sample" label="Click here to copy" />
    </div>
  );
}
