import {
  hasDismissedProjectSetupPrompt,
  markProjectSetupPromptDismissed,
  projectSetupPromptFingerprint,
  projectSetupPromptStorageKey,
} from '../components/onboarding/projectSetupPromptStorage';
import { beforeEach, describe, expect, it } from 'vitest';
import type { ProjectSetupPromptState } from '../components/onboarding/projectSetupPromptStorage';

const optionalState: ProjectSetupPromptState = {
  projectId: 'project-1',
  userKey: 'user-1',
  origin: 'blank',
  sourceRepository: null,
  repositoryAccessRequired: false,
  repositoryAccessStatus: 'not_required',
  repositoryAccessReasonCode: 'not_required',
};

beforeEach(() => {
  localStorage.clear();
});

describe('project setup prompt storage', () => {
  it('invalidates an optional dismissal when repository access becomes required', () => {
    const storageKey = projectSetupPromptStorageKey(optionalState);
    const optionalFingerprint = projectSetupPromptFingerprint(optionalState);
    markProjectSetupPromptDismissed(storageKey, optionalFingerprint);

    const requiredFingerprint = projectSetupPromptFingerprint({
      ...optionalState,
      repositoryAccessRequired: true,
      repositoryAccessStatus: 'not_ready',
      repositoryAccessReasonCode: 'repo_app_installation_required',
    });

    expect(hasDismissedProjectSetupPrompt(storageKey, optionalFingerprint)).toBe(true);
    expect(hasDismissedProjectSetupPrompt(storageKey, requiredFingerprint)).toBe(false);
  });
});
