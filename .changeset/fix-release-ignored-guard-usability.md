---
"agentweaver": patch
---

Fixed the release preparation ignored-file guard so it no longer rejects standard dependency/build/output directories (node_modules, dist, bin, obj, test output, harness artifacts), which had made `release:prepare`/`release:publish` unrunnable from a normal checkout, while still flagging unexpected ignored files in source/config locations.
