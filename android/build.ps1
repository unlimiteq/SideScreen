# Builds android\build\SideScreen.apk without Gradle: aapt2 -> javac -> d8 -> zipalign -> apksigner
$ErrorActionPreference = 'Continue'   # native tools write notes to stderr; exit codes are checked instead
$here = $PSScriptRoot
$root = Split-Path $here -Parent
# Android SDK: the project-local copy (tools\android-sdk), else ANDROID_HOME / ANDROID_SDK_ROOT
$sdk = Join-Path $root 'tools\android-sdk'
if (-not (Test-Path (Join-Path $sdk 'build-tools'))) { $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT } }
if (-not $sdk -or -not (Test-Path (Join-Path $sdk 'build-tools'))) { throw 'Android SDK not found (tools\android-sdk or ANDROID_HOME)' }
# newest installed build-tools (34+) and platform (34+)
$bt = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and [version]$_.Name -ge [version]'34.0.0' } |
      Sort-Object { [version]$_.Name } | Select-Object -Last 1 -ExpandProperty FullName
$platform = Get-ChildItem (Join-Path $sdk 'platforms') -Directory | Where-Object { $_.Name -match '^android-(\d+)$' -and [int]$Matches[1] -ge 34 } |
      Sort-Object { [int]($_.Name -replace 'android-', '') } | Select-Object -Last 1 -ExpandProperty FullName
if (-not $bt -or -not $platform) { throw 'Android build-tools 34+ and platform 34+ are required' }
$androidJar = Join-Path $platform 'android.jar'
# JDK 17+: JAVA_HOME, else the newest JDK under Program Files\Java
$jdkHome = $env:JAVA_HOME
if (-not $jdkHome -or -not (Test-Path (Join-Path $jdkHome 'bin\javac.exe'))) {
    $jdkHome = Get-ChildItem 'C:\Program Files\Java', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Microsoft' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^jdk-?(\d+)' -and [int]$Matches[1] -ge 17 -and (Test-Path (Join-Path $_.FullName 'bin\javac.exe')) } |
        Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $jdkHome) { throw 'JDK 17+ not found (set JAVA_HOME)' }
$java = Join-Path $jdkHome 'bin\java.exe'
$javac = Join-Path $jdkHome 'bin\javac.exe'
$keytool = Join-Path $jdkHome 'bin\keytool.exe'

function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed" } }

$out = Join-Path $here 'build'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force "$out\classes", "$out\dex" | Out-Null

& "$bt\aapt2.exe" compile --dir "$here\res" -o "$out\res.zip"; Check 'aapt2 compile'
& "$bt\aapt2.exe" link -o "$out\unaligned.apk" -I $androidJar --manifest "$here\AndroidManifest.xml" `
    --min-sdk-version 30 --target-sdk-version 35 --version-code 1 --version-name 1.0.0 "$out\res.zip"; Check 'aapt2 link'

$sources = Get-ChildItem "$here\src" -Recurse -Filter *.java | ForEach-Object FullName
& $javac -nowarn -source 11 -target 11 -Xlint:-options -classpath $androidJar -d "$out\classes" $sources; Check 'javac'

$classes = Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName
& $java -cp "$bt\lib\d8.jar" com.android.tools.r8.D8 --release --lib $androidJar --min-api 30 --output "$out\dex" $classes; Check 'd8'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open("$out\unaligned.apk", 'Update')
[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$out\dex\classes.dex", 'classes.dex') | Out-Null
$zip.Dispose()

& "$bt\zipalign.exe" -f -P 16 4 "$out\unaligned.apk" "$out\aligned.apk"; Check 'zipalign'

# local, self-generated signing key (kept out of the build folder so updates install over each other)
$ks = Join-Path $here 'sidescreen.keystore'
if (-not (Test-Path $ks)) {
    & $keytool -genkeypair -keystore $ks -storepass android -keypass android -alias sidescreen `
        -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=SideScreen' | Out-Null; Check 'keytool'
}
& $java -jar "$bt\lib\apksigner.jar" sign --ks $ks --ks-pass pass:android --key-pass pass:android `
    --out "$out\SideScreen.apk" "$out\aligned.apk"; Check 'apksigner'

Write-Host "Built $out\SideScreen.apk"
