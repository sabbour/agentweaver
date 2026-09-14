param([Parameter(Mandatory=$true)][string]$Phase)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$models = Get-Content (Join-Path $PSScriptRoot 'owned-models-v2.json') -Raw | ConvertFrom-Json
$sheetDirectory = Join-Path $PSScriptRoot 'v2\sheets'
New-Item -ItemType Directory -Path $sheetDirectory -Force | Out-Null
$names = @()
foreach ($diagram in $models.diagrams) {
    $folder = Join-Path $repo "docs\diagrams\reviews\$($diagram.name)\v2"
    $source = Join-Path $folder "$($diagram.name)-$Phase.png"
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $names += $diagram.name
    $image = [Drawing.Image]::FromFile($source)
    $height = [int][Math]::Round($image.Height * 794 / $image.Width)
    $print = [Drawing.Bitmap]::new(794, $height)
    $graphics = [Drawing.Graphics]::FromImage($print)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($image, 0, 0, 794, $height)
        $print.Save((Join-Path $folder "$($diagram.name)-$Phase-print.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $print.Dispose()
        $image.Dispose()
    }
}
for ($offset=0; $offset -lt $names.Count; $offset+=4) {
    $sheet = [Drawing.Bitmap]::new(1588, 1200)
    $graphics = [Drawing.Graphics]::FromImage($sheet)
    $font = [Drawing.Font]::new('Segoe UI', 14)
    try {
        $graphics.Clear([Drawing.Color]::White)
        for ($i=0; $i -lt 4 -and ($offset+$i) -lt $names.Count; $i++) {
            $name = $names[$offset+$i]
            $x = ($i % 2) * 794
            $y = [Math]::Floor($i / 2) * 600
            $graphics.DrawString("$name / $Phase", $font, [Drawing.Brushes]::Black, $x+8, $y+5)
            $file = Join-Path $repo "docs\diagrams\reviews\$name\v2\$name-$Phase-print.png"
            $image = [Drawing.Image]::FromFile($file)
            try { $graphics.DrawImage($image, $x, $y+30, $image.Width, $image.Height) } finally { $image.Dispose() }
        }
        $number = [int]($offset/4)+1
        $sheet.Save((Join-Path $sheetDirectory ("$Phase-sheet-{0:D2}.png" -f $number)), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $font.Dispose()
        $sheet.Dispose()
    }
}
Write-Output "$Phase : $($names.Count) print derivatives; $([Math]::Ceiling($names.Count/4)) contact sheets"
for ($offset=0; $offset -lt $names.Count; $offset+=2) {
    $loaded = @()
    foreach ($index in $offset..([Math]::Min($offset+1, $names.Count-1))) {
        $name = $names[$index]
        $file = Join-Path $repo "docs\diagrams\reviews\$name\v2\$name-$Phase.png"
        $loaded += [Drawing.Image]::FromFile($file)
    }
    $width = ($loaded | Measure-Object Width -Maximum).Maximum
    $height = ($loaded | Measure-Object Height -Sum).Sum + 40*$loaded.Count
    $sheet = [Drawing.Bitmap]::new([int]$width, [int]$height)
    $graphics = [Drawing.Graphics]::FromImage($sheet)
    $font = [Drawing.Font]::new('Segoe UI', 18)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $y = 0
        for ($i=0; $i -lt $loaded.Count; $i++) {
            $graphics.DrawString("$($names[$offset+$i]) / $Phase / original export pixels", $font, [Drawing.Brushes]::Black, 8, $y+4)
            $graphics.DrawImageUnscaled($loaded[$i], 0, $y+40)
            $y += $loaded[$i].Height+40
        }
        $number = [int]($offset/2)+1
        $sheet.Save((Join-Path $sheetDirectory ("$Phase-enlarged-{0:D2}.png" -f $number)), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $font.Dispose()
        $sheet.Dispose()
        foreach ($image in $loaded) { $image.Dispose() }
    }
}
Write-Output "$Phase : $([Math]::Ceiling($names.Count/2)) lossless original-pixel inspection pairs"
