<#
.SYNOPSIS
    Writes assets\netroute.ico: the NetRoute mark (two routes on a dark disc) at every size
    Windows asks for. Same drawing as the tray icon in App.xaml.cs. Run again after changing it.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$path = Join-Path $root 'assets\netroute.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $s = $size / 32.0
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $tile = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x1F, 0x23, 0x2C))
    $g.FillEllipse($tile, [single](1 * $s), [single](1 * $s), [single](30 * $s), [single](30 * $s))
    foreach ($route in @(
            @{ Color = [System.Drawing.Color]::FromArgb(0x22, 0xC5, 0x5E); Points = 8, 24, 12, 24, 14, 10, 24, 9 },
            @{ Color = [System.Drawing.Color]::FromArgb(0x4F, 0x8D, 0xF7); Points = 8, 24, 16, 24, 18, 22, 24, 23 })) {
        $pen = New-Object System.Drawing.Pen $route.Color, ([single](3.2 * $s))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $p = $route.Points | ForEach-Object { [single]($_ * $s) }
        $g.DrawBezier($pen, $p[0], $p[1], $p[2], $p[3], $p[4], $p[5], $p[6], $p[7])
        $pen.Dispose()
    }
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

# ICO container: header, one 16-byte entry per image, then the PNG data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write([byte[]]$image) }
$w.Flush()
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
Write-Host "Wrote $path ($($out.Length) bytes)"
