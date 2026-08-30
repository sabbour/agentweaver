---
"agentweaver": patch
---

Prune known-safe browser cache directories (Cache, Code Cache, BrowserMetrics, GrShaderCache,
etc.) from the demo-recording tool's persistent playwright-cli session profile after each
`close` command. Previously this profile (`scripts/demo-recording/.auth/sessions/<name>/`)
grew unbounded across recording sessions since nothing ever cleaned it up; a single session
had accumulated ~52MB of regenerable cache data.
