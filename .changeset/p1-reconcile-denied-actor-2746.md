---
---

Let P1 reconciliation and recovery cleanup accept a released `v1.0.16` API
manifest that contains the optional `fixturePlan.deniedActor` object. These
modes validate the object with the released apphost rule and then ignore it.
The UI path still rejects the object. The P1 harness is not a versioned
component in the release manifest, so this change does not bump a product
component version.
