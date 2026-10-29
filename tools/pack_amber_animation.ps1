param(
    [Parameter(Mandatory = $true)][string]$Source,
    [ValidateSet('IdleWork', 'Walk')][string]$ActionSet = 'IdleWork',
    [string]$ContactBSource,
    [ValidateRange(0.1, 0.9)][double]$RowSplit = 0.53,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../neko-assets/amber')
)
# Mechanical atlas packing only: all artwork and transparency come from imagegen.
# IdleWork: four columns / two rows. Walk: two columns / two rows,
# optionally with an individually drawn reversed-contact frame. No repainting/background removal.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceBitmap = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Source).Path)
$contactBitmap = if ($ContactBSource) { [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $ContactBSource).Path) } else { $null }
try {
    if ($sourceBitmap.Width -lt 256 -or $sourceBitmap.Height -lt 128) { throw 'Source atlas is too small.' }
    $rowCount = if ($ActionSet -eq 'Walk') { 1 } else { 2 }
    for ($row = 0; $row -lt $rowCount; $row++) {
        $boxes = @()
        $frameSources = @()
        for ($column = 0; $column -lt 4; $column++) {
            $frameBitmap = $sourceBitmap
            if ($ActionSet -eq 'Walk') {
                $cellColumn = $column % 2; $cellRow = [int][Math]::Floor($column / 2)
                $originX = [int][Math]::Floor($cellColumn * $sourceBitmap.Width / 2)
                $originY = [int][Math]::Floor($cellRow * $sourceBitmap.Height / 2)
                $cellWidth = [int][Math]::Floor(($cellColumn + 1) * $sourceBitmap.Width / 2) - $originX
                $cellHeight = [int][Math]::Floor(($cellRow + 1) * $sourceBitmap.Height / 2) - $originY
                if ($column -eq 2 -and $contactBitmap) {
                    $frameBitmap = $contactBitmap; $originX = 0; $originY = 0
                    $cellWidth = $contactBitmap.Width; $cellHeight = $contactBitmap.Height
                }
            }
            else {
                $originX = [int][Math]::Floor($column * $sourceBitmap.Width / 4)
                $splitY = [int][Math]::Floor($RowSplit * $sourceBitmap.Height)
                $originY = if ($row -eq 0) { 0 } else { $splitY }
                $cellWidth = [int][Math]::Floor(($column + 1) * $sourceBitmap.Width / 4) - $originX
                $cellHeight = if ($row -eq 0) { $splitY } else { $sourceBitmap.Height - $splitY }
            }
            $left = $cellWidth; $top = $cellHeight; $right = -1; $bottom = -1
            for ($y = 0; $y -lt $cellHeight; $y++) {
                for ($x = 0; $x -lt $cellWidth; $x++) {
                    if ($frameBitmap.GetPixel($originX + $x, $originY + $y).A -gt 8) {
                        $left = [Math]::Min($left, $x); $right = [Math]::Max($right, $x)
                        $top = [Math]::Min($top, $y); $bottom = [Math]::Max($bottom, $y)
                    }
                }
            }
            if ($right -lt 0) { throw "Empty cell $row,$column" }
            # Include the antialiased fringe without altering any alpha values.
            $left = [Math]::Max(0, $left - 2); $top = [Math]::Max(0, $top - 2)
            $right = [Math]::Min($cellWidth - 1, $right + 2); $bottom = [Math]::Min($cellHeight - 1, $bottom + 2)
            $boxes += [System.Drawing.Rectangle]::new($originX + $left, $originY + $top, $right - $left + 1, $bottom - $top + 1)
            $frameSources += $frameBitmap
        }
        $maxWidth = ($boxes | Measure-Object Width -Maximum).Maximum
        $maxHeight = ($boxes | Measure-Object Height -Maximum).Maximum
        $targetHeight = if ($ActionSet -eq 'Walk') { 178 } elseif ($row -eq 0) { 214 } else { 196 }
        $scale = [Math]::Min(238 / $maxWidth, $targetHeight / $maxHeight)
        $sheet = [System.Drawing.Bitmap]::new(1024, 247, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($sheet)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            for ($column = 0; $column -lt 4; $column++) {
                $box = $boxes[$column]
                if ($ActionSet -eq 'Walk') { $scale = [Math]::Min(238 / $box.Width, $targetHeight / $box.Height) }
                $width = [single]($box.Width * $scale); $height = [single]($box.Height * $scale)
                $destination = [System.Drawing.RectangleF]::new([single]($column * 256 + (256 - $width) / 2), [single](241 - $height), $width, $height)
                $sourceRect = [System.Drawing.RectangleF]::new($box.X, $box.Y, $box.Width, $box.Height)
                $graphics.DrawImage($frameSources[$column], $destination, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
            }
            $state = if ($ActionSet -eq 'Walk') { 'walk' } elseif ($row -eq 0) { 'idle' } else { 'work' }
            $sheet.Save((Join-Path $OutputDirectory "neko_$state.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            if ($ActionSet -eq 'IdleWork' -and $row -eq 0) {
                $thumbnail = [System.Drawing.Bitmap]::new(96, 96, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $thumbGraphics = [System.Drawing.Graphics]::FromImage($thumbnail)
                try {
                    $thumbGraphics.Clear([System.Drawing.Color]::Transparent)
                    $thumbGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $thumbGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $box = $boxes[0]; $thumbScale = 84 / [Math]::Max($box.Width, $box.Height)
                    $width = [single]($box.Width * $thumbScale); $height = [single]($box.Height * $thumbScale)
                    $destination = [System.Drawing.RectangleF]::new([single]((96 - $width) / 2), [single](90 - $height), $width, $height)
                    $sourceRect = [System.Drawing.RectangleF]::new($box.X, $box.Y, $box.Width, $box.Height)
                    $thumbGraphics.DrawImage($sourceBitmap, $destination, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
                    $thumbnail.Save((Join-Path $OutputDirectory 'thumb.png'), [System.Drawing.Imaging.ImageFormat]::Png)
                }
                finally { $thumbGraphics.Dispose(); $thumbnail.Dispose() }
            }
            Write-Output "Packed $state`: 4 x 256x247, alpha preserved"
        }
        finally { $graphics.Dispose(); $sheet.Dispose() }
    }
}
finally { $sourceBitmap.Dispose(); if ($contactBitmap) { $contactBitmap.Dispose() } }
