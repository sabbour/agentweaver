import { useState } from 'react';
import { DeleteRegular } from '@fluentui/react-icons';
import {
  Button,
  DeleteResourceDialog,
  ErrorPattern,
  FeedbackFooter,
  NotificationPane,
  NotificationPattern,
  ServiceOverviewPattern,
} from '..';

export function ServiceOverviewFeedbackExample() {
  const [acknowledgedDelete, setAcknowledgedDelete] = useState(false);

  return (
    <div className="azf-stack azf-gap-m">
      <ServiceOverviewPattern
        title="Storage accounts"
        subtitle="Service overview patterns summarize actions, guidance, and health signals without custom page chrome."
        primaryAction={{ id: 'create', label: 'Create storage account', appearance: 'primary', onClick: () => undefined }}
        secondaryAction={{ id: 'open-docs', label: 'Open docs', onClick: () => undefined }}
        overviewCards={[
          {
            id: 'health',
            title: 'Health',
            body: '2 accounts need firewall updates before the next maintenance window.',
            actions: <Button appearance="subtle">Review recommendations</Button>,
          },
          {
            id: 'automation',
            title: 'Automation',
            body: 'Lifecycle policies are active on 6 of 8 accounts.',
            actions: <Button appearance="subtle">Open policy assignments</Button>,
          },
        ]}
      />

      <NotificationPane
        items={[
          {
            id: 'backup-policy',
            title: 'Backup policy updated',
            body: 'The nightly snapshot schedule now applies to every sample account in West US 2.',
            tone: 'success',
            timestamp: '2 min ago',
            actions: [{ id: 'view-change', label: 'View change', onClick: () => undefined }],
          },
          {
            id: 'firewall-check',
            title: 'Firewall validation still blocked',
            body: 'One storage account still allows public access. Resolve the private endpoint policy before the next rollout.',
            tone: 'warning',
            unread: true,
            timestamp: 'Now',
            actions: [{ id: 'open-offender', label: 'Open offending resource', onClick: () => undefined }],
          },
        ]}
        footer={
          <FeedbackFooter
            title="Was this notification placement useful?"
            body="Customer feedback stays low-emphasis and non-blocking."
            action={{ id: 'give-feedback', label: 'Give feedback', onClick: () => undefined }}
          />
        }
      />

      <NotificationPattern
        title="Backup policy updated"
        body="The nightly snapshot schedule now applies to every sample account in West US 2."
        intent="success"
        actions={<Button appearance="subtle">View change</Button>}
      />

      <ErrorPattern
        title="Firewall validation failed"
        body="One storage account still allows public access. Block deployment until the private endpoint policy is applied."
        actions={<Button appearance="subtle">Open offending resource</Button>}
      />

      <DeleteResourceDialog
        resourceName="stsampleshared01"
        softDelete
        trigger={<Button appearance="outline" icon={<DeleteRegular />}>Delete resource</Button>}
        confirmationText="Soft delete stays enabled for 14 days, but dependent workloads lose access immediately."
        consequences={[
          'Snapshots and restore points remain recoverable for the retention window.',
          'Applications that depend on this account lose access immediately.',
        ]}
        acknowledgement={{
          label: 'I understand this action affects connected workloads.',
          checked: acknowledgedDelete,
          onChange: setAcknowledgedDelete,
        }}
        onConfirm={() => undefined}
        onCancel={() => setAcknowledgedDelete(false)}
      />
    </div>
  );
}
