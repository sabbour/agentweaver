import { describe, it, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    listProjects: vi.fn(),
    createProject: vi.fn(),
    listBlueprints: vi.fn(),
    generateBlueprint: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { ProjectGalleryPage } from '../pages/ProjectGalleryPage';
import { ProjectListProvider } from '../hooks/useProjectList';
import type { Blueprint } from '../api/types';

const GENERATED: Blueprint = {
  id: 'gen-triager',
  name: 'Bug Triager',
  description: 'Triages incoming bugs.',
  roster: ['triager', 'qa-engineer'],
  workflow: 'coordinator',
  review_policy: 'auto',
  sandbox_profile: 'standard',
};

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>
        <ProjectListProvider>{children}</ProjectListProvider>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data', workspace_auto_assigned: false } as never);
  vi.mocked(apiClient.listProjects).mockResolvedValue([]);
  vi.mocked(apiClient.listBlueprints).mockResolvedValue([]);
  vi.mocked(apiClient.generateBlueprint).mockResolvedValue({ blueprint: GENERATED });
});

afterEach(() => cleanup());

describe('debug blueprint test', () => {
  it('finds all buttons after blueprint generation', async () => {
    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create blank project' });
    fireEvent.click(trigger);

    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Project' } });
    fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'my-repo' } });
    fireEvent.change(screen.getByLabelText('Describe your project'), { target: { value: 'a bug triager' } });
    fireEvent.click(screen.getByRole('button', { name: /Generate blueprint/ }));

    await waitFor(() => screen.getByLabelText('Generated blueprint preview'));

    const allButtons = screen.queryAllByRole('button', { hidden: true });
    const buttonNames = allButtons.map(b => `"${b.textContent?.trim()}" aria-hidden=${b.closest('[aria-hidden="true"]') ? 'true' : 'none'}`);
    console.log('ALL BUTTONS:\n' + buttonNames.join('\n'));

    const createHidden = screen.queryByRole('button', { name: 'Create', hidden: true });
    console.log('Create button (hidden:true):', createHidden ? 'FOUND' : 'NOT FOUND');

    const createNormal = screen.queryByRole('button', { name: 'Create' });
    console.log('Create button (normal):', createNormal ? 'FOUND' : 'NOT FOUND');
  });
});
