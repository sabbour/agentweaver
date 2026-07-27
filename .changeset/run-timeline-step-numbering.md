---
'agentweaver': patch
---

Fix run timeline steps all rendering as "Step 1" instead of incrementing (Step 1, Step 2, Step 3...) by fixing an off-by-one in the continuation-narration collapse logic that let every size cap be overshot by one merge, allowing a whole run of small continuation-narrated steps ("Now let's...", "Next, I'll...") to fold into a single step.
