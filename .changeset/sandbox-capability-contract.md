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

The trade-off is stated rather than hidden: the sidecar must be rootful with `CAP_SYS_ADMIN`,
`CAP_NET_ADMIN` and `CAP_SYS_PTRACE`, because rootless BuildKit cannot work under Kata. Those
capabilities are confined
to the run's guest kernel — measured in-guest `NET_ADMIN` cannot defeat NetworkPolicy, IMDS stays
blocked, build steps stay runc-confined, and the daemon refuses the `security.insecure` entitlement
— but a `buildkitd` vulnerability would be a root-in-guest compromise, so the sidecar is off by
default and requires a namespace whose PodSecurity level admits those three capabilities.

Build steps run in their own empty network namespace (loopback only), so a Dockerfile line that
needs the network — `RUN apt-get install`, `RUN npm install` — does **not** work; install
dependencies in the sandbox shell, which keeps normal pod networking, and `COPY` the result in.
Base images still pull, because the daemon fetches them itself. The capability contract declares
this limit, so an agent discovers it before starting rather than halfway through a build.

`awx-docker` also validates `--output` by parsing it as CSV field by field, so `type=image,push=true`,
`type=registry`, a quoted `"push=true"` field and a second `type=` in the same value are all refused.
That refusal is ergonomics, not a boundary — `buildctl` is on `PATH` and `BUILDKIT_HOST` is
exported, so a caller can reach the daemon directly. What actually prevents publishing is that the
builder holds no registry credential, no ServiceAccount token and no workload identity.

Availability of the build capability is measured, not assumed: the executor connects to the socket
and speaks the HTTP/2 preface, reporting `image_build` as supported only when a real builder answers.
A crashed daemon that left its socket file behind, or a sidecar still starting up, is reported as
`RequiresExternalService` rather than advertised as a builder whose every build fails at connect time.
