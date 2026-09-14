$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$models = Get-Content (Join-Path $PSScriptRoot 'owned-models-v2.json') -Raw | ConvertFrom-Json
$bitmap = [Drawing.Bitmap]::new(1, 1)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$format = [Drawing.StringFormat]::GenericTypographic.Clone()
$format.FormatFlags = $format.FormatFlags -bor [Drawing.StringFormatFlags]::MeasureTrailingSpaces
try {
    foreach ($model in $models.diagrams) {
        $folder = Join-Path $repo "docs\diagrams\reviews\$($model.name)\v2"
        [xml]$xml = Get-Content (Join-Path $folder "$($model.name)-pass-01.drawio") -Raw
        $corrections = @()
        foreach ($cell in $xml.SelectNodes('//mxCell[@vertex="1"]')) {
            if ($cell.id -notmatch '^n\d+-(title|sub|meta)$') { continue }
            $style = @{}
            foreach ($token in $cell.style.Split(';')) {
                $parts = $token.Split('=', 2)
                if ($parts.Length -eq 2) { $style[$parts[0]] = $parts[1] }
            }
            $size = [double]$style.fontSize
            $initial = $size
            $fontStyle = if ($style.fontStyle -eq '1') { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
            $available = [double]$cell.mxGeometry.width - 8
            do {
                $font = [Drawing.Font]::new($style.fontFamily, [single]$size, $fontStyle, [Drawing.GraphicsUnit]::Pixel)
                try { $width = $graphics.MeasureString($cell.value, $font, [Drawing.PointF]::new(0,0), $format).Width }
                finally { $font.Dispose() }
                if ($width -le $available) { break }
                $size -= 0.5
            } while ($size -ge 10)
            if ($size -lt 10) { throw "Existing label cannot fit at readable size: $($model.name)/$($cell.id)" }
            if ($size -lt $initial) {
                $corrections += [PSCustomObject]@{id=$cell.id;original_size=$initial;font_size=$size;measured_width=$width;available_width=$available;reason='Prevent existing label wrapping into adjacent subtitle/metadata'}
            }
        }
        $output = ConvertTo-Json -InputObject @($corrections) -Depth 5
        [IO.File]::WriteAllText((Join-Path $folder 'text-fit-corrections.json'), $output+"`n")
        if ($corrections.Count) { "$($model.name): $($corrections.Count) text-fit corrections" }
    }
} finally {
    $format.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
