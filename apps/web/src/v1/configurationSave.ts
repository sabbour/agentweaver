import type { UpdateProjectConfigurationResponse } from './contracts';

export function retainNewerConfigurationSave(
  current: { scope: string; response: UpdateProjectConfigurationResponse } | null,
  scope: string,
  response: UpdateProjectConfigurationResponse,
) {
  return !current || current.scope !== scope || response.revision > current.response.revision
    ? { scope, response }
    : current;
}
