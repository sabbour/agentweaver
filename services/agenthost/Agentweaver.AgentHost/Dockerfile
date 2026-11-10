# Build context is the repository root. The final image is always linux/amd64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
ENV TMPDIR=/src TEMP=/src TMP=/src
COPY Directory.Build.props global.json NuGet.Config ./
COPY eng/acs/ eng/acs/
COPY packages/Agentweaver.Abstractions/ packages/Agentweaver.Abstractions/
COPY packages/Agentweaver.Identity/ packages/Agentweaver.Identity/
COPY packages/Agentweaver.AgentRuntime/ packages/Agentweaver.AgentRuntime/
COPY packages/Agentweaver.Providers/ packages/Agentweaver.Providers/
COPY packages/Agentweaver.SourceControl/ packages/Agentweaver.SourceControl/
COPY packages/Agentweaver.Telemetry/ packages/Agentweaver.Telemetry/
COPY services/agenthost/Agentweaver.AgentHost/ services/agenthost/Agentweaver.AgentHost/
COPY tests/Agentweaver.Acs.ImageProbe/ tests/Agentweaver.Acs.ImageProbe/
RUN dotnet restore services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj --runtime linux-x64 --locked-mode && \
    dotnet publish services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj \
    --configuration Release --runtime linux-x64 --no-restore --no-self-contained -p:UseAppHost=false --output /publish
RUN dotnet restore tests/Agentweaver.Acs.ImageProbe/Agentweaver.Acs.ImageProbe.csproj --runtime linux-x64 --locked-mode && \
    dotnet publish tests/Agentweaver.Acs.ImageProbe/Agentweaver.Acs.ImageProbe.csproj \
    --configuration Release --runtime linux-x64 --no-restore --no-self-contained -p:UseAppHost=false --output /acs-probe && \
    test -s /acs-probe/libagent_control_specification.so && \
    rm /acs-probe/libagent_control_specification.so && \
    echo '416b7a116742e81846655db09ed7854dc5441dd05361db13828088a2e11f8bf7  eng/acs/nuget/AgentControlSpecification.0.4.0-beta.0.nupkg' | sha256sum --check --status && \
    echo '286fe960ca9d25f01839dfc3877c9ae8a088334b23f15cc8632384ad04bd037c  /publish/libagent_control_specification.so' | sha256sum --check --status
RUN test -s eng/acs/opa_linux_amd64_static && \
    echo '00d114b94fdb1606a48cccdfc73c9ccdc62c38721150131ae578d5ff3df5c084  eng/acs/opa_linux_amd64_static' | sha256sum --check --status && \
    cp eng/acs/opa_linux_amd64_static /publish/opa && \
    chmod 0755 /publish/opa

RUN apt-get update && apt-get install --no-install-recommends --yes curl ca-certificates && \
    rm -rf /var/lib/apt/lists/*
RUN curl --fail --location --proto '=https' --tlsv1.2 \
    https://ms-feed-25.pkgs.visualstudio.com/1es-public/_packaging/npm-public/npm/registry/@github/copilot-linux-x64/-/copilot-linux-x64-1.0.97.tgz \
    --output /src/copilot.tgz && \
    echo '262068b5c39fd83706bec46a11346ed28a33a99f4ceab1fc9e30a416de30ee4059beb1c6f8c1e1228d666c6289b28d86663067655b6f258b0a5cc655dcdc7f8a  /src/copilot.tgz' | sha512sum --check --status && \
    mkdir /publish/native && \
    tar --extract --gzip --file /src/copilot.tgz --strip-components=1 --directory /publish/native package/copilot && \
    test -s /publish/native/copilot && test -x /publish/native/copilot && \
    rm /src/copilot.tgz && \
    sha256sum /publish/native/copilot | cut -d ' ' -f 1 > /publish/native/copilot.sha256 && \
    mkdir /private-state /workspace

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:0fa044f682cb7d93a5a90401a00c626c66f7b00b86922be9441f869eae039f80
RUN apt-get update && apt-get install --no-install-recommends --yes git ca-certificates && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish/ ./
COPY --from=build --chown=1654:1654 /private-state/ /state/
COPY --from=build --chown=1654:1654 /workspace/ /workspace/
RUN HOME=/workspace /app/native/copilot --no-auto-update --version && \
    runtime_distribution=/workspace/.cache/copilot/pkg/linux-x64/1.0.97 && \
    for file in index.js app.js prebuilds/linux-x64/cli-native.node; do \
      test -s "$runtime_distribution/$file"; \
    done && \
    cp -a "$runtime_distribution/." /app/native/ && \
    for file in index.js app.js prebuilds/linux-x64/cli-native.node; do \
      test -s "/app/native/$file"; \
    done && \
    test "$(sha256sum /app/native/copilot | cut -d ' ' -f 1)" = "$(cat /app/native/copilot.sha256)" && \
    rm -r /workspace/.cache
ENV ASPNETCORE_URLS=https://+:8443 \
    AgentHost__PrivateStateDirectory=/state \
    AgentHost__WorkingDirectory=/workspace \
    ACS_OPA_PATH=/app/opa \
    DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8443
RUN --mount=type=bind,from=build,source=/acs-probe,target=/acs-probe,readonly \
    test -s /app/libagent_control_specification.so && \
    echo '286fe960ca9d25f01839dfc3877c9ae8a088334b23f15cc8632384ad04bd037c  /app/libagent_control_specification.so' | sha256sum --check --status && \
    test -x /app/opa && \
    echo '00d114b94fdb1606a48cccdfc73c9ccdc62c38721150131ae578d5ff3df5c084  /app/opa' | sha256sum --check --status && \
    TMPDIR=/workspace TEMP=/workspace TMP=/workspace LD_LIBRARY_PATH=/app dotnet /acs-probe/Agentweaver.Acs.ImageProbe.dll
ENTRYPOINT ["dotnet", "Agentweaver.AgentHost.dll"]
