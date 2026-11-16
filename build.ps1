# Builds everything into .\dist : SideScreen.exe + sscapture.exe + SideScreen.apk + adb + driver files
#   .\build.ps1            build into .\dist
#   .\build.ps1 -Package   also create .\release\SideScreen-v<version>-win-x64.zip (+ .sha256)
param([switch]$Package)
$ErrorActionPreference = 'Continue'   # native tools write notes to stderr; exit codes are checked instead
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# --- app icon (generated, so there is no binary asset to keep in the repo) ---
$ico = Join-Path $root 'host\app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap 256, 256
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    function RoundRect($x, $y, $w, $h, $r) {
        $p = New-Object Drawing.Drawing2D.GraphicsPath
        $d = $r * 2
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); $p
    }
    $dark = [Drawing.Color]::FromArgb(0x14, 0x16, 0x1B)
    $grey = [Drawing.Color]::FromArgb(0x3A, 0x40, 0x4E)
    $blue = [Drawing.Color]::FromArgb(0x4C, 0x8D, 0xFF)
    $g.FillPath((New-Object Drawing.SolidBrush $dark), (RoundRect 8 8 240 240 52))
    $g.FillPath((New-Object Drawing.SolidBrush $grey), (RoundRect 36 62 130 92 14))
    $g.FillPath((New-Object Drawing.SolidBrush $blue), (RoundRect 84 100 136 96 14))
    $g.FillPath((New-Object Drawing.SolidBrush $dark), (RoundRect 95 111 114 74 5))
    $g.FillPolygon((New-Object Drawing.SolidBrush $blue), [Drawing.PointF[]]@(
        (New-Object Drawing.PointF 140, 130), (New-Object Drawing.PointF 172, 148), (New-Object Drawing.PointF 140, 166)))
    $g.Dispose()
    $png = New-Object IO.MemoryStream
    $bmp.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $bytes = $png.ToArray()
    $fs = [IO.File]::Create($ico)
    $w = New-Object IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)       # ICONDIR
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$bytes.Length); $w.Write([uint32]22)
    $w.Write($bytes); $w.Dispose()
}

# --- tablet app ---
& (Join-Path $root 'android\build.ps1')

# --- native capture/encode helper ---
cmd /c "`"$(Join-Path $root 'capture\build.cmd')`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'sscapture build failed' }

# --- Windows host ---
Get-Process SideScreen -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish (Join-Path $root 'host\SideScreen.csproj') -c Release -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item (Join-Path $root 'android\build\SideScreen.apk'), (Join-Path $root 'capture\build\sscapture.exe') $dist -Force
New-Item -ItemType Directory -Force "$dist\adb", "$dist\driver" | Out-Null
$pt = Join-Path $root 'tools\android-sdk\platform-tools'
if (-not (Test-Path "$pt\adb.exe") -and $env:ANDROID_HOME) { $pt = Join-Path $env:ANDROID_HOME 'platform-tools' }
# adb.exe may be in use (its server keeps running between sessions); the existing copy is fine then
Copy-Item "$pt\adb.exe", "$pt\AdbWinApi.dll", "$pt\AdbWinUsbApi.dll" "$dist\adb" -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'driver\*') "$dist\driver" -Force

Write-Host "`nDone -> $dist\SideScreen.exe"

if ($Package) {
    [xml]$proj = Get-Content (Join-Path $root 'host\SideScreen.csproj')
    $version = @($proj.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    $relDir = Join-Path $root 'release'
    $stage = Join-Path $relDir "SideScreen-v$version"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item "$dist\SideScreen.exe", "$dist\sscapture.exe", "$dist\SideScreen.apk" $stage
    Copy-Item "$dist\adb", "$dist\driver" $stage -Recurse
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') $stage
    @"
SideScreen v$version - use an Android tablet as a second Windows monitor over USB

1. Enable USB debugging on the tablet and plug it in.
2. Run SideScreen.exe (needs the .NET 10 Desktop Runtime; Windows offers the download if missing).
3. Click "Install virtual display driver" once (admin prompt), then press Start.

Full guide: https://github.com/unlimiteq/SideScreen
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding utf8
    $zip = Join-Path $relDir "SideScreen-v$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Build the zip by hand: Compress-Archive in Windows PowerShell writes "\" separators, which non-Windows unzip tools mishandle
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        Get-ChildItem $stage -Recurse -File | ForEach-Object {
            $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, 'Optimal') | Out-Null
        }
    } finally { $archive.Dispose() }
    Remove-Item $stage -Recurse -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    # LF line ending so "sha256sum -c" works on Linux / macOS / Git Bash
    [IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path $zip -Leaf)`n", (New-Object Text.ASCIIEncoding))
    Write-Host "Packaged -> $zip"
}
