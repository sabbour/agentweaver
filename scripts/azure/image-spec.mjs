// image-spec.mjs -- ONE declarative description of every Agentweaver
// container image, consumed by BOTH steps/20-build-push-images.mjs (build)
// and steps/25-verify-image-provenance.mjs (provenance verification). This
// is the single source of truth the "Full Node port of deploy toolchain"
// decision (.squad/decisions/inbox/Squad-Coordinator-full-node-port-of-
// deploy-toolchain-drop-c3-c4-upgr.md) calls for: the legacy bash/PowerShell
// scripts duplicated the watched-path lists between 20 and 25, which is how
// they drifted out of sync and caused the #251 stale-image bug class in the
// first place. Do NOT duplicate this list anywhere else.
//
// Confirmed against source before porting (see scripts/aks/20-build-push-images.sh,
// _image-functions.ps1, 25-verify-image-provenance.sh, _provenance-functions.ps1,
// and apps/*/Dockerfile):
//   - Exactly 4 buildable images: agentweaver-api, agentweaver-frontend,
//     agentweaver-mcp, agentweaver-agent-host.
//   - Build uses `az acr build` (NOT docker buildx / NOT multi-arch).
//   - Bugfixes applied while porting (decision log):
//       1) 'NuGet.config' -> 'nuget.config': the real repo file
//          (verified: <repo-root>/nuget.config) is lowercase; the legacy
//          scripts' watched-path entry does not match it on a case-sensitive
//          filesystem, silently excluding it from staleness detection.
//       2) Added missing watched paths: 'VERSION' (drives IMAGE_TAG
//          derivation and is COPYed into the api runtime image -- see
//          apps/Agentweaver.Api/Dockerfile), and, for the api image
//          specifically, 'apps/Agentweaver.Api.Data' and
//          'apps/Agentweaver.Api.Migrations.Postgres' (both are COPYed into
//          apps/Agentweaver.Api/Dockerfile's build context but were never
//          watched, so a change to either project could silently ship a
//          stale retagged api image).
//       3) IMAGE_TAG/GIT_SHA are now passed as `az acr build --build-arg`
//          values (previously NOT passed at all -- verified: the legacy
//          `az acr build` invocation has no --build-arg flags). All 4
//          Dockerfiles already declare `ARG IMAGE_TAG=dev` / `ARG
//          GIT_SHA=unknown` (apps/Agentweaver.Api/Dockerfile,
//          apps/Agentweaver.Mcp/Dockerfile, apps/web/Dockerfile,
//          apps/Agentweaver.AgentHost/Dockerfile), so no Dockerfile change is
//          needed -- only the build invocation was missing the flags.

/**
 * Watched paths common to every .NET-based image (api, mcp, agent-host); the
 * frontend image does not build via the .sln and is watched separately.
 */
export const COMMON_DOTNET_PATHS = Object.freeze([
  "agentweaver.sln",
  "global.json",
  "Directory.Build.props",
  "Directory.Packages.props",
  "nuget.config",
  "packages",
  "VERSION",
]);

/**
 * The 4 buildable Agentweaver images. Field meanings:
 *   - name: ACR repository name (also the k8s Deployment name for api/frontend/mcp).
 *   - dockerfile: path relative to repo root.
 *   - context: build context relative to repo root (all images share the repo
 *     root because their Dockerfiles COPY from multiple subdirectories).
 *   - tagField: which resolved variables.mjs field supplies this image's tag
 *     ('IMAGE_TAG' for api/frontend/mcp, 'AGENTHOST_IMAGE_TAG' for agent-host).
 *   - watchedPaths: repo-relative paths whose diff between the previous
 *     image's source commit and the target commit decides build-vs-retag
 *     (20) and stale-vs-fresh (25). MUST stay a superset of every path each
 *     image's Dockerfile actually COPYs from.
 *   - currentTag: how to detect the tag currently live in the cluster, for
 *     20's build-vs-retag decision. `{ kind: 'deployment', name }` reads a
 *     Deployment's container image tag; `{ kind: 'agenthost' }` reads the
 *     agent-host SandboxTemplate's container image tag.
 *   - provenance: inputs for 25's live-pod verification. `deployment` (name
 *     to query desired replica count against, omitted for agent-host, which
 *     has no fixed replica count), `podSelector`, and `allowEphemeralPods`
 *     (true only for agent-host: claimed per-run sandbox pods can legitimately
 *     keep running an older image after a release ships, so provenance
 *     verification must tolerate zero/mixed Running pods there -- see
 *     Get-LiveDigestStateForSelector's #351 comment in _provenance-functions.ps1).
 *   - frontendBuild: true only for agentweaver-frontend, which requires a
 *     local `npm ci && npm run build` (producing apps/web/dist) before the
 *     `az acr build` context is tarred, and needs apps/web/node_modules
 *     temporarily moved out of the repo-root build context first.
 */
export const IMAGES = Object.freeze([
  Object.freeze({
    name: "agentweaver-api",
    dockerfile: "apps/Agentweaver.Api/Dockerfile",
    context: ".",
    tagField: "IMAGE_TAG",
    watchedPaths: Object.freeze([
      ...COMMON_DOTNET_PATHS,
      "apps/Agentweaver.Api",
      "apps/Agentweaver.Api.Data",
      "apps/Agentweaver.Api.Migrations.Postgres",
    ]),
    currentTag: Object.freeze({ kind: "deployment", name: "agentweaver-api" }),
    provenance: Object.freeze({
      deployment: "agentweaver-api",
      podSelector: "app=agentweaver-api",
      allowEphemeralPods: false,
    }),
    frontendBuild: false,
  }),
  Object.freeze({
    name: "agentweaver-frontend",
    dockerfile: "apps/web/Dockerfile",
    context: ".",
    tagField: "IMAGE_TAG",
    watchedPaths: Object.freeze(["apps/web", "apps/Agentweaver.Web"]),
    currentTag: Object.freeze({ kind: "deployment", name: "agentweaver-frontend" }),
    provenance: Object.freeze({
      deployment: "agentweaver-frontend",
      podSelector: "app=agentweaver-frontend",
      allowEphemeralPods: false,
    }),
    frontendBuild: true,
  }),
  Object.freeze({
    name: "agentweaver-mcp",
    dockerfile: "apps/Agentweaver.Mcp/Dockerfile",
    context: ".",
    tagField: "IMAGE_TAG",
    watchedPaths: Object.freeze([...COMMON_DOTNET_PATHS, "apps/Agentweaver.Mcp"]),
    currentTag: Object.freeze({ kind: "deployment", name: "agentweaver-mcp" }),
    provenance: Object.freeze({
      deployment: "agentweaver-mcp",
      podSelector: "app=agentweaver-mcp",
      allowEphemeralPods: false,
    }),
    frontendBuild: false,
  }),
  Object.freeze({
    name: "agentweaver-agent-host",
    dockerfile: "apps/Agentweaver.AgentHost/Dockerfile",
    context: ".",
    tagField: "AGENTHOST_IMAGE_TAG",
    watchedPaths: Object.freeze([...COMMON_DOTNET_PATHS, "apps/Agentweaver.AgentHost"]),
    currentTag: Object.freeze({ kind: "agenthost" }),
    provenance: Object.freeze({
      deployment: null,
      podSelector: "app=agentweaver-sandbox,app.kubernetes.io/component=agent-host",
      allowEphemeralPods: true,
    }),
    frontendBuild: false,
  }),
]);

/** Looks up a single image spec by ACR repository name; throws if unknown. */
export function getImage(name) {
  const image = IMAGES.find((i) => i.name === name);
  if (!image) throw new Error(`image-spec: unknown image '${name}'`);
  return image;
}

/**
 * Builds the `--build-arg NAME=VALUE` argv fragment for `az acr build`,
 * always including IMAGE_TAG and GIT_SHA (see the module header bugfix note
 * #3) so image labels/build metadata reflect the tag actually being shipped
 * and the exact commit it was built from.
 *
 * @param {string} imageTag The tag this image is being built/retagged as.
 * @param {string} gitSha The full (or short) commit SHA this build corresponds to.
 * @returns {string[]} argv fragment, e.g. ['--build-arg', 'IMAGE_TAG=v1.2.3', '--build-arg', 'GIT_SHA=abc1234'].
 */
export function buildArgsFor(imageTag, gitSha) {
  return ["--build-arg", `IMAGE_TAG=${imageTag}`, "--build-arg", `GIT_SHA=${gitSha}`];
}
