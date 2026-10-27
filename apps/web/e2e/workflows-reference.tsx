/**
 * Isolated retained 0.x WorkflowsPage, not a running historical application.
 * The page matches fb24c71c4d912354998ccaf775af12be4a049798 except comments.
 * Playwright supplies the old catalog contract; no live service is contacted.
 */
import { FluentProvider } from '@fluentui/react-components';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { WorkflowsPage } from '../src/pages/WorkflowsPage';
import { agentweaverLightTheme } from '../src/theme';

createRoot(document.getElementById('root')!).render(
  <FluentProvider theme={agentweaverLightTheme}>
    <style>{'body { margin: 0; }'}</style>
    <MemoryRouter initialEntries={['/projects/p1/workflows']}>
      <Routes><Route path="/projects/:projectId/workflows" element={<WorkflowsPage />} /></Routes>
    </MemoryRouter>
  </FluentProvider>,
);
