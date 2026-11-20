$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$source = '2d8606fa825e8e2d52f8d95e2f4066b148b78289'
$tree = '6753d68e91a769ca26812c55ec7f4cc8a8d70028'
$build = Join-Path $root ('obj\s1-owner-rebuild-2639-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path $build) { throw "Build directory already exists: $build" }
Push-Location $root
try {
    if ((git rev-parse "$source^{tree}") -ne $tree) { throw 'Unexpected source tree.' }
    New-Item -ItemType Directory -Path $build | Out-Null
    git archive --format=tar $source | tar -xf - -C $build
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
    @'
<Project>
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Agentweaver.Projects.Config'">
    <AssemblyName>Agentweaver.Projects.Config.S1</AssemblyName>
    <TargetName>Agentweaver.Projects.Config.S1</TargetName>
  </PropertyGroup>
</Project>
'@ | Set-Content -Encoding utf8 (Join-Path $build 'Directory.Build.targets')
    Set-Location $build
    if ((dotnet --version) -ne '10.0.404') { throw 'Use .NET SDK 10.0.404.' }
    $project = 'services\projects-config\Agentweaver.Projects.Config\Agentweaver.Projects.Config.csproj'
    dotnet restore $project --locked-mode --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build $project --configuration Release --no-restore "-p:SourceRevisionId=$source" `
        -p:ContinuousIntegrationBuild=true "-p:PathMap=$build=/_/" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Historical owner build failed.' }
    $manifest = Get-Content (Join-Path $PSScriptRoot 'S1-owner-provenance.json') -Raw | ConvertFrom-Json
    $output = Join-Path $build 'services\projects-config\Agentweaver.Projects.Config\bin\Release\net10.0'
    foreach ($entry in @(
        @('Agentweaver.Projects.Config.S1.dll', $manifest.assemblySha256),
        @('Agentweaver.Projects.Config.S1.deps.json', $manifest.dependenciesSha256)
    )) {
        $built = (Get-FileHash (Join-Path $output $entry[0]) -Algorithm SHA256).Hash
        $fixture = (Get-FileHash (Join-Path $PSScriptRoot $entry[0]) -Algorithm SHA256).Hash
        if ($built -ne $entry[1] -or $fixture -ne $built) { throw "Hash mismatch: $($entry[0])" }
        Write-Output "$built $($entry[0])"
    }
}
finally {
    Pop-Location
}
