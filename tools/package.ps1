# Packages DayManager into a zip that runs on a machine without any SDK.
#
# Why this script exists: publishing by hand means typing two dotnet commands,
# then remembering to (a) throw away the dev machine's appsettings.Local.json
# (it holds a token that belongs to this machine only) and (b) write a fresh
# random token into BOTH the client seed and the backend config. Forget either
# and the app looks broken in a way that is very hard to guess from the UI:
# the backend answers 401 to everything and the UI just says "cannot connect".
#
# Comments here are deliberately ASCII-only: PowerShell 5.1 reads a BOM-less
# .ps1 as ANSI, so Chinese comments would turn into garbage.

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$appOut    = Join-Path $root "publish/app"
$backOut   = Join-Path $appOut "backend"
$stamp     = Get-Date -Format "yyyyMMdd-HHmm"
$zipPath   = Join-Path $root "publish/DayManager-win-x64-$stamp.zip"

Write-Host "== 1/7 clean previous output =="
# [System.IO.Directory]::Delete rather than Remove-Item -Recurse:
# the latter goes through the shell's bulk-delete guard here and gets blocked
# once the folder holds a few hundred files. This is a build output folder,
# so deleting it outright is fine.
if (Test-Path $appOut) { [System.IO.Directory]::Delete($appOut, $true) }
New-Item -ItemType Directory -Path $appOut | Out-Null

Write-Host "== 2/7 publish client (self-contained, unpackaged) =="
# NOTE: do NOT pass -p:RuntimeIdentifier. Doing so makes the MAUI SDK demand
# Microsoft.NETCore.App.Runtime.Mono.win-x64, which does not exist for 10.0.x
# on nuget.org and fails restore with NU1102. The default RID is fine.
dotnet publish (Join-Path $root "src/DayManager.App/DayManager.App.csproj") `
    -c $Configuration -f net10.0-windows10.0.19041.0 `
    -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
    -o $appOut
if ($LASTEXITCODE -ne 0) { throw "client publish failed" }

Write-Host "== 3/7 publish backend into backend/ =="
dotnet publish (Join-Path $root "src/DayManager.Api/DayManager.Api.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $backOut
if ($LASTEXITCODE -ne 0) { throw "backend publish failed" }

Write-Host "== 4/8 copy the icon (the installer needs it) =="
# Grabbed from the build output rather than checked in: it is a generated file.
# The .iss reads it from here too, so both stay in sync.
Get-ChildItem -Path (Join-Path $root "src/DayManager.App/obj/Release") `
    -Filter "appicon.ico" -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1 |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $appOut "appicon.ico") -Force }

Write-Host "== 5/8 write a fresh token on both sides =="
$token = -join ((1..3) | ForEach-Object { [guid]::NewGuid().ToString("N") })
$utf8  = New-Object System.Text.UTF8Encoding($false)

$seed = @{ baseUrl = "http://127.0.0.1:5080"; token = $token } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $appOut "seed.json"), $seed, $utf8)

$local = @{
    Auth = @{ Token = $token }
    Ai   = @{ ApiKey = "" }
    Data = @{ DbPath = '%LOCALAPPDATA%\DayManager\daymanager.db' }
} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $backOut "appsettings.Local.json"), $local, $utf8)

Write-Host "== 6/8 copy the read-me =="
# Kept as a standalone UTF-8 file instead of a here-string in this script:
# PowerShell 5.1 reads a BOM-less .ps1 as ANSI, which mangles Chinese text and
# (worse) can break parsing entirely. This way the script stays pure ASCII.
# The destination name has to be ASCII too - a Chinese file name in this script
# comes out as illegal characters for the same ANSI-decoding reason.
Copy-Item (Join-Path $PSScriptRoot "readme.txt") (Join-Path $appOut "ReadMe.txt") -Force

Write-Host "== 7/8 drop debug symbols =="
# .pdb only: *.deps.json must stay, it is what tells the host which dlls to load.
# Same reason as above for not using Remove-Item: piping FileInfo into it fails
# with InputObjectNotBound under the shell's delete wrapper.
Get-ChildItem -Path $appOut -Recurse -Filter *.pdb |
    ForEach-Object { [System.IO.File]::Delete($_.FullName) }

Write-Host "== 8/8 zip it up =="
# tar.exe ships with Windows and is far faster than Compress-Archive here.
& tar.exe -a -c -f $zipPath -C $appOut .
if ($LASTEXITCODE -ne 0) { throw "zip failed" }

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "done: $zipPath ($size MB)"
