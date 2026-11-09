---
---

Issue #2604 adds the `full-flow-delivery` journey to native CLI acceptance.
The journey needs a BYOK model binding, a hard run budget of USD 5 or less,
and a time limit of 30 minutes or less. It generates and confirms casting,
creates a backlog task, and reads the run usage. A new critical-path coverage
check fails pull request CI when a Gateway operation is added on the path, or
a route arrives for a known gap, without a matching journey or manifest
update. This is version-neutral validation tooling; no product component
version changes.
