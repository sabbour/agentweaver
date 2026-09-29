// kubectl.mjs -- Thin, injectable kubectl query helpers shared by
// steps/20-build-push-images.mjs (current deployed tag detection) and
// steps/25-verify-image-provenance.mjs (live pod digest verification).
// Every export is a read-only query; none of them mutate cluster state.
// All accept an injectable `{ capture }` (matching az.mjs/git.mjs) so tests
// run without a real cluster.

import { capture as defaultCapture } from "./exec.mjs";

/**
 * Returns the image tag (portion after the last ':') currently set on a
 * Deployment's first container, or '' if kubectl/the deployment is
 * unavailable. Mirrors current_deployment_tag()/Get-CurrentDeploymentTag.
 */
export async function currentDeploymentTag(deployment, namespace, { capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture(
      "kubectl",
      [
        "get",
        "deployment",
        deployment,
        "--namespace",
        namespace,
        "--output",
        "jsonpath={.spec.template.spec.containers[0].image}",
      ],
      { allowFailure: true },
    );
    return tagFromImageRef(stdout.trim());
  } catch {
    return "";
  }
}

/**
 * Returns the image tag currently set on the agent-host SandboxTemplate, or
 * '' if unavailable. Mirrors current_agenthost_tag()/Get-CurrentAgentHostTag.
 */
export async function currentAgentHostTag(namespace, { capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture(
      "kubectl",
      [
        "get",
        "sandboxtemplate",
        "agentweaver-agent-host",
        "--namespace",
        namespace,
        "--output",
        "jsonpath={.spec.podTemplate.spec.containers[0].image}",
      ],
      { allowFailure: true },
    );
    return tagFromImageRef(stdout.trim());
  } catch {
    return "";
  }
}

function tagFromImageRef(imageRef) {
  if (!imageRef || !imageRef.includes(":")) return "";
  return imageRef.slice(imageRef.lastIndexOf(":") + 1);
}

/** Returns the desired replica count for a Deployment, or '' if unavailable. */
export async function desiredDeploymentReplicas(deployment, namespace, { capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture(
      "kubectl",
      ["get", "deployment", deployment, "--namespace", namespace, "--output", "jsonpath={.spec.replicas}"],
      { allowFailure: true },
    );
    return stdout.trim();
  } catch {
    return "";
  }
}

/**
 * Returns one row per matching pod: { name, phase, ready, imageRef, imageId,
 * deletionTimestamp }. `ready` is the Pod Ready condition, not merely the
 * first container's readiness; absent conditions remain not ready.
 */
export async function podStatusForSelector(selector, namespace, { capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture(
      "kubectl",
      [
        "get",
        "pods",
        "--namespace",
        namespace,
        "--selector",
        selector,
        "--output",
        'jsonpath={range .items[*]}{.metadata.name}{"\\t"}{.status.phase}{"\\t"}{.status.conditions[?(@.type=="Ready")].status}{"\\t"}{.status.containerStatuses[0].image}{"\\t"}{.status.containerStatuses[0].imageID}{"\\t"}{.metadata.deletionTimestamp}{"\\n"}{end}',
      ],
      { allowFailure: true },
    );
    return stdout
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean)
      .map((line) => {
        const [name, phase, ready, imageRef, imageId, deletionTimestamp] = line.split("\t");
        return { name, phase, ready: ready?.toLowerCase(), imageRef, imageId, deletionTimestamp: deletionTimestamp || "" };
      });
  } catch {
    return [];
  }
}
