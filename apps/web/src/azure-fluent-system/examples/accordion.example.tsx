import { AzureAccordion, Text } from '..';

const items = [
  {
    id: 'summary',
    title: 'Deployment summary',
    content: (
      <div className="azf-stack azf-gap-xs">
        <Text weight="semibold">Rollout changes</Text>
        <Text className="azf-muted">Use the panel for details that are helpful but not required before the next action.</Text>
      </div>
    ),
  },
  {
    id: 'dependencies',
    title: 'Connected resources',
    content: (
      <div className="azf-stack azf-gap-xs">
        <Text weight="semibold">Dependency review</Text>
        <Text className="azf-muted">Collapsed sections should stay optional and quick to scan.</Text>
      </div>
    ),
  },
];

export function AccordionExample() {
  return <AzureAccordion items={items} defaultOpenItems={['dependencies']} multiple ariaLabel="Accordion example" />;
}
