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
a builder sidecar, and `winget` is reported as unsupported on Linux with the Windows
executor backend named as the remediation rather than being silently omitted.

Image builds are now actually available where they are wanted, and the builder is scoped to the run.
`k8s/optional/sandbox-buildkit-sidecar.yaml` adds an opt-in BuildKit sidecar to the sandbox pod,
reached over a pod-local unix socket through the `awx-docker` shim — so a run can `docker build`
without the sandbox container gaining a single capability (measured `CapEff: 0000000000000000`).
Because a sandbox pod is a Kata VM, the builder's cache, history and content store are scoped to
that one run: a second run sees an empty `debug histories` and an empty cache. That closes the
cross-run channel a shared broker would have opened, where any run holding a client certificate
could read another run's build logs and blobs through BuildKit's debug APIs.

The trade-off is stated rather than hidden: the sidecar must be rootful with `CAP_SYS_ADMIN` and
`CAP_NET_ADMIN`, because rootless BuildKit cannot work under Kata. Those capabilities are confined
to the run's guest kernel — measured in-guest `NET_ADMIN` cannot defeat NetworkPolicy, IMDS stays
blocked, build steps stay runc-confined, and the daemon refuses the `security.insecure` entitlement
— but a `buildkitd` vulnerability would be a root-in-guest compromise, so the sidecar is off by
default and requires a namespace whose PodSecurity level admits those two capabilities. `awx-docker`
also validates `--output`, so `type=image,push=true` and `type=registry` are refused rather than
walking around the `--push` refusal.
