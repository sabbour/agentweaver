---
"agentweaver": patch
---

Bump `GitHub.Copilot.SDK` from 1.0.2 to 1.0.11, together with
`Microsoft.Agents.AI.GitHub.Copilot` from 1.11.1-rc1 to 1.19.0.

The two must move together: `GitHub.Copilot.SDK` became strong-named in 1.0.4
(`PublicKeyToken` went from `null` to `cc7b13ffcd2ddd51`), while
`Microsoft.Agents.AI.GitHub.Copilot` 1.11.1-rc1 was compiled against the
unsigned SDK and records an assembly reference of
`GitHub.Copilot.SDK, Version=1.0.0.0, PublicKeyToken=null`. A weakly-named
assembly reference can never bind to a strong-named definition, so bumping the
SDK on its own produced `error CS0012: The type 'CopilotClient' is defined in an
assembly that is not referenced` in `Agentweaver.AgentRuntime`. Version drift
alone was not the problem — SDK 1.0.3 still builds fine against the old adapter.

`Microsoft.Agents.AI.GitHub.Copilot` 1.19.0 is built against the signed SDK and
is the first stable (non-prerelease) line of the adapter, so this also moves the
package off an `-rc1` prerelease.
