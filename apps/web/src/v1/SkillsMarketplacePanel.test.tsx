import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import {
  SkillsMarketplacePanel,
} from './SkillsMarketplacePanel';
import type {
  SkillsMarketplaceActions,
} from './SkillsMarketplacePanel';
import type {
  MarketplaceBrowsePage,
  MarketplaceSource,
  SkillContentPreview,
} from './contracts';
import type {
  VersionedProjectConfiguration,
} from './contracts';

const source: MarketplaceSource = {
  sourceId: 'source-1',
  name: 'Team skills',
  repository: 'example/skills',
  requestedRef: 'main',
  subpath: null,
  revision: 7,
  state: 'active',
};

const preview: SkillContentPreview = {
  name: 'Sample skill',
  description: 'A sample',
  contentDigest: 'sha256:sample',
  resourceCount: 1,
  totalBytes: 32,
};

const importedSkill = {
  skillId: 'skill-1',
  revision: 3,
  name: preview.name,
  description: preview.description,
  contentDigest: preview.contentDigest,
  resourceCount: preview.resourceCount,
  totalBytes: preview.totalBytes,
};

const browsePage: MarketplaceBrowsePage = {
  sourceId: source.sourceId,
  sourceRevision: source.revision,
  requestedRef: source.requestedRef,
  resolvedCommitSha: '0123456789abcdef',
  candidates: [{ location: 'skills/sample', name: 'Sample skill', description: 'A sample skill' }],
  total: 1,
  page: 1,
  pageSize: 25,
  hasMore: false,
};

function createActions(overrides: Partial<SkillsMarketplaceActions> = {}): SkillsMarketplaceActions {
  return {
    listSources: vi.fn().mockResolvedValue([]),
    createSource: vi.fn().mockResolvedValue(source),
    updateSource: vi.fn().mockResolvedValue({ ...source, revision: source.revision + 1 }),
    removeSource: vi.fn().mockResolvedValue({ ...source, state: 'removed', revision: source.revision + 1 }),
    browseSource: vi.fn().mockResolvedValue(browsePage),
    previewSkillContent: vi.fn().mockResolvedValue(preview),
    importSkillContent: vi.fn().mockResolvedValue(importedSkill),
    updateSkillAssignment: vi.fn().mockResolvedValue({
      projectId: 'project-1',
      revision: 5,
      configuration: {
        providerOverrides: [],
        orderedProviderOverrides: [],
        agentCharters: [],
        casting: [],
        blueprintWorkflowReferences: [],
        skills: [{
          skillId: importedSkill.skillId,
          enabled: true,
          order: 0,
          revision: importedSkill.revision,
          contentDigest: importedSkill.contentDigest,
          agentIds: ['agent-1'],
        }],
        runLimits: {},
      },
      updatedByActorId: 'actor-1',
      createdAt: '2026-10-10T00:00:00Z',
    } satisfies VersionedProjectConfiguration),
    ...overrides,
  };
}

function renderPanel(actions: SkillsMarketplaceActions | null) {
  return render(
    <SkillsMarketplacePanel
      projectId="project-1"
      configurationRevision={4}
      assignments={[]}
      agents={[{ agentId: 'agent-1', displayName: 'Agent One' }]}
      actions={actions}
    />,
  );
}

describe('Skills and marketplace panel', () => {
  it('does not assume owner state or enable actions when Gateway actions are absent', () => {
    renderPanel(null);

    expect(screen.getByText(/No source, import, assignment, or runtime state is assumed/)).toBeTruthy();
    expect(screen.getByText(/Runtime load status is not available from this API/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add source' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByRole('button', { name: 'Import skill to project' }).hasAttribute('disabled')).toBe(true);
  });

  it('pins browse results to the source revision and keeps source import disabled', async () => {
    const actions = createActions({
      listSources: vi.fn().mockResolvedValue([source]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
    });
    renderPanel(actions);

    await screen.findByText('example/skills · main · revision 7 · active');
    const browseButton = screen.getByRole('button', { name: 'Browse skills' }) as HTMLButtonElement;
    await waitFor(() => expect(browseButton.disabled).toBe(false));
    fireEvent.click(browseButton);
    await screen.findByText('0123456789abcdef');

    expect(actions.browseSource).toHaveBeenCalledWith('project-1', 'source-1', {
      expectedSourceRevision: 7,
      query: undefined,
      page: 1,
      pageSize: 25,
    });
    expect(screen.getByRole('button', { name: 'Import source skill' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByText(/selected-content preview.*pinned manifest and resources/i)).toBeTruthy();
  });

  it('keeps the selected files stable while their preview is pending', async () => {
    let resolvePreview!: (value: SkillContentPreview) => void;
    const pendingPreview = new Promise<SkillContentPreview>((resolve) => {
      resolvePreview = resolve;
    });
    const actions = createActions({
      previewSkillContent: vi.fn().mockReturnValue(pendingPreview),
    });
    renderPanel(actions);

    const fileInput = screen.getByLabelText('Skill folder or files') as HTMLInputElement;
    fireEvent.change(fileInput, {
      target: {
        files: [
          new File(['# Skill A'], 'SKILL.md', { type: 'text/markdown' }),
          new File(['Resource A'], 'README.md', { type: 'text/markdown' }),
        ],
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate skill files' }));

    await waitFor(() => expect(actions.previewSkillContent).toHaveBeenCalledOnce());
    expect(fileInput.disabled).toBe(true);
    fireEvent.change(fileInput, {
      target: { files: [new File(['# Skill B'], 'SKILL.md', { type: 'text/markdown' })] },
    });
    expect(screen.getByText('2 file(s) selected.')).toBeTruthy();

    await act(async () => {
      resolvePreview({ ...preview, name: 'Skill A' });
      await pendingPreview;
    });

    expect(await screen.findByText('Skill A')).toBeTruthy();
    expect(screen.getByText('2 file(s) selected.')).toBeTruthy();
    expect(actions.previewSkillContent).toHaveBeenCalledWith({
      skillMarkdown: btoa('# Skill A'),
      resources: [{ relativePath: 'README.md', content: btoa('Resource A') }],
    });
    expect((screen.getByRole('button', { name: 'Import skill to project' }) as HTMLButtonElement).disabled)
      .toBe(false);
  });

  it('validates local files, imports with the validated digest, and assigns to selected project agents', async () => {
    const assignment: VersionedProjectConfiguration = {
      projectId: 'project-1',
      revision: 5,
      configuration: {
        providerOverrides: [],
        orderedProviderOverrides: [],
        agentCharters: [],
        casting: [],
        blueprintWorkflowReferences: [],
        skills: [{
          skillId: importedSkill.skillId,
          enabled: true,
          order: 0,
          revision: importedSkill.revision,
          contentDigest: importedSkill.contentDigest,
          agentIds: ['agent-1'],
        }],
        runLimits: {},
      },
      updatedByActorId: 'actor-1',
      createdAt: '2026-10-10T00:00:00Z',
    };
    const actions = createActions({
      previewSkillContent: vi.fn().mockResolvedValue(preview),
      importSkillContent: vi.fn().mockResolvedValue(importedSkill),
      updateSkillAssignment: vi.fn().mockResolvedValue(assignment),
    });
    renderPanel(actions);

    const skillFile = new File(['# Sample skill'], 'SKILL.md', { type: 'text/markdown' });
    const resourceFile = new File(['Read me'], 'README.md', { type: 'text/markdown' });
    fireEvent.change(screen.getByLabelText('Skill folder or files'), {
      target: { files: [skillFile, resourceFile] },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate skill files' }));

    await screen.findByText('sha256:sample');
    expect(actions.previewSkillContent).toHaveBeenCalledWith({
      skillMarkdown: btoa('# Sample skill'),
      resources: [{ relativePath: 'README.md', content: btoa('Read me') }],
    });

    fireEvent.click(screen.getByRole('button', { name: 'Import skill to project' }));
    await screen.findByText(/The Skills owner imported Sample skill at revision 3/);
    await waitFor(() => expect(actions.importSkillContent).toHaveBeenCalledWith(
      'project-1',
      expect.objectContaining({
        expectedContentDigest: preview.contentDigest,
        candidate: {
          skillMarkdown: btoa('# Sample skill'),
          resources: [{ relativePath: 'README.md', content: btoa('Read me') }],
        },
      }),
    ));
    const importRequest = vi.mocked(actions.importSkillContent).mock.calls[0][1];
    expect(importRequest.idempotencyKey).toBeTruthy();

    fireEvent.click(screen.getByRole('checkbox', { name: 'Agent One' }));
    fireEvent.click(screen.getByRole('button', { name: 'Assign to selected agents' }));
    await screen.findByText(/saved this assignment in configuration revision 5/);
    expect(actions.updateSkillAssignment).toHaveBeenCalledWith('project-1', 'skill-1', {
      expectedProjectConfigurationRevision: 4,
      revision: 3,
      contentDigest: 'sha256:sample',
      enabled: true,
      order: 0,
      agentIds: ['agent-1'],
    });
    expect(screen.getByText(importedSkill.skillId)).toBeTruthy();
    expect(screen.getByText(/does not prove a runtime loaded it/)).toBeTruthy();
  });

  it('shows source owner errors instead of replacing them with success-shaped state', async () => {
    const actions = createActions({
      listSources: vi.fn().mockRejectedValue(new Error('source owner denied this request')),
    });
    renderPanel(actions);

    expect((await screen.findByRole('alert')).textContent).toContain('source owner denied this request');
  });
});
