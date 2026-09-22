import { randomUUID } from 'node:crypto';

import { capture as defaultCapture, run as defaultRun } from './exec.mjs';

export const CONTEXT_BUDGET_ENV = ['MemoryContext__MaxItems', 'MemoryContext__MaxTokens'];
export const CONTEXT_BUDGET_DEPLOYMENTS = [
  { deployment: 'agentweaver-api', container: 'api' },
  { deployment: 'agentweaver-worker', container: 'worker' },
];

const PROFILE_ANNOTATION = 'agentweaver.io/context-budget-profile';
const SNAPSHOT_ANNOTATION = 'agentweaver.io/context-budget-snapshot';
const ENVIRONMENT_LABEL = 'agentweaver.io/environment';
const LOCK_NAME = 'agentweaver-context-budget-harness';

function abortError(signal) {
  return signal?.reason instanceof Error ? signal.reason : new Error('context-budget profile cancelled');
}

function throwIfAborted(signal) {
  if (signal?.aborted) throw abortError(signal);
}

function pointer(value) {
  return value.replaceAll('~', '~0').replaceAll('/', '~1');
}

function clone(value) {
  return value === undefined ? undefined : structuredClone(value);
}

function selectedState(deployment, containerName) {
  const containers = deployment?.spec?.template?.spec?.containers;
  const containerIndex = containers?.findIndex((container) => container.name === containerName) ?? -1;
  if (containerIndex < 0) {
    throw new Error(`Deployment ${deployment?.metadata?.name ?? '<unknown>'} has no container named ${containerName}.`);
  }
  const env = containers[containerIndex].env ?? [];
  const variables = Object.fromEntries(
    CONTEXT_BUDGET_ENV.map((name) => [name, clone(env.find((entry) => entry.name === name)) ?? null]),
  );
  const annotation = deployment.spec.template.metadata?.annotations?.[PROFILE_ANNOTATION] ?? null;
  return {
    resourceVersion: deployment.metadata.resourceVersion,
    generation: Number(deployment.metadata.generation),
    containerIndex,
    env: clone(env),
    variables,
    annotation,
  };
}

function nextEnv(env, values) {
  const retained = env.filter((entry) => !CONTEXT_BUDGET_ENV.includes(entry.name));
  return [
    ...retained,
    { name: 'MemoryContext__MaxItems', value: String(values.maxItems) },
    { name: 'MemoryContext__MaxTokens', value: String(values.maxTokens) },
  ];
}

function restoredEnv(env, variables) {
  const retained = env.filter((entry) => !CONTEXT_BUDGET_ENV.includes(entry.name));
  for (const name of CONTEXT_BUDGET_ENV) {
    if (variables[name] !== null) retained.push(clone(variables[name]));
  }
  return retained;
}

function validateValues({ maxItems, maxTokens }) {
  if (!Number.isInteger(maxItems) || maxItems < 1 || maxItems > 20) {
    throw new Error('Context-budget profile maxItems must be an integer from 1 through 20.');
  }
  if (!Number.isInteger(maxTokens) || maxTokens < 64 || maxTokens > 4000) {
    throw new Error('Context-budget profile maxTokens must be an integer from 64 through 4000.');
  }
}

function validateTarget(target) {
  const url = new URL(target);
  if (url.protocol !== 'https:') {
    throw new Error('Context-budget profile requires an HTTPS target.');
  }
  return url;
}

function inContext(args, kubeContext) {
  return [...args, '--context', kubeContext];
}

async function kubectlJson(capture, args, kubeContext, signal) {
  throwIfAborted(signal);
  const result = await capture('kubectl', [...inContext(args, kubeContext), '--output', 'json'], { json: true, signal });
  throwIfAborted(signal);
  return result.json;
}

async function createLock(capture, namespace, kubeContext, owner, signal) {
  const lease = {
    apiVersion: 'coordination.k8s.io/v1',
    kind: 'Lease',
    metadata: { name: LOCK_NAME, namespace },
    spec: {
      holderIdentity: owner,
      leaseDurationSeconds: 1800,
      acquireTime: new Date().toISOString(),
    },
  };
  throwIfAborted(signal);
  try {
    const result = await capture('kubectl', [...inContext(['create', '-f', '-'], kubeContext), '--output', 'json'], {
      input: JSON.stringify(lease),
      json: true,
    });
    return {
      uid: result.json?.metadata?.uid,
      resourceVersion: result.json?.metadata?.resourceVersion,
    };
  } catch (error) {
    const existing = await kubectlJson(
      capture,
      ['get', 'lease', LOCK_NAME, '--namespace', namespace],
      kubeContext,
    ).catch(() => null);
    if (existing?.spec?.holderIdentity === owner) {
      return {
        uid: existing.metadata?.uid,
        resourceVersion: existing.metadata?.resourceVersion,
      };
    }
    throw new Error(`Context-budget profile is already locked or cannot acquire its Lease: ${error.message}`);
  }
}

async function releaseLock(capture, namespace, kubeContext, owner, identity) {
  const lease = await kubectlJson(capture, ['get', 'lease', LOCK_NAME, '--namespace', namespace], kubeContext);
  if (lease?.spec?.holderIdentity !== owner
    || lease?.metadata?.uid !== identity.uid
    || lease?.metadata?.resourceVersion !== identity.resourceVersion) {
    throw new Error('Context-budget profile Lease ownership changed; refusing to delete another owner\'s lock.');
  }
  const path = `/apis/coordination.k8s.io/v1/namespaces/${encodeURIComponent(namespace)}/leases/${LOCK_NAME}`;
  await capture('kubectl', inContext(['delete', `--raw=${path}`, '-f', '-'], kubeContext), {
    input: JSON.stringify({
      apiVersion: 'v1',
      kind: 'DeleteOptions',
      preconditions: {
        uid: identity.uid,
        resourceVersion: identity.resourceVersion,
      },
    }),
  });
}

async function patchDeployment(
  capture,
  namespace,
  kubeContext,
  target,
  env,
  annotation,
  { signal, requireOwner, expectedEnv } = {},
) {
  const annotationPath = `/spec/template/metadata/annotations/${pointer(PROFILE_ANNOTATION)}`;
  const annotationExists = target.annotation !== null;
  const patch = [
    { op: 'test', path: '/metadata/resourceVersion', value: target.resourceVersion },
    ...(requireOwner ? [
      { op: 'test', path: annotationPath, value: requireOwner },
      { op: 'test', path: `/spec/template/spec/containers/${target.containerIndex}/env`, value: expectedEnv },
    ] : []),
    { op: 'replace', path: `/spec/template/spec/containers/${target.containerIndex}/env`, value: env },
    annotation === null
      ? { op: 'remove', path: annotationPath }
      : { op: annotationExists ? 'replace' : 'add', path: annotationPath, value: annotation },
  ];
  throwIfAborted(signal);
  await capture(
    'kubectl',
    inContext(
      ['patch', 'deployment', target.deployment, '--namespace', namespace, '--type=json', '--patch', JSON.stringify(patch)],
      kubeContext,
    ),
    {},
  );
}

export const contextBudgetProfileInternals = {
  nextEnv,
  restoredEnv,
  selectedState,
};

async function waitForRollout(run, namespace, kubeContext, deployment, signal) {
  throwIfAborted(signal);
  await run(
    'kubectl',
    inContext(
      ['rollout', 'status', `deployment/${deployment}`, '--namespace', namespace, '--timeout=300s'],
      kubeContext,
    ),
    { signal },
  );
  throwIfAborted(signal);
}

function equalVariables(actual, expected) {
  return CONTEXT_BUDGET_ENV.every((name) =>
    JSON.stringify(actual.variables[name]) === JSON.stringify(expected.variables[name]));
}

function hasAppliedProfile(state, owner, maxItems, maxTokens) {
  return state.annotation === owner
    && state.variables.MemoryContext__MaxItems?.value === String(maxItems)
    && state.variables.MemoryContext__MaxTokens?.value === String(maxTokens);
}

export async function withContextBudgetProfile(options, action) {
  const {
    target,
    namespace,
    kubeContext,
    confirmNonProduction,
    nonProductionVerified,
    maxItems,
    maxTokens,
    signal,
    capture = defaultCapture,
    run = defaultRun,
    owner = randomUUID(),
  } = options;
  validateValues({ maxItems, maxTokens });
  const targetUrl = validateTarget(target);
  if (confirmNonProduction !== targetUrl.origin) {
    throw new Error(`Pass --confirm-non-production ${targetUrl.origin} to acknowledge the staging mutation.`);
  }
  if (!nonProductionVerified) {
    throw new Error('The target must report isRelease=false before the context-budget profile can mutate deployments.');
  }
  if (!namespace || !kubeContext) throw new Error('An explicit Kubernetes context and namespace are required.');

  const currentContext = (await capture('kubectl', ['config', 'current-context'], { signal })).stdout.trim();
  if (currentContext !== kubeContext) {
    throw new Error(`Active Kubernetes context "${currentContext}" does not match requested context "${kubeContext}".`);
  }
  const kubeConfig = await kubectlJson(capture, ['config', 'view', '--minify'], kubeContext, signal);
  const currentNamespace = kubeConfig?.contexts?.[0]?.context?.namespace ?? 'default';
  if (currentNamespace !== namespace) {
    throw new Error(
      `Active Kubernetes namespace "${currentNamespace}" does not match requested namespace "${namespace}".`,
    );
  }
  const namespaceResource = await kubectlJson(capture, ['get', 'namespace', namespace], kubeContext, signal);
  if (namespaceResource?.metadata?.labels?.[ENVIRONMENT_LABEL] !== 'staging') {
    throw new Error(
      `Namespace ${namespace} must be independently labeled ${ENVIRONMENT_LABEL}=staging.`,
    );
  }
  const route = await kubectlJson(
    capture,
    ['get', 'httproute', 'agentweaver-api-route', '--namespace', namespace],
    kubeContext,
    signal,
  );
  if (!(route?.spec?.hostnames ?? []).includes(targetUrl.hostname)) {
    throw new Error(`Target host ${targetUrl.hostname} is not served by ${namespace}/agentweaver-api-route.`);
  }

  let locked = false;
  let lockIdentity = null;
  let snapshots = null;
  const patchedDeployments = new Set();
  let primaryError = null;
  try {
    lockIdentity = await createLock(capture, namespace, kubeContext, owner, signal);
    if (!lockIdentity.uid || !lockIdentity.resourceVersion) {
      throw new Error('Context-budget profile Lease creation did not return UID/resourceVersion identity.');
    }
    locked = true;
    throwIfAborted(signal);
    snapshots = {};
    for (const targetDeployment of CONTEXT_BUDGET_DEPLOYMENTS) {
      const deployment = await kubectlJson(
        capture,
        ['get', 'deployment', targetDeployment.deployment, '--namespace', namespace],
        kubeContext,
        signal,
      );
      snapshots[targetDeployment.deployment] = {
        ...selectedState(deployment, targetDeployment.container),
        ...targetDeployment,
      };
    }
    throwIfAborted(signal);
    const encodedSnapshot = Buffer.from(JSON.stringify(snapshots)).toString('base64url');
    const leasePatch = [
      { op: 'test', path: '/metadata/resourceVersion', value: lockIdentity.resourceVersion },
      { op: 'test', path: '/spec/holderIdentity', value: owner },
      {
        op: 'add',
        path: `/metadata/annotations/${pointer(SNAPSHOT_ANNOTATION)}`,
        value: encodedSnapshot,
      },
    ];
    let patchedLease;
    try {
      patchedLease = (await capture(
        'kubectl',
        inContext([
          'patch', 'lease', LOCK_NAME, '--namespace', namespace, '--type=json', '--patch',
          JSON.stringify(leasePatch), '--output', 'json',
        ], kubeContext),
        { json: true },
      )).json;
    } catch (error) {
      const reconciled = await kubectlJson(
        capture,
        ['get', 'lease', LOCK_NAME, '--namespace', namespace],
        kubeContext,
      ).catch(() => null);
      if (reconciled?.metadata?.uid !== lockIdentity.uid
        || reconciled?.spec?.holderIdentity !== owner
        || reconciled?.metadata?.annotations?.[SNAPSHOT_ANNOTATION] !== encodedSnapshot) {
        throw error;
      }
      patchedLease = reconciled;
    }
    lockIdentity.resourceVersion = patchedLease?.metadata?.resourceVersion;
    if (!lockIdentity.resourceVersion) {
      throw new Error('Context-budget profile Lease snapshot patch did not return a resourceVersion.');
    }
    throwIfAborted(signal);

    for (const targetDeployment of CONTEXT_BUDGET_DEPLOYMENTS) {
      const snapshot = snapshots[targetDeployment.deployment];
      try {
        await patchDeployment(
          capture,
          namespace,
          kubeContext,
          snapshot,
          nextEnv(snapshot.env, { maxItems, maxTokens }),
          owner,
          { signal },
        );
        patchedDeployments.add(targetDeployment.deployment);
      } catch (error) {
        const current = selectedState(
          await kubectlJson(
            capture,
            ['get', 'deployment', targetDeployment.deployment, '--namespace', namespace],
            kubeContext,
          ),
          targetDeployment.container,
        );
        if (hasAppliedProfile(current, owner, maxItems, maxTokens)) {
          patchedDeployments.add(targetDeployment.deployment);
        }
        throw error;
      }
      throwIfAborted(signal);
    }
    for (const targetDeployment of CONTEXT_BUDGET_DEPLOYMENTS) {
      await waitForRollout(run, namespace, kubeContext, targetDeployment.deployment, signal);
    }

    const applied = {};
    for (const targetDeployment of CONTEXT_BUDGET_DEPLOYMENTS) {
      const current = selectedState(
        await kubectlJson(
          capture,
          ['get', 'deployment', targetDeployment.deployment, '--namespace', namespace],
          kubeContext,
          signal,
        ),
        targetDeployment.container,
      );
      const previous = snapshots[targetDeployment.deployment];
      if (current.generation <= previous.generation) {
        throw new Error(`${targetDeployment.deployment} generation did not increase.`);
      }
      if (current.variables.MemoryContext__MaxItems?.value !== String(maxItems)
        || current.variables.MemoryContext__MaxTokens?.value !== String(maxTokens)
        || current.annotation !== owner) {
        throw new Error(`${targetDeployment.deployment} context-budget readback did not match requested values.`);
      }
      applied[targetDeployment.deployment] = current.variables;
    }
    return await action({ owner, applied, signal });
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    const cleanupErrors = [];
    if (snapshots) {
      for (const targetDeployment of CONTEXT_BUDGET_DEPLOYMENTS) {
        if (!patchedDeployments.has(targetDeployment.deployment)) continue;
        const snapshot = snapshots[targetDeployment.deployment];
        try {
          const current = selectedState(
            await kubectlJson(
              capture,
              ['get', 'deployment', targetDeployment.deployment, '--namespace', namespace],
              kubeContext,
            ),
            targetDeployment.container,
          );
          if (!hasAppliedProfile(current, owner, maxItems, maxTokens)) {
            throw new Error('profile ownership or applied context-budget values changed; refusing to overwrite');
          }
          await patchDeployment(
            capture,
            namespace,
            kubeContext,
            { ...current, deployment: targetDeployment.deployment },
            restoredEnv(current.env, snapshot.variables),
            snapshot.annotation,
            {
              requireOwner: owner,
              expectedEnv: current.env,
            },
          );
        } catch (error) {
          cleanupErrors.push(`${targetDeployment.deployment} restore: ${error.message}`);
        }
      }
      for (const targetDeployment of CONTEXT_BUDGET_DEPLOYMENTS) {
        if (!patchedDeployments.has(targetDeployment.deployment)) continue;
        try {
          await waitForRollout(run, namespace, kubeContext, targetDeployment.deployment);
          const restored = selectedState(
            await kubectlJson(
              capture,
              ['get', 'deployment', targetDeployment.deployment, '--namespace', namespace],
              kubeContext,
            ),
            targetDeployment.container,
          );
          const snapshot = snapshots[targetDeployment.deployment];
          if (!equalVariables(restored, snapshot) || restored.annotation !== snapshot.annotation) {
            throw new Error('restored environment does not structurally match its snapshot');
          }
        } catch (error) {
          cleanupErrors.push(`${targetDeployment.deployment} verification: ${error.message}`);
        }
      }
    }
    if (locked && cleanupErrors.length === 0) {
      try {
        await releaseLock(capture, namespace, kubeContext, owner, lockIdentity);
      } catch (error) {
        cleanupErrors.push(`Lease cleanup: ${error.message}`);
      }
    } else if (locked) {
      cleanupErrors.push(`Lease retained for recovery: ${namespace}/${LOCK_NAME}`);
    }
    if (cleanupErrors.length > 0) {
      if (primaryError) {
        primaryError.cleanupErrors = [...(primaryError.cleanupErrors ?? []), ...cleanupErrors];
      }
      else throw new AggregateError(cleanupErrors.map((message) => new Error(message)), 'Context-budget profile cleanup failed.');
    }
  }
}
