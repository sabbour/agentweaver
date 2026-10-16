import {
  type ModelProviderConnectionRequirement,
  isProjectScopedModelProviderRequirement,
  isUserScopedModelProviderRequirement,
} from '../api/modelProviderConnectionRequirement';
import { ProjectModelProviderSettings } from './ProjectModelProviderSettings';
import { Button } from '@fluentui/react-components';
import { useNavigate } from 'react-router-dom';
import { SetupReadiness } from './SetupReadiness';

export function ModelProviderRequiredAction({
  requirement,
  onDismiss,
}: {
  requirement: ModelProviderConnectionRequirement;
  onDismiss: () => void;
}) {
  const navigate = useNavigate();
  const isProjectScoped = isProjectScopedModelProviderRequirement(requirement);
  const isUserScoped = isUserScopedModelProviderRequirement(requirement);
  const projectId = requirement.action.project_id;
  return (
    <SetupReadiness
      compact
      onDismiss={onDismiss}
      model={{
        title: 'Model provider setup',
        description: requirement.message,
        items: [{
          id: 'model-provider',
          title: 'Model provider',
          description: isProjectScoped
            ? 'Choose the GitHub Copilot account for this project.'
            : isUserScoped
              ? 'Choose a provider for your personal session chat.'
              : 'A Platform Admin must choose the model provider for this deployment.',
          requirement: 'required',
          status: 'action-required',
        }],
      }}
      primaryAction={isProjectScoped ? (
        <ProjectModelProviderSettings projectId={projectId} triggerLabel="Set up model provider" />
      ) : isUserScoped ? (
        <Button appearance="primary" onClick={() => navigate('/settings')}>
          Open AI Access settings
        </Button>
      ) : (
        <Button appearance="primary" onClick={() => navigate('/platform-settings')}>
          Open Platform settings
        </Button>
      )}
    />
  );
}
