<#
.SYNOPSIS
  Starts ULTRAKILL as a bridge guest: BepInEx is enabled through Doorstop command-line arguments for this launch only,
  and both sides are pointed at the same bridge folder.
.DESCRIPTION
  Needs Install-Guest.ps1 to have been run once (winhttp.dll + a disabled doorstop_config.ini in the game folder) and
  dist\guest to exist (Build.ps1 or an extracted release zip). The host game must be started by its own launcher with
  the same bridge folder (UKBRIDGE_DIR / ERMC_DIR); this script does not start any host (see Run-FakeHost.ps1 for the
  test host).
  It refuses to start a second guest, and clears a stale control block left by a guest that was killed (the host
  compositor has no timeout, so a dead guest's last frame would otherwise stay on screen).
.PARAMETER BridgeDir
  The shared bridge folder (default <repo>\runtime). Exported as UKBRIDGE_DIR and ERMC_DIR to ULTRAKILL.
.PARAMETER UltrakillDir
  The folder containing ULTRAKILL.exe (default: the one recorded by Install-Guest.ps1, else the Steam path).
.PARAMETER GuestDir
  The staged guest (default dist\guest).
.PARAMETER AttachTimeoutSeconds
  How long to wait for the guest to attach to bridge.shm (default 60).
#>
param(
    [string]$BridgeDir,
    [string]$UltrakillDir,
    [string]$GuestDir,
    [int]$AttachTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')

if (-not $BridgeDir) { $BridgeDir = $script:DefaultBridgeDir }
$BridgeDir = Resolve-Dir $BridgeDir
if (-not $GuestDir) { $GuestDir = $script:GuestStage }
$GuestDir = Resolve-Dir $GuestDir
if (-not $UltrakillDir) {
    $RecordPath = Join-Path $script:DefaultBridgeDir 'guest-install.json'
    if (Test-Path -LiteralPath $RecordPath) {
        try { $UltrakillDir = [string]((Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json).game_dir) } catch { }
    }
    if (-not $UltrakillDir) { $UltrakillDir = $script:DefaultUltrakillDir }
}
$UkExe = Join-Path $UltrakillDir 'ULTRAKILL.exe'
$Preloader = Join-Path $GuestDir 'BepInEx\core\BepInEx.Preloader.dll'
$Plugin = Join-Path $GuestDir 'BepInEx\plugins\UltrakillBridge\UltrakillBridge.Guest.dll'
if (-not (Test-Path -LiteralPath $UkExe)) { throw "ULTRAKILL was not found: $UkExe (pass -UltrakillDir)." }
if (-not (Test-Path -LiteralPath $Preloader) -or -not (Test-Path -LiteralPath $Plugin)) {
    throw "The staged guest is incomplete ($GuestDir). Run scripts\Build.ps1 or extract a release zip there."
}
if (-not (Test-Path -LiteralPath (Join-Path $UltrakillDir 'winhttp.dll'))) {
    throw 'winhttp.dll is not in the ULTRAKILL folder. Run scripts\Install-Guest.ps1 first.'
}

# Both names, one folder: any host (the Elden Ring DLL reads ERMC_DIR only) finds the same files.
New-Item -ItemType Directory -Path $BridgeDir -Force | Out-Null
Set-BridgeEnvironment $BridgeDir
$SharedFile = Join-Path $BridgeDir 'bridge.shm'

# ---------------------------------------------------------------- guard against a second guest
$State = Read-BridgeHeader $SharedFile
if ($State -and $State.GuestProcessId -gt 0) {
    $Guest = Get-Process -Id $State.GuestProcessId -ErrorAction SilentlyContinue
    if ($Guest -and $Guest.ProcessName -eq 'ULTRAKILL') {
        $StartedMs = [DateTimeOffset]::new($Guest.StartTime).ToUnixTimeMilliseconds()
        if ([Math]::Abs([double]$StartedMs - [double]$State.GuestStartMs) -lt 120000) {
            Write-Output "ULTRAKILL is already attached to the bridge (PID $($Guest.Id)). Use F8 to switch control."
            return
        }
    }
}
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) {
    throw 'ULTRAKILL is already running without the bridge. Close it first (BepInEx can only be enabled at startup).'
}

# ---------------------------------------------------------------- stale control block
if ($State -and $State.ControlFlags -ne 0) {
    $OldGuest = $null
    if ($State.GuestProcessId -gt 0) { $OldGuest = Get-Process -Id $State.GuestProcessId -ErrorAction SilentlyContinue }
    if (-not $OldGuest) {
        if (Clear-BridgeControl $SharedFile) { Write-Output ('Cleared a stale bridge control block left by a dead guest (PID {0}, flags 0x{1:X}).' -f $State.GuestProcessId, $State.ControlFlags) }
    }
}

# ---------------------------------------------------------------- host hint
$HostAlive = $false
if ($State -and $State.HostProcessId -gt 0 -and (Get-Process -Id $State.HostProcessId -ErrorAction SilentlyContinue)) { $HostAlive = $true }
if (-not $HostAlive) {
    Write-Warning 'No running host found in bridge.shm. Start the host game (or scripts\Run-FakeHost.ps1) with the same bridge folder; the guest waits for it.'
}

# ---------------------------------------------------------------- ULTRAKILL with BepInEx
# doorstop_config.ini in the game folder says enabled=false; the command line switches BepInEx on for this launch only.
# -screen-fullscreen 0 -popupwindow: windowed and borderless, which the input window glued over the host needs.
$UkArgs = '--doorstop-enabled true --doorstop-target-assembly "' + $Preloader + '" -screen-fullscreen 0 -popupwindow'
Write-Output 'Starting ULTRAKILL with BepInEx...'
$UkProcess = Start-Process -FilePath $UkExe -ArgumentList $UkArgs -WorkingDirectory $UltrakillDir -PassThru

$Watch = [Diagnostics.Stopwatch]::StartNew()
$Attached = $false
while ($Watch.Elapsed.TotalSeconds -lt $AttachTimeoutSeconds) {
    $State = Read-BridgeHeader $SharedFile
    if ($State -and $State.GuestProcessId -eq $UkProcess.Id) { $Attached = $true; break }
    if (-not (Get-Process -Id $UkProcess.Id -ErrorAction SilentlyContinue)) {
        throw "ULTRAKILL exited during startup. See $GuestDir\BepInEx\LogOutput.log and the Unity player log."
    }
    Start-Sleep -Milliseconds 500
}

Write-Output ''
Write-Output "ULTRAKILL : PID $($UkProcess.Id) $(if ($Attached) { '(attached to the bridge)' } else { '(not attached yet; see ' + $GuestDir + '\BepInEx\LogOutput.log)' })"
Write-Output "Bridge dir: $BridgeDir"
Write-Output "F8 hands control to the host and back. Log: $GuestDir\BepInEx\LogOutput.log"
