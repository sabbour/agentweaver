import { useState } from 'react';
import { AzureSlider } from '..';

export function AzureSliderExample() {
  const [cores, setCores] = useState(4);

  return (
    <div className="azf-stack azf-gap-m">
      <AzureSlider
        label="Provisioned vCores"
        info="Scale compute for the elastic pool. Applies at the next maintenance window."
        min={2}
        max={32}
        step={2}
        value={cores}
        onChange={setCores}
        showValue
        formatValue={(value) => `${value} vCores`}
      />
      <AzureSlider label="Disabled" min={0} max={100} defaultValue={40} disabled />
    </div>
  );
}
