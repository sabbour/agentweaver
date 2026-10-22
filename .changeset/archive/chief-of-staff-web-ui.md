---
"Agentweaver.Web": patch
---

Add the Chief of Staff web experience. Overview is the Chief of Staff home with
Priorities, items that need input, New chat with a project picker, and My
conversations. New chat creates one run per draft, binds it as a conversation,
and resumes an interrupted start after a reload or sign-in redirect. Priorities
support create, edit, checklist, reorder, and picker-only run and conversation
links, and reread with a short message on revision or order conflicts. The
project Flow page groups Needs input, Open, Working, and Done from pending gate
notifications and the Gateway run list. The notification bell and Overview show
the same pending gates from the caller's attention inbox. The decision view
rereads a gate and sends nothing when its version changed. The run Chat tab
rebuilds user and assistant message text from the owner turn read after a
reload or reconnect and hides it when read access is lost. The rail's New
session button is now New chat.
