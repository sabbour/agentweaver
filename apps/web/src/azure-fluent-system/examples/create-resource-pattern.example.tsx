import { useState } from 'react';
import { CreateResourcePattern, FormFieldRow, Input, StatusIconText, type AzfAction } from '..';

const createAction: AzfAction = {
  id: 'create',
  label: 'Create',
  appearance: 'primary',
  onClick: () => undefined,
};

export function CreateResourcePatternExample() {
  const [currentStepId, setCurrentStepId] = useState('basics');
  const [deleteProtection, setDeleteProtection] = useState('Enabled');

  return (
    <CreateResourcePattern
      title="Create storage account"
      subtitle="Derived from Forms + Step Wizard references"
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
          description: 'Name, scope, and default protection',
          content: (
            <div className="azf-stack azf-gap-m">
              <FormFieldRow
                label="Subscription"
                htmlFor="create-resource-subscription"
                info="Subscriptions scope quota, billing, and policy. Keep the current subscription unless the deployment needs isolation."
                status={<StatusIconText status="info">Inherited from the current tenant context.</StatusIconText>}
              >
                <Input id="create-resource-subscription" value="Contoso production" readOnly />
              </FormFieldRow>
              <FormFieldRow
                label="Resource group"
                htmlFor="create-resource-group"
                hint="Use a shared resource group only when lifecycle ownership already matches."
              >
                <Input id="create-resource-group" value="rg-contoso-shared" readOnly />
              </FormFieldRow>
              <FormFieldRow
                label="Delete protection"
                htmlFor="create-resource-protection"
                hint="This mirrors the small status line + helper layout from the Azure form-row references."
              >
                <Input id="create-resource-protection" value={deleteProtection} onChange={(_, data) => setDeleteProtection(data.value)} />
              </FormFieldRow>
            </div>
          ),
        },
        {
          id: 'review',
          label: 'Review + create',
          description: 'Validate naming, region, and diagnostics',
          status: 'warning',
          content: <p>Show validation, confirmation language, and any deployment warnings before the final action.</p>,
        },
      ]}
      reviewContent={currentStepId === 'review' ? <p>Check naming, region, SKU, and diagnostics settings before submission.</p> : undefined}
    />
  );
}
