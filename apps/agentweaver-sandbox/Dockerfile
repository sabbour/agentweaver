# agentweaver-sandbox — base image for Agentweaver sandbox execution pods.
#
# Provides the language runtimes needed by agent workloads: git, Python 3,
# Node.js/npm, and .NET SDK 9.0. The container runs as non-root (uid 1000)
# matching the SandboxTemplate securityContext (runAsUser: 1000).
#
# readOnlyRootFilesystem is enforced by the SandboxTemplate; writable mounts
# are provided at /workspace (agent work) and /tmp (tools scratch) as emptyDir
# volumes. No writable layer is required at runtime.
#
# CMD: sleep infinity — keeps the pod alive for kubectl exec sessions without
# running a server process. The agent drives all work via pod-exec.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

# Install base tools + language runtimes in one layer to minimize image size.
# nodejs/npm: use Ubuntu 24.04's bundled version (Node 18 LTS).
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    wget \
    git \
    build-essential \
    python3 \
    python3-pip \
    python3-venv \
    nodejs \
    npm \
    && rm -rf /var/lib/apt/lists/*

# Install .NET SDK 9.0 via Microsoft's install script (no apt repo pinning needed).
RUN curl -sSL https://dot.net/v1/dotnet-install.sh \
    | bash -s -- --channel 9.0 --install-dir /usr/local/dotnet \
    && ln -sf /usr/local/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/local/dotnet
ENV PATH="${PATH}:/usr/local/dotnet"
# Suppress first-run telemetry prompt and keep .NET artefacts out of /root.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

# Create non-root user (uid/gid 1000) to match SandboxTemplate securityContext.
# --no-create-home: home dir is not needed; /workspace is the working directory.
RUN groupadd --gid 1000 sandbox \
    && useradd --uid 1000 --gid 1000 --no-create-home --shell /bin/bash sandbox

WORKDIR /workspace

USER 1000

# Keep the pod alive for kubectl exec. The agent drives all execution via exec,
# not via a long-running entrypoint process.
CMD ["sleep", "infinity"]
