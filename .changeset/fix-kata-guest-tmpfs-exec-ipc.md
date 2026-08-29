---
"agentweaver": patch
---

Restore sandbox pod execution on Kata. The AKS node-image upgrade on 2026-08-27 brought Kata 3.32.0, which flipped `disable_guest_empty_dir` to `true` and turned the executor IPC `emptyDir` into a per-container virtio-fs share, so the AgentHost↔executor Unix socket started failing every connection with `ECONNREFUSED` and AgentHost refused to start. Pinning that volume to `medium: Memory` keeps it on a guest-owned tmpfs, and the sidecar now fails at startup with the remediation instead of crash-looping silently.
