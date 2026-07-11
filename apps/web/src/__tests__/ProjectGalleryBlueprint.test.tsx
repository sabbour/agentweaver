import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ProjectListProvider } from '../hooks/useProjectList';
import { ProjectGalleryPage } from '../pages/ProjectGalleryPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { Blueprint, Project } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    listProjects: vi.fn(),
    createProject: vi.fn(),
    listBlueprints: vi.fn(),
    generateBlueprint: vi.fn(),
    suggestBlueprint: vi.fn(),
  },
}));

function makeProject(id: string, name: string): Project {
  return {
    project_id: id,
    name,
    origin: 'blank',
    source_repository: null,
    working_directory: '/data/x',
    default_branch: 'main',
    owner: 'me',
    default_provider: 'github-copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    blueprint_generation_model: null,
    workflow_generation_model: null,
    outcome_spec_generation_model: null,
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

const BP_BACKEND: Blueprint = {
  id: 'backend-squad',
  name: 'Backend Squad',
  description: 'A team for backend services.',
  roster: ['architect', 'backend-engineer'],
  workflow: 'coordinator',
  workflows: ['coordinator', 'release'],
  review_policy: 'auto',
  sandbox_profile: 'standard',
};

const BP_DOCS: Blueprint = {
  id: 'docs-team',
  name: 'Docs Team',
  description: 'Documentation reviewers.',
  roster: ['tech-writer'],
  workflow: 'single',
  workflows: ['single'],
  review_policy: 'manual',
  sandbox_profile: 'readonly',
};

const GENERATED: Blueprint = {
  id: 'gen-triager',
  name: 'Bug Triager',
  description: 'Triages incoming bugs.',
  roster: ['triager', 'qa-engineer'],
  workflow: 'coordinator',
  workflows: ['coordinator'],
  review_policy: 'auto',
  sandbox_profile: 'standard',
};

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>
        <ProjectListProvider>
          {children}
        </ProjectListProvider>
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data', workspace_auto_assigned: false } as never);
  vi.mocked(apiClient.listProjects).mockResolvedValue([]);
  vi.mocked(apiClient.listBlueprints).mockResolvedValue([BP_BACKEND, BP_DOCS]);
  vi.mocked(apiClient.suggestBlueprint).mockResolvedValue({
    recommended_blueprint: BP_BACKEND,
    rationale: 'Recommended for tests.',
    confidence: 0.8,
    signals: [],
    fallback: false,
  });
  vi.mocked(apiClient.createProject).mockImplementation(async () => makeProject('new', 'New'));
});

afterEach(() => cleanup());

async function openBlankDialog() {
  render(<Wrapper><ProjectGalleryPage /></Wrapper>);
  const trigger = await screen.findByRole('button', { name: 'Create blank project' });
  fireEvent.click(trigger);
}

function fillNameAndFolder() {
  fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Project' } });
  fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'my-repo' } });
}

describe('ProjectGalleryPage — blueprint selection', () => {
  it('lists predefined blueprints in the picker', async () => {
    await openBlankDialog();
    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());
    expect(screen.getByText('Docs Team')).toBeDefined();
    // The roster now lives in a focus/hover popover, not inline. Focusing the
    // row reveals it (this is the keyboard-accessible path screen readers use).
    fireEvent.focus(screen.getByRole('radio', { name: 'Backend Squad' }));
    await waitFor(() => expect(screen.getByText('backend-engineer')).toBeDefined());
    // A blueprint bundles one or more workflows; the roster meta lists them all.
    expect(screen.getByText('Workflows: coordinator, release')).toBeDefined();
  });

  it('unwraps the blueprint list response wrapper', async () => {
    vi.mocked(apiClient.listBlueprints).mockResolvedValue({
      blueprints: [BP_BACKEND, BP_DOCS],
    } as never);

    await openBlankDialog();

    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());
    expect(screen.getByText('Docs Team')).toBeDefined();
  });

  it('keeps rendering when the blueprint payload is malformed', async () => {
    vi.mocked(apiClient.listBlueprints).mockResolvedValue({
      blueprints: { unexpected: true },
    } as never);

    await openBlankDialog();

    await waitFor(() => expect(apiClient.listBlueprints).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'No blueprint' })).toBeDefined();
    expect(screen.queryByRole('radio', { name: 'No blueprint' })).toBeNull();

    fillNameAndFolder();
    fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.blueprint_id).toBeUndefined();
    expect(req.blueprint).toBeUndefined();
  });

  it('generates a blueprint from a description and shows the preview', async () => {
    vi.mocked(apiClient.generateBlueprint).mockResolvedValue({
      blueprint: GENERATED,
    });

    await openBlankDialog();
    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());

    // Generation now lives on the shared Generate tab (unified with GitHub).
    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
    fireEvent.change(screen.getByLabelText('Describe what you want Agentweaver to do'), {
      target: { value: 'handle job searches' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Generate blueprint/ }));

    await waitFor(() =>
      expect(apiClient.generateBlueprint).toHaveBeenCalledWith('handle job searches'),
    );
    // Preview card surfaces the generated blueprint and its roster.
    await waitFor(() => expect(screen.getByLabelText('Generated blueprint preview')).toBeDefined());
    expect(screen.getAllByText('Bug Triager').length).toBeGreaterThan(0);
    expect(screen.getByText('qa-engineer')).toBeDefined();
  });

  it('submits blueprint_id when a predefined blueprint is selected', async () => {
    await openBlankDialog();
    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());

    fillNameAndFolder();
    fireEvent.click(screen.getByRole('radio', { name: /Backend Squad/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.blueprint_id).toBe('backend-squad');
    expect(req.blueprint).toBeUndefined();
  });

  it('submits the inline blueprint when a generated blueprint is applied', async () => {
    vi.mocked(apiClient.generateBlueprint).mockResolvedValue({
      blueprint: GENERATED,
    });

    await openBlankDialog();
    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());

    fillNameAndFolder();
    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
    fireEvent.change(screen.getByLabelText('Describe what you want Agentweaver to do'), {
      target: { value: 'a bug triager' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Generate blueprint/ }));
    await waitFor(() => expect(screen.getByLabelText('Generated blueprint preview')).toBeDefined());

    const createButton = await waitFor(() => screen.getByRole('button', { name: 'Create', hidden: true }) as HTMLButtonElement);
    expect(createButton.disabled).toBe(false);
    fireEvent.click(createButton);

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.blueprint_id).toBeUndefined();
    expect(req.blueprint?.id).toBe('gen-triager');
    expect((req as { new_roles?: unknown }).new_roles).toBeUndefined();
  });

  it('keeps create disabled after generated blueprint selection when required fields are missing', async () => {
    vi.mocked(apiClient.generateBlueprint).mockResolvedValue({
      blueprint: GENERATED,
    });

    await openBlankDialog();
    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
    fireEvent.change(screen.getByLabelText('Describe what you want Agentweaver to do'), {
      target: { value: 'a bug triager' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Generate blueprint/ }));

    await waitFor(() => expect(screen.getByLabelText('Generated blueprint preview')).toBeDefined());
    await waitFor(() => expect((screen.getByRole('button', { name: 'Create', hidden: true }) as HTMLButtonElement).disabled).toBe(true));
  });

  it('creates with no blueprint when the user skips', async () => {
    await openBlankDialog();
    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());

    fillNameAndFolder();
    fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.blueprint_id).toBeUndefined();
    expect(req.blueprint).toBeUndefined();
  });
});

describe('ProjectGalleryPage — workspace_auto_assigned', () => {
  it('hides the Repository folder field when workspace_auto_assigned is true', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      workspace_auto_assigned: true,
    } as never);

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create blank project' });
    fireEvent.click(trigger);

    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());
    expect(screen.queryByPlaceholderText('my-repo')).toBeNull();
    expect(screen.queryByText(/Repository folder/)).toBeNull();
  });

  it('derives working_directory from name slug when workspace_auto_assigned is true', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      workspace_auto_assigned: true,
    } as never);

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create blank project' });
    fireEvent.click(trigger);

    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());

    // Only the name field is needed — no folder field to fill.
    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Project' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.working_directory).toBe('my-project');
  });

  it('shows the Repository folder field when workspace_auto_assigned is false (default)', async () => {
    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create blank project' });
    fireEvent.click(trigger);

    await waitFor(() => expect(screen.getByText('Backend Squad')).toBeDefined());
    expect(screen.getByPlaceholderText('my-repo')).toBeDefined();
  });
});
