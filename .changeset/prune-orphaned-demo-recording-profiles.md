---
"agentweaver": patch
---

Prevent unbounded disk growth from the demo recording harness: `scripts/demo-recording/.auth/edge-default-automation.refresh-*` temporary Edge profile copies were only cleaned up on the success/failure path of the process that created them, so a killed or interrupted `demo-recording signin` (e.g. closing the terminal) left a full copy of the Edge profile behind forever. The next `signin` now prunes any leftover refresh-temp copies before starting.
