# Builds LiveWallpaper.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or Visual Studio needed.  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'
$wpf  = Join-Path $fw 'WPF'
$out  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

# ---- app icon (PNG-compressed .ico, drawn here so the repo has no binary files)
Add-Type -AssemblyName System.Drawing
function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($size * 0.22)
    $w = [single]($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 88, 86, 214)), ([System.Drawing.Color]::FromArgb(255, 236, 72, 153))
    $g.FillPath($brush, $path)
    $s = [single]$size
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.28)),
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.72)),
        (New-Object System.Drawing.PointF ($s * 0.74), ($s * 0.50)))
    $g.FillPolygon([System.Drawing.Brushes]::White, $tri)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($sz in $sizes) { ,(New-IconPng $sz) }
$ico = Join-Path $out 'app.ico'
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

# ---- compile
$refs = @(
    "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll",
    "$fw\System.dll", "$fw\System.Core.dll"
) | ForEach-Object { "/r:$_" }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $out 'LiveWallpaper.exe'

& $csc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 `
    "/out:$exe" "/win32manifest:$(Join-Path $root 'app.manifest')" "/win32icon:$ico" `
    "/resource:$ico,LiveWallpaper.app.ico" @refs @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'app.config') (Join-Path $out 'LiveWallpaper.exe.config') -Force
Write-Host "Built $exe"
