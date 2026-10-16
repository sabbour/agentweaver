/** Browser event dispatched when a request requires model-provider setup. */
export const MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT = 'agentweaver:model-provider-connection-required';
/** Server code identifying the standard model-provider setup response. */
export const MODEL_PROVIDER_CONNECTION_REQUIRED_CODE = 'model_provider_connection_required';
/** Fallback message used when presenting the setup requirement. */
export const MODEL_PROVIDER_CONNECTION_REQUIRED_MESSAGE =
  'Set up a model provider to continue.';
/** Project-scoped provider setup action discriminator. */
export const CONFIGURE_PROJECT_MODEL_PROVIDER_ACTION = 'configure_project_model_provider';
/** Deployment-default provider setup action discriminator. */
export const CONFIGURE_PLATFORM_MODEL_PROVIDER_ACTION = 'configure_platform_model_provider';
/** User-level provider setup action discriminator. */
export const CONFIGURE_USER_MODEL_PROVIDER_ACTION = 'configure_user_model_provider';

/** Structured server response describing which model-provider setup action is required. */
export interface ModelProviderConnectionRequirement {
  code: string;
  message: string;
  action: {
    type: typeof CONFIGURE_PROJECT_MODEL_PROVIDER_ACTION
      | typeof CONFIGURE_PLATFORM_MODEL_PROVIDER_ACTION
      | typeof CONFIGURE_USER_MODEL_PROVIDER_ACTION;
    project_id: string;
  };
}

/** Narrows an unknown response to the supported model-provider requirement shape. */
export function isModelProviderConnectionRequirement(value: unknown): value is ModelProviderConnectionRequirement {
  if (typeof value !== 'object' || value === null) return false;
  const record = value as Record<string, unknown>;
  const action = record.action;
  if (typeof record.code !== 'string' || typeof record.message !== 'string') return false;
  if (typeof action !== 'object' || action === null) return false;
  const actionType = (action as Record<string, unknown>).type;
  return (actionType === CONFIGURE_PROJECT_MODEL_PROVIDER_ACTION
    || actionType === CONFIGURE_PLATFORM_MODEL_PROVIDER_ACTION
    || actionType === CONFIGURE_USER_MODEL_PROVIDER_ACTION)
    && typeof (action as Record<string, unknown>).project_id === 'string';
}

/** True when a requirement is scoped to a specific project, rather than the platform default. */
export function isProjectScopedModelProviderRequirement(requirement: ModelProviderConnectionRequirement): boolean {
  return requirement.action.type === CONFIGURE_PROJECT_MODEL_PROVIDER_ACTION;
}

/** True when the requested setup action configures the signed-in user's provider. */
export function isUserScopedModelProviderRequirement(requirement: ModelProviderConnectionRequirement): boolean {
  return requirement.action.type === CONFIGURE_USER_MODEL_PROVIDER_ACTION;
}
