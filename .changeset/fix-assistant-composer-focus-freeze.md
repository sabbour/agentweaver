---
"agentweaver": patch
---

Fixed the Operator Assistant chat composer losing focus after every send and
feeling frozen while a message was in flight. Both were caused by the
composer's `disabled` prop being tied to the `busy` send state, which
disabled the textarea for the duration of the request — React blurs disabled
form elements (stealing focus) and made the whole composer unresponsive even
though the send itself is already optimistic (input clears immediately, a
pending message bubble shows). The textarea now stays enabled and focused
during a send; only the send affordance is gated via `disableSend`, so users
can keep typing their next message right away.
