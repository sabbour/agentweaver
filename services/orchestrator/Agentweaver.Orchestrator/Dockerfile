FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
ENV TMPDIR=/src TEMP=/src TMP=/src
COPY Directory.Build.props global.json NuGet.Config ./
COPY eng/acs/ eng/acs/
COPY scripts/azure/lib/p1-postgres-plan.json scripts/azure/lib/p1-postgres-plan.json
COPY packages/ packages/
COPY services/orchestrator/ services/orchestrator/
COPY tests/Agentweaver.Acs.ImageProbe/ tests/Agentweaver.Acs.ImageProbe/
RUN dotnet restore services/orchestrator/Agentweaver.Orchestrator/Agentweaver.Orchestrator.csproj --runtime linux-x64 --locked-mode -p:AgentweaverUseLinuxX64LockFile=true && \
    dotnet publish services/orchestrator/Agentweaver.Orchestrator/Agentweaver.Orchestrator.csproj \
    --configuration Release --runtime linux-x64 --no-restore --no-self-contained -p:UseAppHost=false --output /publish
RUN dotnet restore tests/Agentweaver.Acs.ImageProbe/Agentweaver.Acs.ImageProbe.csproj --runtime linux-x64 --locked-mode -p:AgentweaverUseLinuxX64LockFile=true && \
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

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build --chown=1654:1654 /publish/ ./
RUN mkdir /app/.acs-probe && chown 1654:1654 /app/.acs-probe
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ACS_OPA_PATH=/app/opa \
    LD_LIBRARY_PATH=/app
USER 1654
EXPOSE 8080
RUN --mount=type=bind,from=build,source=/acs-probe,target=/acs-probe,readonly \
    test -s /app/libagent_control_specification.so && \
    echo '286fe960ca9d25f01839dfc3877c9ae8a088334b23f15cc8632384ad04bd037c  /app/libagent_control_specification.so' | sha256sum --check --status && \
    test -x /app/opa && \
    echo '00d114b94fdb1606a48cccdfc73c9ccdc62c38721150131ae578d5ff3df5c084  /app/opa' | sha256sum --check --status && \
    ACS_PROBE_TEMP_DIRECTORY=/app/.acs-probe TMPDIR=/app/.acs-probe \
    TEMP=/app/.acs-probe TMP=/app/.acs-probe \
    dotnet /acs-probe/Agentweaver.Acs.ImageProbe.dll
ENTRYPOINT ["dotnet", "/app/Agentweaver.Orchestrator.dll"]
