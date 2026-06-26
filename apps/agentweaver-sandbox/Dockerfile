FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

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

ARG DOTNET_INSTALL_SHA256=4a9f24fc08d5f4ca4922f132bc6ace813b38c4b170a50e74cf760e5db6b1d9f1
RUN curl -fsSLo /usr/local/bin/dotnet-install.sh https://dot.net/v1/dotnet-install.sh \
    && echo "${DOTNET_INSTALL_SHA256}  /usr/local/bin/dotnet-install.sh" | sha256sum -c - \
    && bash /usr/local/bin/dotnet-install.sh --channel 9.0 --install-dir /usr/local/dotnet \
    && rm /usr/local/bin/dotnet-install.sh \
    && ln -sf /usr/local/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/local/dotnet
ENV PATH="${PATH}:/usr/local/dotnet"
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

RUN groupadd --gid 1000 sandbox \
    && useradd --uid 1000 --gid 1000 --no-create-home --shell /bin/bash sandbox

WORKDIR /workspace

USER 1000

CMD ["sleep", "infinity"]
