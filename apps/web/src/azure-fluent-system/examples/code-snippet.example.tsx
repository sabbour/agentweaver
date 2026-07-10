import { CodeSnippet } from '..';

const lines = [
  { lineNumber: 1, text: '{', foldState: 'expanded' as const },
  {
    lineNumber: 2,
    indentLevel: 1,
    tokens: [
      { text: '"$schema"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '"https://schema.management.azure.com/schemas/2015-01-01/deploymentTemplate.json#"' },
      { text: ',', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 3,
    indentLevel: 1,
    tokens: [
      { text: '"parameters"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '{ ... }', tone: 'muted' as const },
    ],
    foldState: 'collapsed' as const,
  },
  { lineNumber: 4, text: '}', tokens: [{ text: '}', tone: 'operator' as const }] },
];

export function CodeSnippetExample() {
  return <CodeSnippet title="ARM template" lines={lines} maxHeight={220} />;
}
