import { ProgressBarWithLabel } from '..';
export function ProgressBarWithLabelExample() {
  return (
    <div className="azf-stack azf-gap-l">
      <ProgressBarWithLabel
        label="Copying blobs"
        info="Server-side copy across regions. Safe to leave this blade."
        description="18 of 42 objects copied"
        value={0.42}
      />
      <ProgressBarWithLabel
        label="Provisioning environment"
        description="This can take a few minutes."
        indeterminate
      />
    </div>
  );
}
