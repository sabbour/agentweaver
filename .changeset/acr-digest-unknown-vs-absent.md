---
"agentweaver": patch
---

Fix GHCR/custom image promotion resume behavior when an ACR final-tag digest read times out or fails transiently.

ACR repository digest reads now distinguish present, absent, and unknown states after retrying read-only failures. Image promotion no longer treats an unknown digest as an absent tag, honors operator `--force` intent even when the pre-read failed, and recovers from ACR `Conflict` responses by retrying with `--force` only after confirming the existing tag already matches the staged digest.
