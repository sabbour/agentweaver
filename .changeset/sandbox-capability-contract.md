---
"agentweaver": patch
---

Give sandboxed runs a real developer toolchain and publish what the sandbox can actually do. A run
that needs system packages now gets a per-run writable system root — `/usr` and `/var` overlaid onto
a size-bounded tmpfs inside its own user namespace, `/etc` copied — so `apt-get install` works
without adding a single pod privilege, and everything installed is discarded with the run and is
invisible to other runs, to AgentHost, and to the node. The executor also answers a new
`capabilities` request describing every supported workload (npm, NuGet, apt, preview port binding)
and, for the ones it cannot perform, why and what would change that: container image builds require
the external BuildKit broker, and `winget` is reported as unsupported on Linux with the Windows
executor backend named as the remediation rather than being silently omitted.

Image builds are now actually available where they are wanted. `k8s/optional/buildkit-broker.yaml`
adds an opt-in BuildKit broker that runs unprivileged, under `RuntimeDefault` seccomp, in its own
Kata VM, and the sandbox reaches it over mutual TLS through the `awx-docker` shim — so a run can
`docker build` without the sandbox pod gaining a single privilege. The broker is shared across runs,
which its documentation states plainly: unlike the sandbox itself, it does not preserve the per-run
boundary, and that is why it is off by default.
