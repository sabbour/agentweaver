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
    previewMarketplaceSkill: vi.fn().mockResolvedValue(preview),
    importMarketplaceSkill: vi.fn().mockResolvedValue(importedSkill),
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

  it('previews and imports a selected source skill at the pinned commit', async () => {
    const actions = createActions({
      listSources: vi.fn().mockResolvedValue([source]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
      previewMarketplaceSkill: vi.fn().mockResolvedValue(preview),
      importMarketplaceSkill: vi.fn().mockResolvedValue(importedSkill),
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
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await screen.findByLabelText('Marketplace skill preview');
    expect(actions.previewMarketplaceSkill).toHaveBeenCalledWith('project-1', 'source-1', {
      expectedSourceRevision: 7,
      resolvedCommitSha: browsePage.resolvedCommitSha,
      selectedPath: 'skills/sample',
    });

    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));
    await screen.findByText(/The Skills owner imported Sample skill at revision 3/);
    const request = vi.mocked(actions.importMarketplaceSkill).mock.calls[0][2];
    expect(request).toEqual({
      expectedSourceRevision: 7,
      resolvedCommitSha: browsePage.resolvedCommitSha,
      selectedPath: 'skills/sample',
      idempotencyKey: expect.any(String),
      expectedContentDigest: preview.contentDigest,
    });
    expect(request).not.toHaveProperty('candidate');
    expect((screen.getByRole('button', { name: 'Source skill imported' }) as HTMLButtonElement).disabled)
      .toBe(true);
  });

  it('reuses a source import key after failure and ignores a late preview after search changes', async () => {
    let resolvePreview!: (value: SkillContentPreview) => void;
    const pendingPreview = new Promise<SkillContentPreview>((resolve) => {
      resolvePreview = resolve;
    });
    const actions = createActions({
      listSources: vi.fn().mockResolvedValue([source]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
      previewMarketplaceSkill: vi.fn().mockResolvedValue(preview),
      importMarketplaceSkill: vi.fn()
        .mockRejectedValueOnce(new Error('temporary import failure'))
        .mockRejectedValueOnce(new Error('retry import failure'))
        .mockResolvedValueOnce(importedSkill),
    });
    renderPanel(actions);

    await screen.findByText('example/skills · main · revision 7 · active');
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await screen.findByLabelText('Marketplace skill preview');
    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));
    await screen.findByText('temporary import failure');
    const firstKey = vi.mocked(actions.importMarketplaceSkill).mock.calls[0][2].idempotencyKey;

    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));
    await screen.findByText('retry import failure');
    expect(vi.mocked(actions.importMarketplaceSkill).mock.calls[1][2].idempotencyKey).toBe(firstKey);

    fireEvent.change(screen.getByLabelText('Search skills'), { target: { value: 'different' } });
    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await screen.findByLabelText('Marketplace skill preview');
    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));
    await screen.findByText(/The Skills owner imported Sample skill at revision 3/);
    const nextKey = vi.mocked(actions.importMarketplaceSkill).mock.calls[2][2].idempotencyKey;
    expect(nextKey).not.toBe(firstKey);

    vi.mocked(actions.previewMarketplaceSkill).mockReturnValueOnce(pendingPreview);
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await waitFor(() => expect(actions.previewMarketplaceSkill).toHaveBeenCalledTimes(3));
    fireEvent.change(screen.getByLabelText('Search skills'), { target: { value: 'third query' } });
    await act(async () => {
      resolvePreview({ ...preview, name: 'Late source skill' });
      await pendingPreview;
    });
    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Import selected source skill' })).toBeNull();
  });

  it('ignores a pending source preview when refreshing to a new source revision', async () => {
    let resolvePreview!: (value: SkillContentPreview) => void;
    const pendingPreview = new Promise<SkillContentPreview>((resolve) => {
      resolvePreview = resolve;
    });
    const refreshedSource = { ...source, revision: 8 };
    const actions = createActions({
      listSources: vi.fn()
        .mockResolvedValueOnce([source])
        .mockResolvedValueOnce([refreshedSource]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
      previewMarketplaceSkill: vi.fn().mockReturnValue(pendingPreview),
    });
    renderPanel(actions);

    await screen.findByText('example/skills · main · revision 7 · active');
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await waitFor(() => expect(actions.previewMarketplaceSkill).toHaveBeenCalledTimes(1));

    const refreshButton = screen.getByRole('button', { name: 'Refresh sources' }) as HTMLButtonElement;
    expect(refreshButton.disabled).toBe(false);
    fireEvent.click(refreshButton);
    await screen.findByText('example/skills · main · revision 8 · active');
    await act(async () => {
      resolvePreview(preview);
      await pendingPreview;
    });

    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    expect(screen.queryByText(/The Skills owner validated this source skill/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Import selected source skill' })).toBeNull();
  });

  it('resets the source import retry key when actions reload the source binding', async () => {
    const actions = createActions({
      listSources: vi.fn().mockResolvedValue([source]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
      importMarketplaceSkill: vi.fn().mockRejectedValueOnce(new Error('temporary import failure')),
    });
    const rendered = renderPanel(actions);

    await screen.findByText('example/skills · main · revision 7 · active');
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await screen.findByLabelText('Marketplace skill preview');
    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));
    await screen.findByText('temporary import failure');
    const firstKey = vi.mocked(actions.importMarketplaceSkill).mock.calls[0][2].idempotencyKey;

    const refreshedSource = { ...source, revision: 8 };
    const refreshedPage = {
      ...browsePage,
      sourceRevision: refreshedSource.revision,
      resolvedCommitSha: 'fedcba9876543210',
    };
    const refreshedActions = createActions({
      listSources: vi.fn().mockResolvedValue([refreshedSource]),
      browseSource: vi.fn().mockResolvedValue(refreshedPage),
    });
    rendered.rerender(
      <SkillsMarketplacePanel
        projectId="project-1"
        configurationRevision={4}
        assignments={[]}
        agents={[{ agentId: 'agent-1', displayName: 'Agent One' }]}
        actions={refreshedActions}
      />,
    );

    await screen.findByText('example/skills · main · revision 8 · active');
    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('fedcba9876543210');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await screen.findByLabelText('Marketplace skill preview');
    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));
    await screen.findByText(/The Skills owner imported Sample skill at revision 3/);

    const nextKey = vi.mocked(refreshedActions.importMarketplaceSkill).mock.calls[0][2].idempotencyKey;
    expect(nextKey).not.toBe(firstKey);
  });

  it('ignores a late preview while actions reload the same source revision', async () => {
    let resolvePreview!: (value: SkillContentPreview) => void;
    let resolveSources!: (value: MarketplaceSource[]) => void;
    const pendingPreview = new Promise<SkillContentPreview>((resolve) => {
      resolvePreview = resolve;
    });
    const pendingSources = new Promise<MarketplaceSource[]>((resolve) => {
      resolveSources = resolve;
    });
    const actions = createActions({
      listSources: vi.fn().mockResolvedValue([source]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
      previewMarketplaceSkill: vi.fn().mockReturnValue(pendingPreview),
    });
    const rendered = renderPanel(actions);

    await screen.findByText('example/skills · main · revision 7 · active');
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await waitFor(() => expect(actions.previewMarketplaceSkill).toHaveBeenCalledTimes(1));

    const reloadedActions = createActions({ listSources: vi.fn().mockReturnValue(pendingSources) });
    rendered.rerender(
      <SkillsMarketplacePanel
        projectId="project-1"
        configurationRevision={4}
        assignments={[]}
        agents={[{ agentId: 'agent-1', displayName: 'Agent One' }]}
        actions={reloadedActions}
      />,
    );

    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    expect(screen.queryByText('0123456789abcdef')).toBeNull();
    expect(screen.getByText('Loading marketplace sources…')).toBeTruthy();
    await act(async () => {
      resolvePreview(preview);
      await pendingPreview;
    });
    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Import selected source skill' })).toBeNull();

    await act(async () => {
      resolveSources([source]);
      await pendingSources;
    });
    await screen.findByText('example/skills · main · revision 7 · active');
    expect(screen.queryByLabelText('Marketplace skill preview')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Import selected source skill' })).toBeNull();
  });

  it('does not accept a source import receipt that differs from its preview', async () => {
    const actions = createActions({
      listSources: vi.fn().mockResolvedValue([source]),
      browseSource: vi.fn().mockResolvedValue(browsePage),
      previewMarketplaceSkill: vi.fn().mockResolvedValue(preview),
      importMarketplaceSkill: vi.fn().mockResolvedValue({
        ...importedSkill,
        contentDigest: 'sha256:other-content',
      }),
    });
    renderPanel(actions);

    await screen.findByText('example/skills · main · revision 7 · active');
    fireEvent.click(screen.getByRole('button', { name: 'Browse skills' }));
    await screen.findByText('0123456789abcdef');
    fireEvent.click(screen.getByRole('button', { name: 'Preview Sample skill' }));
    await screen.findByLabelText('Marketplace skill preview');
    fireEvent.click(screen.getByRole('button', { name: 'Import selected source skill' }));

    await screen.findByText('The Skills owner returned an import receipt that does not match the preview.');
    expect(screen.queryByText(/The Skills owner imported Sample skill/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Assign to selected agents' })).toBeNull();
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
