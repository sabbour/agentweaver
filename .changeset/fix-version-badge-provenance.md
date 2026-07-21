---
"agentweaver": patch
---

Fix the "Alpha vX.Y.Z" badge (top-left of the app shell) always showing the last `VERSION`-file bump, even when the running deployment was produced by `azure:upgrade`/`azure:deploy-from-local` (which tag images by git SHA and never touch `VERSION`) — making the badge completely uninformative about what's actually running.

Root cause: `AppVersionProvider` only ever read the static `VERSION` file baked into the image, and while every Dockerfile already declares `ARG IMAGE_TAG`/`ARG GIT_SHA` (passed by `scripts/azure/image-spec.mjs` for every `az acr build`), those were only ever set as OCI `LABEL`s (image metadata), never as container `ENV` vars, so the running .NET process had no way to read them.

Fixed by:
- Adding `ENV IMAGE_TAG=${IMAGE_TAG}` / `ENV GIT_SHA=${GIT_SHA}` right after the existing `ARG`/`LABEL` declarations in all four Dockerfiles (API, MCP, web, AgentHost), so the build provenance is readable at runtime via `Environment.GetEnvironmentVariable`.
- `AppVersionProvider` now prefers these runtime env vars: when `IMAGE_TAG` looks like a real semver release tag (`^v?\d+\.\d+\.\d+$`), it's a real `azure:release` build and that tag is the authoritative version. Otherwise (local `dotnet run`, or a git-SHA-tagged `azure:upgrade`/`azure:deploy-from-local` build) it falls back to the `VERSION` file for the base semver and surfaces the git SHA separately.
- `GET /api/version` now returns `{ version, gitSha, isRelease }` instead of a single opaque string.
- The frontend badge now reads: `Alpha v0.9.70` for a real release, `Alpha v0.9.71-dev+a1c11f1` for a SHA-tagged local/upgrade build — clearly distinguishing the two instead of showing the same stale string for both.
