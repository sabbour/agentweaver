param(
    [Parameter(Mandatory = $true)]
    [string] $BuildDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$source = '2d8606fa825e8e2d52f8d95e2f4066b148b78289'
$tree = '6753d68e91a769ca26812c55ec7f4cc8a8d70028'
$manifest = Get-Content (Join-Path $PSScriptRoot 'S1-owner-provenance.json') -Raw | ConvertFrom-Json
if ($manifest.sourceCommit -ne $source -or $manifest.sourceTree -ne $tree) {
    throw 'Unexpected source identity in provenance manifest.'
}
$build = [System.IO.Path]::GetFullPath($BuildDirectory)
$parent = Split-Path $build -Parent
if (!(Test-Path -PathType Container $parent)) { throw "Build parent does not exist: $parent" }
if (Test-Path $build) { throw "Build directory already exists: $build" }
git -C $parent rev-parse --is-inside-work-tree 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'Build directory must be outside a Git worktree.' }
if ((git -C $root rev-parse "$source^{tree}") -ne $tree) { throw 'Unexpected source tree.' }

New-Item -ItemType Directory -Path $build | Out-Null
Push-Location $build
try {
    git -C $root archive --format=tar "--output=$build\source.tar" $source
    if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
    if ((Get-FileHash 'source.tar' -Algorithm SHA256).Hash -ne $manifest.sourceArchiveSha256) {
        throw 'Source archive hash mismatch.'
    }
    $tracked = @(git -C $root ls-tree -r --name-only $source)
    if ($LASTEXITCODE -ne 0) { throw 'Source tree listing failed.' }
    $archived = @(tar -tf 'source.tar' | Where-Object { !$_.EndsWith('/') })
    if ($LASTEXITCODE -ne 0 -or (Compare-Object -CaseSensitive $tracked $archived)) {
        throw 'Source archive does not contain the full tracked tree.'
    }
    New-Item -ItemType Directory -Path 'source' | Out-Null
    tar -xf 'source.tar' -C 'source'
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
    Set-Location 'source'
    $sourceDirectory = (Get-Location).Path
    @'
<Project>
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Agentweaver.Projects.Config'">
    <AssemblyName>Agentweaver.Projects.Config.S1</AssemblyName>
    <TargetName>Agentweaver.Projects.Config.S1</TargetName>
  </PropertyGroup>
</Project>
'@ | Set-Content -Encoding utf8 'Directory.Build.targets'
    if ((dotnet --version) -ne $manifest.sdk) { throw "Use .NET SDK $($manifest.sdk)." }
    $project = 'services\projects-config\Agentweaver.Projects.Config\Agentweaver.Projects.Config.csproj'
    dotnet restore $project --locked-mode --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build $project --configuration Release --no-restore "-p:SourceRevisionId=$source" `
        -p:ContinuousIntegrationBuild=true -p:EnableSourceControlManagerQueries=false `
        "-p:PathMap=$sourceDirectory=/_/" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Historical owner build failed.' }
    $sourceLink = 'services\projects-config\Agentweaver.Projects.Config\obj\Release\net10.0\Agentweaver.Projects.Config.sourcelink.json'
    if (Test-Path $sourceLink) { throw 'Unexpected SourceLink metadata.' }
    $output = 'services\projects-config\Agentweaver.Projects.Config\bin\Release\net10.0'
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
