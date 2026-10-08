<#
.SYNOPSIS
  Builds the ULTRAKILL guest (BepInEx plugin) and stages a ready-to-use guest runtime in dist\guest. Touches no game
  folder and starts no game.
.DESCRIPTION
  1. Downloads BepInEx 5.4.23.5 x64 into .tools\ if it is missing (SHA-256 verified).
  2. dotnet build of the guest project (needs ULTRAKILL's Managed DLLs, read from the game folder; see -UltrakillDir).
  3. Stages dist\guest\:
       winhttp.dll                         BepInEx's Doorstop proxy
       doorstop_config.ini                 enabled=false (a normal Steam launch stays vanilla)
       BepInEx\core\...                    BepInEx
       BepInEx\config\BepInEx.cfg          HideManagerGameObject = true (required, see docs\guest-reference.md)
       BepInEx\plugins\UltrakillBridge\    UltrakillBridge.Guest.dll + UltrakillBridge.Protocol.dll
  4. With -Package, also writes dist\UltrakillBridge-Guest-<version>.zip (the release artifact).
.PARAMETER Configuration
  dotnet build configuration (default Release).
.PARAMETER UltrakillDir
  The folder containing ULTRAKILL.exe (default: the Steam path). Passed to MSBuild as -p:UltrakillDir.
.PARAMETER Package
  Also zip dist\guest as dist\UltrakillBridge-Guest-<version>.zip.
.PARAMETER SkipBuild
  Do not run dotnet build; only stage what is already built.
.PARAMETER Solution
  Build the whole solution (guest, protocol, host SDK, fake host, tests) instead of the guest project only.
#>
param(
    [string]$Configuration = 'Release',
    [string]$UltrakillDir = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL',
    [switch]$Package,
    [switch]$SkipBuild,
    [switch]$Solution
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Root = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root '.tools'

# Pinned BepInEx
$BepVersion = '5.4.23.5'
$BepName = "BepInEx_win_x64_$BepVersion"
$BepUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepVersion/$BepName.zip"
$BepSha = '82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4'

function Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Get-Download([string]$Url, [string]$Sha256, [string]$Dest) {
    if (Test-Path -LiteralPath $Dest) {
        if ((Get-FileHash -LiteralPath $Dest -Algorithm SHA256).Hash -ieq $Sha256) { return }
        Remove-Item -LiteralPath $Dest -Force
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Dest) -Force | Out-Null
    Write-Host "    downloading $Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try { Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing }
    catch { throw "Could not download $Url ($($_.Exception.Message)). Download it manually to '$Dest' and run again." }
    $Actual = (Get-FileHash -LiteralPath $Dest -Algorithm SHA256).Hash
    if ($Actual -ine $Sha256) {
        Remove-Item -LiteralPath $Dest -Force
        throw "SHA-256 mismatch for $Url (expected $Sha256, got $Actual)."
    }
}

# ---------------------------------------------------------------- 1. BepInEx
Step "BepInEx $BepVersion"
$BepDir = Join-Path $Tools 'bepinex5-x64'
if (-not (Test-Path -LiteralPath (Join-Path $BepDir 'BepInEx\core\BepInEx.Preloader.dll'))) {
    $Zip = Join-Path $Tools "downloads\bepinex5\$BepName.zip"
    Get-Download $BepUrl $BepSha $Zip
    New-Item -ItemType Directory -Path $BepDir -Force | Out-Null
    Write-Host '    extracting'
    Expand-Archive -LiteralPath $Zip -DestinationPath $BepDir -Force
    if (-not (Test-Path -LiteralPath (Join-Path $BepDir 'BepInEx\core\BepInEx.Preloader.dll'))) { throw "BepInEx.Preloader.dll not found after extracting $Zip." }
}

# ---------------------------------------------------------------- 2. managed build
$GuestProject = Join-Path $Root 'guest\UltrakillBridge.Guest\UltrakillBridge.Guest.csproj'
if (-not $SkipBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) was not found in PATH.' }
    $Managed = Join-Path $UltrakillDir 'ULTRAKILL_Data\Managed\Assembly-CSharp.dll'
    if (-not (Test-Path -LiteralPath $Managed)) {
        throw "ULTRAKILL's Managed DLLs were not found ($Managed). Pass -UltrakillDir <folder containing ULTRAKILL.exe>."
    }
    $Target = if ($Solution) { Join-Path $Root 'UltrakillBridge.sln' } else { $GuestProject }
    Step "dotnet build $(Split-Path -Leaf $Target) -c $Configuration"
    & dotnet build $Target -c $Configuration --nologo -v:minimal "-p:UltrakillDir=$UltrakillDir"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}

# ---------------------------------------------------------------- 3. stage dist\guest
Step 'Staging dist\guest'
$Stage = Join-Path $Root 'dist\guest'
$Out = Join-Path $Root "guest\UltrakillBridge.Guest\bin\$Configuration\netstandard2.1"
$Dlls = 'UltrakillBridge.Guest.dll', 'UltrakillBridge.Protocol.dll'
foreach ($Name in $Dlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $Out $Name))) { throw "Missing $Out\$Name (build first, or do not use -SkipBuild)." }
}
# Refresh the binaries only: keep the user's BepInEx\config and the logs from previous sessions.
foreach ($Sub in 'BepInEx\core', 'BepInEx\plugins') {
    $Old = Join-Path $Stage $Sub
    if (Test-Path -LiteralPath $Old) { Remove-Item -LiteralPath $Old -Recurse -Force }
}
$StageCore = Join-Path $Stage 'BepInEx\core'
$StagePlugin = Join-Path $Stage 'BepInEx\plugins\UltrakillBridge'
New-Item -ItemType Directory -Path $StageCore, $StagePlugin, (Join-Path $Stage 'BepInEx\config') -Force | Out-Null
Copy-Item -Path (Join-Path $BepDir 'BepInEx\core\*') -Destination $StageCore -Recurse -Force
Copy-Item -LiteralPath (Join-Path $BepDir 'winhttp.dll') -Destination (Join-Path $Stage 'winhttp.dll') -Force
foreach ($Name in $Dlls) { Copy-Item -LiteralPath (Join-Path $Out $Name) -Destination $StagePlugin -Force }
$Pdb = Join-Path $Out 'UltrakillBridge.Guest.pdb'
if (Test-Path -LiteralPath $Pdb) { Copy-Item -LiteralPath $Pdb -Destination $StagePlugin -Force }

# Doorstop settings: BepInEx is OFF for normal launches. Launch-Guest.ps1 switches it on on the command line.
$Ini = @(
    '# ULTRAKILL Crossover Bridge: BepInEx is OFF for normal launches. Launch-Guest.ps1 enables it on the command line.',
    '[General]',
    'enabled = false',
    'target_assembly = BepInEx\core\BepInEx.Preloader.dll',
    'redirect_output_log = false',
    'boot_config_override =',
    'ignore_disable_switch = false',
    '',
    '[UnityMono]',
    'dll_search_path_override =',
    'debug_enabled = false',
    'debug_address = 127.0.0.1:10000',
    'debug_suspend = false'
)
Set-Content -LiteralPath (Join-Path $Stage 'doorstop_config.ini') -Value $Ini -Encoding ASCII

# ULTRAKILL modding convention: hide the BepInEx manager object, otherwise objects created by plugins before the
# first scene do not survive it.
[IO.File]::WriteAllText((Join-Path $Stage 'BepInEx\config\BepInEx.cfg'), "[Chainloader]`r`n`r`nHideManagerGameObject = true`r`n")

# ---------------------------------------------------------------- 4. package
$Version = '0.0.0'
$PluginSource = Join-Path $Root 'guest\UltrakillBridge.Guest\Plugin.cs'
$M = [regex]::Match([IO.File]::ReadAllText($PluginSource), 'Version\s*=\s*"([^"]+)"')
if ($M.Success) { $Version = $M.Groups[1].Value }
if ($Package) {
    Step 'Packaging'
    $ZipPath = Join-Path $Root "dist\UltrakillBridge-Guest-$Version.zip"
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $ZipPath
    Write-Host "    $ZipPath"
}

Write-Host ''
Write-Host "Build finished (guest $Version)." -ForegroundColor Green
Write-Host "  staged guest : $Stage"
Write-Host 'Next: .\scripts\Install-Guest.ps1, then .\scripts\Launch-Guest.ps1 (or .\scripts\Run-FakeHost.ps1 -WithGuest to test without a host game).'
