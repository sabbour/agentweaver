import { describe, it, expect } from 'vitest';
import type {
  Project,
  CreateProjectRequest,
  UpdateProjectProviderSettingsRequest,
  CreateProjectRunRequest,
  ProjectRunSummary,
  GitHubDeviceFlow,
  GitHubPollResult,
  GitHubAuthStatusResponse,
  GitHubAuthStatus,
  ProjectOrigin,
  ProjectState,
} from '../types';
import { ScaffolderApiClient } from '../client';

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

  it('CreateProjectRequest supports github origin with source_repository', () => {
    const req: CreateProjectRequest = {
      name: 'GH Project',
      origin: 'github',
      source_repository: 'owner/repo',
      working_directory: '/tmp/gh-project',
    };
    expect(req.source_repository).toBe('owner/repo');
  });

  it('UpdateProjectProviderSettingsRequest is all optional', () => {
    const minimal: UpdateProjectProviderSettingsRequest = {};
    expect(minimal).toBeDefined();

    const full: UpdateProjectProviderSettingsRequest = {
      default_provider: 'microsoft-foundry',
      default_model_github_copilot: 'gpt-4o',
      default_model_microsoft_foundry: 'my-model',
    };
    expect(full.default_provider).toBe('microsoft-foundry');
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

describe('GitHub auth type shapes', () => {
  it('GitHubAuthStatus accepts all valid values', () => {
    const signedIn: GitHubAuthStatus = 'signed_in';
    const signedOut: GitHubAuthStatus = 'signed_out';
    const neverSignedIn: GitHubAuthStatus = 'never_signed_in';
    expect(signedIn).toBe('signed_in');
    expect(signedOut).toBe('signed_out');
    expect(neverSignedIn).toBe('never_signed_in');
  });

  it('GitHubDeviceFlow has required fields', () => {
    const flow: GitHubDeviceFlow = {
      user_code: 'ABCD-1234',
      verification_uri: 'https://github.com/login/device',
      expires_in: 900,
      interval: 5,
    };
    expect(flow.user_code).toBe('ABCD-1234');
    expect(flow.verification_uri).toContain('github.com');
  });

  it('GitHubPollResult status accepts all expected values', () => {
    const pending: GitHubPollResult = { status: 'pending', login: null };
    const success: GitHubPollResult = { status: 'success', login: 'myuser' };
    const expired: GitHubPollResult = { status: 'expired', login: null };
    const denied: GitHubPollResult = { status: 'denied', login: null };
    expect(pending.status).toBe('pending');
    expect(success.login).toBe('myuser');
    expect(expired.status).toBe('expired');
    expect(denied.status).toBe('denied');
  });

  it('GitHubAuthStatusResponse has status and login', () => {
    const resp: GitHubAuthStatusResponse = {
      status: 'signed_in',
      login: 'mylogin',
    };
    expect(resp.status).toBe('signed_in');
    expect(resp.login).toBe('mylogin');
  });
});

describe('ScaffolderApiClient project methods', () => {
  it('client has listProjects method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.listProjects).toBe('function');
  });

  it('client has getProject method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.getProject).toBe('function');
  });

  it('client has createProject method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.createProject).toBe('function');
  });

  it('client has renameProject method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.renameProject).toBe('function');
  });

  it('client has updateProjectProviderSettings method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.updateProjectProviderSettings).toBe('function');
  });

  it('client has relinkProject method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.relinkProject).toBe('function');
  });

  it('client has deleteProject method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.deleteProject).toBe('function');
  });

  it('client has startProjectRun method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.startProjectRun).toBe('function');
  });

  it('client has listProjectRuns method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.listProjectRuns).toBe('function');
  });
});

describe('ScaffolderApiClient GitHub auth methods', () => {
  it('client has startGitHubDeviceFlow method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.startGitHubDeviceFlow).toBe('function');
  });

  it('client has pollGitHubAuth method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.pollGitHubAuth).toBe('function');
  });

  it('client has getGitHubAuthStatus method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.getGitHubAuthStatus).toBe('function');
  });

  it('client has signOutGitHub method', () => {
    const client = new ScaffolderApiClient('http://localhost:5000', 'key');
    expect(typeof client.signOutGitHub).toBe('function');
  });
});
