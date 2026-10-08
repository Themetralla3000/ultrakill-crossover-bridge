<#
.SYNOPSIS
  Undoes Install-Guest.ps1: removes winhttp.dll and doorstop_config.ini from the ULTRAKILL folder and puts back
  whatever Install-Guest.ps1 moved to runtime\backups. Nothing else in the game folder is touched.
.PARAMETER UltrakillDir
  Only needed if runtime\guest-install.json is missing.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$UltrakillDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')
$RecordPath = Join-Path $script:DefaultBridgeDir 'guest-install.json'
$Record = $null
if (Test-Path -LiteralPath $RecordPath) { $Record = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json }
if (-not $UltrakillDir) {
    if (-not $Record) { throw 'runtime\guest-install.json was not found; pass -UltrakillDir.' }
    $UltrakillDir = [string]$Record.game_dir
}
$UltrakillDir = Resolve-Dir $UltrakillDir
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { throw 'Close ULTRAKILL first (scripts\Stop-Guest.ps1).' }
if (-not (Test-Path -LiteralPath "$UltrakillDir\ULTRAKILL.exe")) { throw "ULTRAKILL.exe was not found in '$UltrakillDir'." }

foreach ($Name in 'winhttp.dll', 'doorstop_config.ini') {
    $Target = [IO.Path]::GetFullPath((Join-Path $UltrakillDir $Name))
    Assert-Inside $UltrakillDir $Target
    if ((Test-Path -LiteralPath $Target) -and $PSCmdlet.ShouldProcess($Target, 'remove')) {
        Remove-Item -LiteralPath $Target -Force
        Write-Output "  removed: $Name"
    }
}
if ($Record) {
    foreach ($Name in @($Record.backed_up)) {
        $Source = Join-Path ([string]$Record.backup) $Name
        $Target = [IO.Path]::GetFullPath((Join-Path $UltrakillDir $Name))
        Assert-Inside $UltrakillDir $Target
        if (-not (Test-Path -LiteralPath $Source)) { Write-Warning "  backup not found, cannot restore: $Source"; continue }
        if ($PSCmdlet.ShouldProcess($Target, 'restore from backup')) {
            Copy-Item -LiteralPath $Source -Destination $Target
            Write-Output "  restored: $Name"
        }
    }
    if ($Record.created_steam_appid) {
        $AppId = Join-Path $UltrakillDir 'steam_appid.txt'
        if ((Test-Path -LiteralPath $AppId) -and $PSCmdlet.ShouldProcess($AppId, 'remove')) { Remove-Item -LiteralPath $AppId -Force; Write-Output '  removed: steam_appid.txt' }
    }
    if ($PSCmdlet.ShouldProcess($RecordPath, 'remove')) { Remove-Item -LiteralPath $RecordPath -Force }
}
Write-Output 'Done. ULTRAKILL is back to vanilla.'
