import { AgentweaverApiClient } from '../client';
import { describe, expect, it } from 'vitest';
import type {
  CreateProjectRequest,
  CreateProjectRunRequest,
  Project,
  ProjectOrigin,
  ProjectRunSummary,
  ProjectState,
  UpdateProjectProviderSettingsRequest,
} from '../types';
// =============================================================================
// Type shape tests: verify that the TypeScript types are correctly defined
// and that the ApiClient exposes all expected project and GitHub auth methods.
// These tests verify compile-time shapes at runtime using object literal
// assignments that would fail type-check if shapes were wrong.
// =============================================================================

describe('Project type shapes', () => {
  it('Project interface has all required fields', () => {
    const p: Project = {
      project_id: 'test-id',
      name: 'My Project',
      origin: 'blank',
      source_repository: null,
      working_directory: '/path/to/project',
      default_branch: 'main',
      owner: 'test-user',
      default_provider: 'github-copilot',
      default_model_github_copilot: null,
      default_model_microsoft_foundry: null,
      blueprint_generation_model: null,
      workflow_generation_model: 'claude-sonnet-4.6',
      outcome_spec_generation_model: null,
      available: true,
      state: 'active',
      created_at: '2026-01-01T00:00:00Z',
      updated_at: '2026-01-01T00:00:00Z',
    };
    expect(p.project_id).toBe('test-id');
    expect(p.origin).toBe('blank');
    expect(p.state).toBe('active');
    expect(p.available).toBe(true);
  });

  it('ProjectOrigin type accepts blank and github', () => {
    const blank: ProjectOrigin = 'blank';
    const github: ProjectOrigin = 'github';
    expect(blank).toBe('blank');
    expect(github).toBe('github');
  });

  it('ProjectState type accepts active and deleting', () => {
    const active: ProjectState = 'active';
    const deleting: ProjectState = 'deleting';
    expect(active).toBe('active');
    expect(deleting).toBe('deleting');
  });

  it('CreateProjectRequest has required fields', () => {
    const req: CreateProjectRequest = {
      name: 'Test',
      origin: 'blank',
      working_directory: '/tmp/project',
    };
    expect(req.name).toBe('Test');
    expect(req.origin).toBe('blank');
  });

  it('CreateProjectRequest supports github origin with repository_selection_code', () => {
    const req: CreateProjectRequest = {
      name: 'GH Project',
      origin: 'github',
      repository_selection_code: 'opaque-selection-code',
      working_directory: '/tmp/gh-project',
    };
    expect(req.repository_selection_code).toBe('opaque-selection-code');
  });

  it('UpdateProjectProviderSettingsRequest is all optional', () => {
    const minimal: UpdateProjectProviderSettingsRequest = {};
    expect(minimal).toBeDefined();

    const full: UpdateProjectProviderSettingsRequest = {
      default_provider: 'microsoft-foundry',
      default_model_github_copilot: 'gpt-4o',
      default_model_microsoft_foundry: 'my-model',
      blueprint_generation_model: 'gpt-5.5',
      workflow_generation_model: null,
      outcome_spec_generation_model: 'claude-sonnet-4.6',
    };
    expect(full.default_provider).toBe('microsoft-foundry');
    expect(full.workflow_generation_model).toBeNull();
  });

  it('CreateProjectRunRequest has task as required field', () => {
    const req: CreateProjectRunRequest = { task: 'do something' };
    expect(req.task).toBe('do something');
  });

  it('CreateProjectRunRequest supports optional fields', () => {
    const req: CreateProjectRunRequest = {
      task: 'do something',
      model_source: 'github-copilot',
      model_id: 'gpt-4o',
      base_branch: 'feature/branch',
    };
    expect(req.model_source).toBe('github-copilot');
    expect(req.model_id).toBe('gpt-4o');
    expect(req.base_branch).toBe('feature/branch');
  });

  it('ProjectRunSummary has required fields', () => {
    const summary: ProjectRunSummary = {
      run_id: 'run-abc',
      status: 'in_progress',
      model_source: 'github-copilot',
      model_id: null,
      task: 'test task',
      started_at: '2026-01-01T00:00:00Z',
      ended_at: null,
    };
    expect(summary.run_id).toBe('run-abc');
    expect(summary.status).toBe('in_progress');
  });
});

describe('AgentweaverApiClient project methods', () => {
  it('client has listProjects method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.listProjects).toBe('function');
  });

  it('client has getProject method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.getProject).toBe('function');
  });

  it('client has createProject method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.createProject).toBe('function');
  });

  it('client has renameProject method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.renameProject).toBe('function');
  });

  it('client has updateProjectProviderSettings method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.updateProjectProviderSettings).toBe('function');
  });

  it('client has deleteProject method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.deleteProject).toBe('function');
  });

  it('client has startProjectRun method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.startProjectRun).toBe('function');
  });

  it('client has listProjectRuns method', () => {
    const client = new AgentweaverApiClient('http://localhost:5000', 'key');
    expect(typeof client.listProjectRuns).toBe('function');
  });
});
