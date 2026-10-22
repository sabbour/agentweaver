---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": patch
"Agentweaver.Providers.Sessions.AgentSessions": minor
---

Add AgentSessions as one optional Sessions provider in the new `Agentweaver.Providers.Sessions.AgentSessions` package. The provider verifies AgentSessions record exports, rejects stored event bytes that contain unknown protobuf fields, and serves read-only replay through `ISessionsJournal`. The native PostgreSQL journal stays the default, and Events & Sessions has no reference to the provider. `Agentweaver.Abstractions` adds RFC 8785 canonical JSON and the provider-neutral `SessionsCapabilities` names, which move out of Events & Sessions.
