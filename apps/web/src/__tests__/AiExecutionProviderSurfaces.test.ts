import { readdirSync, readFileSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      return entry.name === '__tests__' ? [] : sourceFiles(path);
    }
    return entry.name.endsWith('.tsx') ? [path] : [];
  });
}

const expectedProviderSurfaces = [
  'components/AgentSessionPanel.tsx',
  'components/ArtifactBrowser.tsx',
  'components/BlueprintPicker.tsx',
  'components/CoordinatorTopologyGraph.tsx',
  'components/DecomposePreviewDialog.tsx',
  'components/OutcomePlanPanel.tsx',
  'components/ReviewPanel.tsx',
  'components/StartOrchestrationDialog.tsx',
  'components/StartOrchestrationFab.tsx',
  'components/SteerChatPanel.tsx',
  'components/SteerPanel.tsx',
  'components/board/KanbanBoard.tsx',
  'components/board/RunCard.tsx',
  'pages/AssistantRunPage.tsx',
  'pages/CastingWizardPage.tsx',
  'pages/CoordinatorRunPage.tsx',
  'pages/SkillsPage.tsx',
  'pages/WorkflowsPage.tsx',
  'pages/WorkspacePage.tsx',
].sort();

describe('AI execution provider presentation coverage', () => {
  it('keeps every effective-provider surface on the shared presentation component', () => {
    const actual = sourceFiles(srcRoot)
      .filter((path) => !path.endsWith('AiExecutionProviderHint.tsx'))
      .filter((path) => /<AiExecutionProvider(?:Hint|Status|Indicator)\b/.test(readFileSync(path, 'utf8')))
      .map((path) => relative(srcRoot, path).replaceAll('\\', '/'))
      .sort();

    expect(actual).toEqual(expectedProviderSurfaces);
  });

  it('does not reintroduce visible Used or Expected provider copy outside the shared component', () => {
    const violations = sourceFiles(srcRoot)
      .filter((path) => !path.endsWith('AiExecutionProviderHint.tsx'))
      .filter((path) => /Expected provider:|Using (?:GitHub Copilot|.*BYOK)|Used (?:GitHub Copilot|.*BYOK)/.test(
        readFileSync(path, 'utf8'),
      ))
      .map((path) => relative(srcRoot, path).replaceAll('\\', '/'));

    expect(violations).toEqual([]);
  });
});
