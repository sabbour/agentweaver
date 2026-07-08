import { useState } from 'react';
import { CreateResourcePattern, Field, Input, type AzfAction } from '..';

const createAction: AzfAction = {
  id: 'create',
  label: 'Create',
  appearance: 'primary',
  onClick: () => undefined,
};

export function CreateResourcePatternExample() {
  const [currentStepId, setCurrentStepId] = useState('basics');

  return (
    <CreateResourcePattern
      title="Create storage account"
      subtitle="Derived from Forms + Step Wizard evidence"
      currentStepId={currentStepId}
      onStepSelect={setCurrentStepId}
      validationSummary={currentStepId === 'review' ? undefined : 'Subscription and resource group are required before review.'}
      primaryAction={createAction}
      secondaryAction={{ id: 'cancel', label: 'Cancel', onClick: () => undefined }}
      feedback="The footer stays fixed while the blade content scrolls."
      steps={[
        {
          id: 'basics',
          label: 'Basics',
          content: (
            <div>
              <Field label="Subscription">
                <Input value="Contoso production" readOnly />
              </Field>
              <Field label="Resource group">
                <Input value="rg-contoso-shared" readOnly />
              </Field>
            </div>
          ),
        },
        {
          id: 'review',
          label: 'Review + create',
          content: <p>Show validation, confirmation language, and any deployment warnings before the final action.</p>,
        },
      ]}
      reviewContent={currentStepId === 'review' ? <p>Check naming, region, SKU, and diagnostics settings before submission.</p> : undefined}
    />
  );
}
