# ACS runtime assets

This directory holds the pinned Agent Control Specification (ACS) package and
the image-build inputs that use it. It does not define Agentweaver Policies or
activate an ACS store.

## Package provenance

The package was built from the official Microsoft
[Agent Governance Toolkit](https://github.com/microsoft/agent-governance-toolkit)
source at commit
[`48f04e5ad1efd4f2e5692d4b67def2e0f99a6870`](https://github.com/microsoft/agent-governance-toolkit/tree/48f04e5ad1efd4f2e5692d4b67def2e0f99a6870).
The source project declares package `AgentControlSpecification`
`0.4.0-beta.0`.

The package and its native library have these SHA-256 values:

| File | SHA-256 |
| --- | --- |
| `nuget/AgentControlSpecification.0.4.0-beta.0.nupkg` | `416b7a116742e81846655db09ed7854dc5441dd05361db13828088a2e11f8bf7` |
| `runtimes/linux-x64/native/libagent_control_specification.so` | `286fe960ca9d25f01839dfc3877c9ae8a088334b23f15cc8632384ad04bd037c` |

The build used Rust `1.89.0` from
`rust:1.89.0-bookworm@sha256:c9ac3fa8945b61dede1e4500d25028aa8fd8a8fe46365fcf9c0422f8d999b9b0`
and .NET SDK `8.0.428` from
`mcr.microsoft.com/dotnet/sdk@sha256:4cdea2a99ba7f4d8a48ebe250ec01db119d2102c141f85411a576be7bac5eb23`.
From a checkout of the exact AGT source commit, the Linux-x64 library was built
with:

```bash
cargo build --locked --release --quiet -p agent_control_specification \
  --features opa,bundled-dispatchers \
  --target x86_64-unknown-linux-gnu \
  --manifest-path policy-engine/Cargo.toml

mkdir -p policy-engine/sdk/dotnet/src/AgentControlSpecification/runtimes/linux-x64/native
cp policy-engine/target/x86_64-unknown-linux-gnu/release/libagent_control_specification.so \
  policy-engine/sdk/dotnet/src/AgentControlSpecification/runtimes/linux-x64/native/
```

The `.NET` package was packed with the staged Linux-x64 library and the
upstream native-build targets disabled. Set `AGENTWEAVER_ROOT` to the root of
the Agentweaver checkout before running this command:

```bash
export AGENTWEAVER_ROOT='/path/to/agentweaver-v1'
dotnet pack policy-engine/sdk/dotnet/src/AgentControlSpecification/AgentControlSpecification.csproj \
  --configuration Release \
  -p:AgentControlSpecificationSkipNativeBuild=true \
  -p:AgentControlSpecificationAllowIncompleteNativePack=true \
  --output "$AGENTWEAVER_ROOT/eng/acs/nuget"
```

Replace `/path/to/agentweaver-v1` with the root path of the Agentweaver
checkout. This package contains only the `linux-x64` native asset. The local
NuGet feed is selected only for `AgentControlSpecification` by the root
`NuGet.Config`; other packages restore from nuget.org. Linux-x64 restores use
per-project `packages.linux-x64.lock.json` files. Regular restores keep using
`packages.lock.json`, so other locked restores do not gain a Linux-only runtime
requirement. Both lock files record the package content hash.

## OPA provenance

The image build downloads OPA `0.70.0` from the official
[OPA download URL](https://openpolicyagent.org/downloads/v0.70.0/opa_linux_amd64_static).
Its SHA-256 is
`00d114b94fdb1606a48cccdfc73c9ccdc62c38721150131ae578d5ff3df5c084`.
The build fails if the hash does not match.

## Image flow and checks

The Orchestrator and AgentHost Dockerfiles restore the package for
`linux-x64`, publish the service, and verify the packaged native library hash.
Each Dockerfile copies the verified OPA binary to `/app/opa`, sets
`ACS_OPA_PATH=/app/opa`, and sets `LD_LIBRARY_PATH=/app`. The image build runs
the same probe inside each final image. It removes the probe's own native
library, then loads the image's library through that search path. The probe
checks OPA allow, deny,
fail-closed behavior with an invalid OPA path, and timeout denial. A failed
asset check or probe stops that image build.

The build does not download assets at runtime, search `PATH` for OPA, enable a
permissive mode, activate ACS, or change stored policy data. It proves runtime
assets only; Agentweaver does not yet use them to authorize actions.
