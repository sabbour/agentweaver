---
"agentweaver": patch
---

Fix stale `k8s/` manifest paths in `KubernetesRemoteApiManifestTests` after the Kustomize `base`/`overlays` migration (#375). The test helper still pointed at the old flat `k8s/*.yaml` layout and was raising `FileNotFoundException` for every run against `dev`; it now resolves manifests under `k8s/base/`, matching the current directory structure.
