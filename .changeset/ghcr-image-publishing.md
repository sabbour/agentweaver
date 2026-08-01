---
"agentweaver": minor
---

Publish Agentweaver container images to GitHub's container/artifact registry. A new `Publish images` workflow builds `agentweaver-api`, `agentweaver-frontend`, `agentweaver-mcp`, and `agentweaver-agent-host` and pushes them to `ghcr.io`, with tags that map to each stage of the `dev → release/vX.Y.Z → main` topology: `dev` pushes, release-candidate branches, `main`, published releases (`X.Y.Z`/`vX.Y.Z`/`latest`), and manual runs of an arbitrary commit. Every build also publishes an immutable `sha-<short>` tag. The build matrix is derived from the existing `image-spec.mjs` source of truth rather than restated in YAML.
