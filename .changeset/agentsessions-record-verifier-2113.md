---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": minor
---

Add RFC 8785 canonical JSON and an independent verifier for AgentSessions record exports, with a Go reference exporter and fixture. The verifier rejects stored event bytes that contain unknown protobuf fields.