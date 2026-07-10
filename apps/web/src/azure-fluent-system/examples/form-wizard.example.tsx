import { useMemo, useState } from 'react';
import { AzureForm, FormFieldRow, FormFooter, Input, StatusIconText, StepWizardPattern, Text } from '..';

export function FormWizardExample() {
  const [subscription, setSubscription] = useState('Sample subscription A');
  const [resourceGroup, setResourceGroup] = useState('rg-sample-platform');
  const [currentStepId, setCurrentStepId] = useState('scope');

  const validationMessage = useMemo(() => {
    if (!subscription.trim()) {
      return 'Subscription is required.';
    }

    if (!resourceGroup.trim()) {
      return 'Resource group is required.';
    }

    return undefined;
  }, [resourceGroup, subscription]);

  return (
    <div className="azf-stack azf-gap-l">
      <AzureForm
        message="Use AzureForm when the host app owns field validation and submission."
        onSubmit={() => undefined}
        footer={
          <FormFooter
            primaryAction={{ id: 'save', label: 'Save draft', onClick: () => undefined }}
            secondaryAction={{ id: 'discard', label: 'Discard', appearance: 'secondary', onClick: () => undefined }}
            feedback={validationMessage ?? 'All required fields are present.'}
          />
        }
      >
        <FormFieldRow
          label="Subscription"
          htmlFor="form-wizard-subscription"
          info="Subscriptions control billing and quota. Keep the field aligned with the same fixed label column used in Azure blades."
          validationMessage={!subscription.trim() ? 'Required' : undefined}
        >
          <Input id="form-wizard-subscription" value={subscription} onChange={(_, data) => setSubscription(data.value)} />
        </FormFieldRow>
        <FormFieldRow
          label="Resource group"
          htmlFor="form-wizard-resource-group"
          hint="This status line stays inside the field column so operators can scan dense forms without extra card chrome."
          validationMessage={!resourceGroup.trim() ? 'Required' : undefined}
          status={<StatusIconText status="info">Inherited tags will apply after deployment.</StatusIconText>}
        >
          <Input id="form-wizard-resource-group" value={resourceGroup} onChange={(_, data) => setResourceGroup(data.value)} />
        </FormFieldRow>
      </AzureForm>

      <StepWizardPattern
        title="Create deployment target"
        subtitle="StepWizardPattern builds on the same form shell and footer contract."
        currentStepId={currentStepId}
        onStepSelect={setCurrentStepId}
        primaryAction={{
          id: currentStepId === 'review' ? 'deploy' : 'next',
          label: currentStepId === 'review' ? 'Deploy' : 'Next',
          onClick: () => setCurrentStepId((current) => (current === 'scope' ? 'configuration' : 'review')),
          disabled: Boolean(validationMessage) && currentStepId === 'scope',
        }}
        secondaryAction={{
          id: 'back',
          label: currentStepId === 'scope' ? 'Cancel' : 'Back',
          appearance: 'secondary',
          onClick: () => setCurrentStepId((current) => (current === 'review' ? 'configuration' : 'scope')),
        }}
        steps={[
          {
            id: 'scope',
            label: 'Scope',
            description: 'Choose tenant, subscription, and resource group',
            content: (
              <Text className="azf-muted">
                Capture tenant, subscription, and resource group selection before moving to configuration.
              </Text>
            ),
          },
          {
            id: 'configuration',
            label: 'Configuration',
            description: 'Apply compute, networking, and identity defaults',
            content: (
              <Text className="azf-muted">
                Add compute, networking, and identity settings here. Keep the field components in the host app.
              </Text>
            ),
          },
          {
            id: 'review',
            label: 'Review',
            description: 'Confirm validation and irreversible actions',
            status: 'warning',
            content: (
              <Text className="azf-muted">
                Summarize the final choices and surface any warnings before the irreversible action.
              </Text>
            ),
          },
        ]}
      />
    </div>
  );
}
