# Build context is the repository root. The final image is always linux/amd64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY packages/Agentweaver.Abstractions/ packages/Agentweaver.Abstractions/
COPY packages/Agentweaver.Identity/ packages/Agentweaver.Identity/
COPY packages/Agentweaver.AgentRuntime/ packages/Agentweaver.AgentRuntime/
COPY packages/Agentweaver.Telemetry/ packages/Agentweaver.Telemetry/
COPY services/agenthost/Agentweaver.AgentHost/ services/agenthost/Agentweaver.AgentHost/
RUN dotnet restore services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj --locked-mode && \
    dotnet publish services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj \
    --configuration Release --no-restore --no-self-contained -p:UseAppHost=false --output /publish

RUN apt-get update && apt-get install --no-install-recommends --yes curl ca-certificates && \
    rm -rf /var/lib/apt/lists/*
RUN curl --fail --location --proto '=https' --tlsv1.2 \
    https://ms-feed-25.pkgs.visualstudio.com/1es-public/_packaging/npm-public/npm/registry/@github/copilot-linux-x64/-/copilot-linux-x64-1.0.79.tgz \
    --output /tmp/copilot.tgz && \
    echo 'c33a2d65fbc7908b6eb5c88b14c5d94f693d4228a2e136bcb6c54308c43b08ff213f157dd45c89d727f7f8515249f3ef5ab7d833a04e022a88b97f8b8e046e8b  /tmp/copilot.tgz' | sha512sum --check --status && \
    mkdir /publish/native && tar --extract --gzip --file /tmp/copilot.tgz --strip-components=1 --directory /publish/native && \
    rm -rf /publish/native/clipboard /publish/native/foundry-local-sdk /publish/native/pvrecorder /publish/native/webview && \
    test -s /publish/native/index.js && test -s /publish/native/app.js && \
    test -s /publish/native/prebuilds/linux-x64/runtime.node && \
    rm /tmp/copilot.tgz && \
    sha256sum /publish/native/copilot | cut -d ' ' -f 1 > /publish/native/copilot.sha256 && \
    mkdir /private-state /workspace

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:0fa044f682cb7d93a5a90401a00c626c66f7b00b86922be9441f869eae039f80
WORKDIR /app
COPY --from=build /publish/ ./
COPY --from=build --chown=1654:1654 /private-state/ /state/
COPY --from=build --chown=1654:1654 /workspace/ /workspace/
ENV ASPNETCORE_URLS=https://+:8443 \
    AgentHost__PrivateStateDirectory=/state \
    AgentHost__WorkingDirectory=/workspace \
    DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8443
ENTRYPOINT ["dotnet", "Agentweaver.AgentHost.dll"]
