### 2026-08-28: Fence provisional trigger tasks from contributor promotion
**By:** Tank
**What:** Trusted trigger tasks carry a persisted server-owned provisional marker until the bound invocation is committed; only a dedicated publication operation clears it while moving the task to Ready.
**Why:** Both individual and bulk contributor promotion operations exclude marked tasks, so neither can make an unbound invocation claimable while failed binding/publication cleanup can still delete the exact provisional task and release the occurrence for retry.
