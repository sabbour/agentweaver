import { assembleReleasePlan } from "@changesets/assemble-release-plan";
import { readConfig } from "@changesets/config";
import { readPreState } from "@changesets/pre";
import { readChangesets } from "@changesets/read";
import { getPackages } from "@manypkg/get-packages";

export async function readNativeReleaseConfig(root, { reportWarnings = true } = {}) {
  const packages = await getPackages(root);
  const { config, warnings, errors } = await readConfig(packages.rootDir, packages);
  if (errors != null) {
    throw new Error(`Invalid Changesets configuration:\n- ${errors.join("\n- ")}`);
  }
  if (reportWarnings && warnings.length > 0) {
    console.warn(`Changesets configuration warnings:\n- ${warnings.join("\n- ")}`);
  }

  return { packages, config };
}

export async function createNativeReleasePlan(root) {
  const { packages, config } = await readNativeReleaseConfig(root);
  const [changesets, preState] = await Promise.all([
    readChangesets(packages.rootDir),
    readPreState(packages.rootDir),
  ]);
  if (changesets.length === 0 && (preState == null || preState.mode !== "exit")) {
    throw new Error("No unreleased changesets found.");
  }

  return {
    packages,
    config,
    releasePlan: assembleReleasePlan(changesets, packages, config, preState),
  };
}
