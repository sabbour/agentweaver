import { useMemo, useState } from 'react';
import { InfoRegular, SettingsRegular, ShieldTaskRegular } from '@fluentui/react-icons';
import { AzureTabList, Button, CalloutPopover, HelpPopover, Text } from '..';
const tabs = [
  { id: 'overview', label: 'Overview', icon: <InfoRegular /> },
  { id: 'security', label: 'Security', icon: <ShieldTaskRegular />, status: 'error' as const },
  { id: 'settings', label: 'Settings', icon: <SettingsRegular />, status: 'success' as const },
];

export function TabPopoversExample() {
  const [selectedValue, setSelectedValue] = useState('overview');

  const content = useMemo(() => {
    switch (selectedValue) {
      case 'security':
        return 'The security tab can call attention to expiring certificates, missing policy assignments, or blocking alerts.';
      case 'settings':
        return 'The settings tab is a good place for editable forms, command links, and inline callouts.';
      default:
        return 'Use tabs for top-level view switching inside a blade or split layout without rebuilding the host router.';
    }
  }, [selectedValue]);

  return (
    <div className="azf-stack azf-gap-s">
      <AzureTabList tabs={tabs} selectedValue={selectedValue} onTabSelect={setSelectedValue} />
      <Text>{content}</Text>
      <div className="azf-row azf-wrap azf-gap-s">
        <HelpPopover
          trigger={<Button appearance="subtle">What belongs in this tab?</Button>}
          title="Tab guidance"
          body="Keep each tab focused on one resource task, and let the host app own the route or persisted selection state."
          actions={[{ id: 'learn-more', label: 'Learn more', onClick: () => undefined }]}
        />
        <CalloutPopover
          trigger={<Button appearance="secondary">Show operator tip</Button>}
          tone="brand"
          title="Suggested action"
          body="Open the Security tab first when a badge indicates an error so the operator sees the blocking issue before editing settings."
        />
      </div>
    </div>
  );
}
