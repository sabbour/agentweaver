import { deploymentOutputs, fixture, source } from './target.mjs';

export const postDeploymentAzure = {
  create: { status: 0, stderr: '', stdout: JSON.stringify({
    id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/agentweaver-${source.sha.slice(0, 12)}`,
    properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs },
  }) },
};
