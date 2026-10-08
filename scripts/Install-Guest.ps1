<#
.SYNOPSIS
  Installs the ULTRAKILL guest hook: copies BepInEx's winhttp.dll (Doorstop proxy) and a doorstop_config.ini with
  enabled=false next to ULTRAKILL.exe. Run Build.ps1 first (or extract a release zip and pass -GuestDir).
.DESCRIPTION
  A normal Steam launch of ULTRAKILL stays vanilla: Doorstop is disabled in the ini. Launch-Guest.ps1 enables BepInEx
  for its own launch only, with
      --doorstop-enabled true --doorstop-target-assembly <guest dir>\BepInEx\core\BepInEx.Preloader.dll
  so no BepInEx folder, plugin or config is ever copied into the game folder.
  An existing winhttp.dll / doorstop_config.ini is backed up to runtime\backups\<timestamp> first. What was done is
  recorded in runtime\guest-install.json (used by Uninstall-Guest.ps1). Running it again only refreshes the two files.
.PARAMETER UltrakillDir
  The folder containing ULTRAKILL.exe (default: the Steam path).
.PARAMETER GuestDir
  The staged guest (default dist\guest).
.PARAMETER SteamAppId
  Also write steam_appid.txt (1229490) next to ULTRAKILL.exe. Off by default: ULTRAKILL never calls
  RestartAppIfNecessary, so starting ULTRAKILL.exe directly (Steam running) works without it. Use it only if the
  launched ULTRAKILL exits at once or "restarts through Steam" (which would lose the BepInEx arguments).
#>
param(
    [string]$UltrakillDir = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL',
    [string]$GuestDir,
    [switch]$SteamAppId
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')
if (-not $GuestDir) { $GuestDir = $script:GuestStage }
$GuestDir = Resolve-Dir $GuestDir
$UltrakillDir = Resolve-Dir $UltrakillDir
$RuntimeDir = $script:DefaultBridgeDir
$RecordPath = Join-Path $RuntimeDir 'guest-install.json'

if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { throw 'Close ULTRAKILL before installing.' }
if (-not (Test-Path -LiteralPath "$UltrakillDir\ULTRAKILL.exe")) { throw "ULTRAKILL.exe was not found in '$UltrakillDir'. Pass -UltrakillDir." }
foreach ($Name in 'winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.Preloader.dll') {
    if (-not (Test-Path -LiteralPath "$GuestDir\$Name")) { throw "The staged guest is incomplete (missing $GuestDir\$Name). Run scripts\Build.ps1." }
}

$Prev = $null
if (Test-Path -LiteralPath $RecordPath) {
    try { $Prev = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json } catch { $Prev = $null }
}
if ($Prev -and ([string]$Prev.game_dir) -ine $UltrakillDir) { $Prev = $null }

if ($Prev) {
    $Backup = [string]$Prev.backup
    $BackedUp = @($Prev.backed_up)
    $CreatedAppId = [bool]$Prev.created_steam_appid
    Write-Output 'ULTRAKILL: already installed here, refreshing the two files.'
} else {
    $Backup = Join-Path $RuntimeDir ('backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '\ultrakill')
    $BackedUp = @()
    $CreatedAppId = $false
    foreach ($Name in 'winhttp.dll', 'doorstop_config.ini') {
        $Candidate = [IO.Path]::GetFullPath((Join-Path $UltrakillDir $Name))
        Assert-Inside $UltrakillDir $Candidate
        if (Test-Path -LiteralPath $Candidate) {
            New-Item -ItemType Directory -Path $Backup -Force | Out-Null
            Move-Item -LiteralPath $Candidate -Destination (Join-Path $Backup $Name)
            $BackedUp += $Name
        }
    }
}

Copy-Item -LiteralPath "$GuestDir\winhttp.dll" -Destination "$UltrakillDir\winhttp.dll" -Force
Copy-Item -LiteralPath "$GuestDir\doorstop_config.ini" -Destination "$UltrakillDir\doorstop_config.ini" -Force
if ($SteamAppId) {
    $AppIdPath = "$UltrakillDir\steam_appid.txt"
    if (Test-Path -LiteralPath $AppIdPath) {
        Write-Output 'steam_appid.txt already exists; left alone.'
    } else {
        '1229490' | Set-Content -LiteralPath $AppIdPath -Encoding ASCII
        $CreatedAppId = $true
    }
}

New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null
$Record = [ordered]@{
    installed_at = (Get-Date).ToString('o')
    game_dir = $UltrakillDir
    guest_dir = $GuestDir
    backup = $Backup
    backed_up = $BackedUp
    created_steam_appid = $CreatedAppId
}
($Record | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $RecordPath -Encoding UTF8
Write-Output "ULTRAKILL: installed (BepInEx disabled by default; Launch-Guest.ps1 enables it). Backed up: $($BackedUp.Count) file(s)."
