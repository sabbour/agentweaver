$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
Set-Location $root
$renderer = 'C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe'
$checks = @(
    @{ Name='node-tests'; Program='npm'; Arguments=@('run','test:docs-diagrams') },
    @{ Name='pitch-tests'; Program='python'; Arguments=@('-B','-m','unittest','discover','-s','.github\skills\docs-diagram-pitch\tests','-v') },
    @{ Name='iterate-tests'; Program='python'; Arguments=@('-B','-m','unittest','discover','-s','.github\skills\docs-diagram-iterate\tests','-v') },
    @{ Name='audit-tests'; Program='python'; Arguments=@('-B','-m','unittest','discover','-s','.github\skills\docs-diagram-audit\tests','-v') },
    @{ Name='inventory'; Program='node'; Arguments=@('scripts\docs\inventory-diagrams.mjs','--check') },
    @{ Name='audit-final'; Program='python'; Arguments=@('-B','.github\skills\docs-diagram-audit\scripts\diagram_audit.py','validate','docs-diagram-audit.json','--final') },
    @{ Name='full-render'; Program='npm'; Arguments=@('run','docs:render-diagrams','--','--drawio-cli',$renderer) },
    @{ Name='full-drift'; Program='npm'; Arguments=@('run','docs:check-diagrams') },
    @{ Name='rerender-identity'; Program='python'; Arguments=@('-B','docs\diagrams\reviews\catalog-integration\verify-rerender.py','after') },
    @{ Name='docs-build'; Program='npm'; Arguments=@('run','docs:build') },
    @{ Name='catalog'; Program='python'; Arguments=@('-B','docs\diagrams\reviews\catalog-integration\validate-catalog.py') },
    @{ Name='diff-check'; Program='git'; Arguments=@('diff','--check') }
)
$results = @()
foreach ($check in $checks) {
    $log = Join-Path $PSScriptRoot ("final-" + $check.Name + '.log')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $arguments = $check.Arguments
    & $check.Program @arguments *> $log
    $code = $LASTEXITCODE
    $clock.Stop()
    $results += @{
        name=$check.Name; command=(@($check.Program)+$arguments); exit_code=$code
        seconds=[Math]::Round($clock.Elapsed.TotalSeconds,3); log=(Split-Path $log -Leaf)
    }
    ConvertTo-Json -InputObject $results -Depth 8 | Set-Content (Join-Path $PSScriptRoot 'completion-checks.json') -Encoding utf8
    Write-Output "$($check.Name): exit $code ($($clock.Elapsed.TotalSeconds.ToString('F1'))s)"
}
if ($results.Where({$_.exit_code -ne 0}).Count) { exit 1 }
