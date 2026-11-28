---
"Agentweaver.Projects.Config": minor
"Agentweaver.Gateway": minor
---

Add guarded `PUT /api/v1/policies/projects/{projectId}` (`writeProjectNativeAcsPolicy`) to create Skills-only revision 1 for a headless Project by atomically consuming its pinned baseline, with `NativeAcsPolicy:ProjectHeadWritesEnabled` disabled by default.
