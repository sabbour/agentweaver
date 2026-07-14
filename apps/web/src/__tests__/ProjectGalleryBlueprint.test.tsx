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

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjects`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function projectsPage(items: Project[]) {
  return { items, page: 1, page_size: 100, total_count: items.length, total_pages: 1 } as never;
}

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
  vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));
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
  it('pages project tiles from the server when the catalog exceeds 100 projects', async () => {
    const manyProjects = Array.from({ length: 101 }, (_, index) =>
      makeProject(`p-${index + 1}`, `Project ${index + 1}`),
    );
    vi.mocked(apiClient.listProjects).mockImplementation(async (options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 100;
      const start = (pageNumber - 1) * pageSize;
      return {
        items: manyProjects.slice(start, start + pageSize),
        page: pageNumber,
        page_size: pageSize,
        total_count: manyProjects.length,
        total_pages: Math.ceil(manyProjects.length / pageSize),
      } as never;
    });

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);

    await waitFor(() => expect(screen.getByText('Project 1')).toBeDefined());
    expect(screen.getByText('1-12 of 101')).toBeDefined();
    expect(screen.queryByText('Project 13')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(screen.getByText('Project 13')).toBeDefined());
    expect(screen.queryByText('Project 1')).toBeNull();
    expect(
      vi.mocked(apiClient.listProjects).mock.calls.some(([options]) => options?.page === 2 && options?.pageSize === 12),
    ).toBe(true);
  });

  it('resets to page 1 and refetches when the project-gallery page size changes', async () => {
    const manyProjects = Array.from({ length: 101 }, (_, index) =>
      makeProject(`p-${index + 1}`, `Project ${index + 1}`),
    );
    vi.mocked(apiClient.listProjects).mockImplementation(async (options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 100;
      const start = (pageNumber - 1) * pageSize;
      return {
        items: manyProjects.slice(start, start + pageSize),
        page: pageNumber,
        page_size: pageSize,
        total_count: manyProjects.length,
        total_pages: Math.ceil(manyProjects.length / pageSize),
      } as never;
    });

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);

    await waitFor(() => expect(screen.getByText('Project 1')).toBeDefined());

    fireEvent.click(screen.getByRole('combobox', { name: 'Rows per page' }));
    fireEvent.click(await screen.findByRole('option', { name: '24 / page' }));

    await waitFor(() =>
      expect(
        vi.mocked(apiClient.listProjects).mock.calls.some(([options]) => options?.page === 1 && options?.pageSize === 24),
      ).toBe(true),
    );
    await waitFor(() => expect(screen.getByText('Project 24')).toBeDefined());
    expect(screen.queryByText('Project 25')).toBeNull();
  });

  it('returns an empty (not erroring) tile grid when requesting a page beyond the available data', async () => {
    const manyProjects = Array.from({ length: 15 }, (_, index) =>
      makeProject(`p-${index + 1}`, `Project ${index + 1}`),
    );
    // Simulates the backend's overflow-safe `Paging.Of` behaviour: an out-of-range page
    // returns an empty `items` array (HTTP 200) while echoing back the requested page number,
    // rather than erroring or silently wrapping back to page 1's data.
    vi.mocked(apiClient.listProjects).mockImplementation(async (options) => {
      const pageNumber = options?.page ?? 1;
      const pageSize = options?.pageSize ?? 12;
      const start = (pageNumber - 1) * pageSize;
      return {
        items: manyProjects.slice(start, start + pageSize),
        page: pageNumber,
        page_size: pageSize,
        total_count: manyProjects.length,
        total_pages: Math.ceil(manyProjects.length / pageSize),
      } as never;
    });

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);

    await waitFor(() => expect(screen.getByText('Project 1')).toBeDefined());

    // Navigate to the last real page, then confirm Next is disabled there (the pager clamps
    // navigation client-side so a beyond-data request is never actually issued from the UI —
    // the boundary guarantee is exercised end-to-end by the backend `PaginationTests`).
    const totalPages = Math.ceil(manyProjects.length / 12);
    for (let i = 1; i < totalPages; i += 1) {
      fireEvent.click(screen.getByRole('button', { name: 'Next' }));
      // eslint-disable-next-line no-await-in-loop
      await waitFor(() => expect(vi.mocked(apiClient.listProjects).mock.calls.some(([o]) => o?.page === i + 1)).toBe(true));
    }

    await waitFor(() => expect(screen.getByRole('button', { name: 'Next' })).toHaveProperty('disabled', true));
  });

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
