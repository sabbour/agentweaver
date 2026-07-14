# MCP harness

MCP-protocol-driven validation for Agentweaver. `tools/list` is discovered live at
session start and is the sole persona action menu. The independent
`required-capabilities.json` contract is a smoke/acceptance regression tripwire.

Run unit tests with `npm test` from this directory. Run a smoke session with:

```powershell
npm run smoke -- --target stdio --server-command dotnet --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' --project-id <id>
```

Only localhost and staging targets are accepted by default. Production requires both
`--allow-prod` and `--i-understand-prod`.
