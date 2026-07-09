import { useState } from 'react';
import { FilterableComboBox } from '..';

const subscriptions = [
  { id: 'sub-prod', label: 'Contoso Production' },
  { id: 'sub-stage', label: 'Contoso Staging' },
  { id: 'sub-dev', label: 'Contoso Development' },
  { id: 'sub-shared', label: 'Shared Platform Services' },
  { id: 'sub-sandbox', label: 'Innovation Sandbox' },
];

export function FilterableComboBoxExample() {
  const [selected, setSelected] = useState<string | undefined>('sub-prod');

  return (
    <div className="azf-stack azf-gap-m">
      <FilterableComboBox
        label="Subscription"
        info="Type to filter across all subscriptions you can access."
        placeholder="Select a subscription"
        options={subscriptions}
        value={selected}
        onSelect={setSelected}
      />
    </div>
  );
}
