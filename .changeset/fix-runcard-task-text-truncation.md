---
'agentweaver': patch
---

Fix the "Problems" panel (and other coordinator-run cards) on the project
board rendering the entire raw task prompt as the card title. Long,
multi-paragraph prompts previously rendered in full with no truncation,
making individual cards enormous and breaking the board's compact card
layout. The task text is now clamped to 3 lines with an ellipsis, and the
full text remains available via a native `title` tooltip on hover/focus.
