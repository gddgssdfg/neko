param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$SkinDirectory = (Join-Path $PSScriptRoot '../neko-assets/amber'),
    [string]$OutputDirectory = $SkinDirectory
)
# Mechanical packing: generated middle poses plus byte-copied canonical endpoints.
# No repainting, background removal, or synthetic body deformation.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$raw = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Source).Path)
$canonical = @{}
function Find-GapMidpoint([int[]]$Counts, [int]$Start, [int]$End) {
    $bestStart = -1; $bestLength = 0; $runStart = -1
    for ($index = $Start; $index -le $End; $index++) {
        if ($Counts[$index] -eq 0 -and $index -lt $End) { if ($runStart -lt 0) { $runStart = $index } }
        else {
            if ($runStart -ge 0 -and $index - $runStart -gt $bestLength) {
                $bestStart = $runStart; $bestLength = $index - $runStart
            }
            $runStart = -1
        }
    }
    if ($bestLength -lt 2) { throw 'No clear alpha gap between cells. Inspect the source atlas before packing.' }
    return [int]($bestStart + [Math]::Floor($bestLength / 2))
}
function Copy-CanonicalFrame($Destination, [int]$DestinationIndex, [string]$State, [int]$FrameIndex) {
    $bitmap = $canonical[$State]
    $sourceRect = [System.Drawing.Rectangle]::new($FrameIndex * 256, 0, 256, 247)
    $destinationRect = [System.Drawing.Rectangle]::new($DestinationIndex * 256, 0, 256, 247)
    $sourceData = $null; $destinationData = $null
    try {
        $sourceData = $bitmap.LockBits($sourceRect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $destinationData = $Destination.LockBits($destinationRect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $rowBytes = [byte[]]::new(256 * 4)
        for ($y = 0; $y -lt 247; $y++) {
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($sourceData.Scan0, $y * $sourceData.Stride), $rowBytes, 0, $rowBytes.Length)
            [Runtime.InteropServices.Marshal]::Copy($rowBytes, 0, [IntPtr]::Add($destinationData.Scan0, $y * $destinationData.Stride), $rowBytes.Length)
        }
    }
    finally {
        if ($destinationData) { $Destination.UnlockBits($destinationData) }
        if ($sourceData) { $bitmap.UnlockBits($sourceData) }
    }
}
try {
    foreach ($state in 'sit','sleep','idle','pet','work') {
        $canonical[$state] = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath (Join-Path $SkinDirectory "neko_$state.png")).Path)
        if ($canonical[$state].Width -ne 1024 -or $canonical[$state].Height -ne 247) { throw "Unexpected canonical dimensions: $state" }
    }
    $columns = [int[]]::new($raw.Width)
    for ($x = 0; $x -lt $raw.Width; $x++) {
        for ($y = 0; $y -lt $raw.Height; $y++) { if ($raw.GetPixel($x, $y).A -gt 16) { $columns[$x]++ } }
    }
    $dividers = @(0)
    for ($column = 1; $column -lt 4; $column++) {
        $dividers += Find-GapMidpoint $columns ([int]($raw.Width * ($column / 4.0 - .07))) ([int]($raw.Width * ($column / 4.0 + .07)))
    }
    $dividers += $raw.Width
    $cells = [System.Drawing.Rectangle[]]::new(8)
    for ($column = 0; $column -lt 4; $column++) {
        $rows = [int[]]::new($raw.Height)
        for ($y = 0; $y -lt $raw.Height; $y++) {
            for ($x = $dividers[$column]; $x -lt $dividers[$column + 1]; $x++) {
                if ($raw.GetPixel($x, $y).A -gt 16) { $rows[$y]++ }
            }
        }
        $split = Find-GapMidpoint $rows ([int]($raw.Height * .4)) ([int]($raw.Height * .6))
        for ($row = 0; $row -lt 2; $row++) {
            $startY = if ($row -eq 0) { 0 } else { $split }
            $endY = if ($row -eq 0) { $split } else { $raw.Height }
            $left = $raw.Width; $right = -1; $top = $raw.Height; $bottom = -1
            for ($y = $startY; $y -lt $endY; $y++) {
                for ($x = $dividers[$column]; $x -lt $dividers[$column + 1]; $x++) {
                    if ($raw.GetPixel($x, $y).A -gt 8) {
                        $left = [Math]::Min($left, $x); $right = [Math]::Max($right, $x)
                        $top = [Math]::Min($top, $y); $bottom = [Math]::Max($bottom, $y)
                    }
                }
            }
            if ($right -lt 0) { throw 'Empty intermediate pose' }
            $left = [Math]::Max($dividers[$column], $left - 2); $right = [Math]::Min($dividers[$column + 1] - 1, $right + 2)
            $top = [Math]::Max($startY, $top - 2); $bottom = [Math]::Min($endY - 1, $bottom + 2)
            $cells[$row * 4 + $column] = [System.Drawing.Rectangle]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
        }
    }
    $routes = @(
        @{ Name='sleep_enter'; From='sit'; To='sleep'; Last=0; Middle=@(0,1); Heights=@(190,160) },
        @{ Name='wake_idle'; From='sleep'; To='idle'; Last=0; Middle=@(1,0); Heights=@(160,190) },
        @{ Name='wake_work'; From='sleep'; To='work'; Last=1; Middle=@(2,3); Heights=@(165,196) },
        @{ Name='pet_end_idle'; From='pet'; To='idle'; Last=0; Middle=@(4,5); Heights=@(214,214) },
        @{ Name='pet_end_work'; From='pet'; To='work'; Last=1; Middle=@(6,7); Heights=@(205,196) }
    )
    foreach ($route in $routes) {
        $sheet = [System.Drawing.Bitmap]::new(1024, 247, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($sheet)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                for ($index = 0; $index -lt 2; $index++) {
                    $box = $cells[$route.Middle[$index]]
                    $scale = [Math]::Min(238 / $box.Width, $route.Heights[$index] / $box.Height)
                    $width = [single]($box.Width * $scale); $height = [single]($box.Height * $scale)
                    $destination = [System.Drawing.RectangleF]::new([single](($index + 1) * 256 + (256 - $width) / 2), [single](241 - $height), $width, $height)
                    $sourceRect = [System.Drawing.RectangleF]::new($box.X, $box.Y, $box.Width, $box.Height)
                    $graphics.DrawImage($raw, $destination, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
                }
            }
            finally { $graphics.Dispose() }
            # Raw BGRA copy avoids alpha/color rounding in the endpoint drawing path.
            Copy-CanonicalFrame $sheet 0 $route.From 0
            Copy-CanonicalFrame $sheet 3 $route.To $route.Last
            $sheet.Save((Join-Path $OutputDirectory "neko_$($route.Name).png"), [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output "Packed $($route.Name): exact $($route.From)[0] / $($route.To)[$($route.Last)] endpoints"
        }
        finally { $sheet.Dispose() }
    }
}
finally { $raw.Dispose(); foreach ($bitmap in $canonical.Values) { $bitmap.Dispose() } }
