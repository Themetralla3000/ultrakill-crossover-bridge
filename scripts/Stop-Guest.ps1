<#
.SYNOPSIS
  Stops the ULTRAKILL guest and releases the host: closes ULTRAKILL (gracefully first, then forced) and clears the
  bridge control block so the host stops compositing the guest's last frame and gives its camera/character back.
.PARAMETER BridgeDir
  The shared bridge folder (default <repo>\runtime).
.PARAMETER All
  Also close the fake host and every process named in -HostProcessName, gracefully. Never forced: if a host does not
  close, close it yourself.
.PARAMETER HostProcessName
  Extra host process names to close with -All (for example 'eldenring').
.PARAMETER GraceSeconds
  How long to wait for ULTRAKILL to close by itself before it is killed (default 5).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BridgeDir,
    [switch]$All,
    [string[]]$HostProcessName = @(),
    [int]$GraceSeconds = 5
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')
if (-not $BridgeDir) { $BridgeDir = $script:DefaultBridgeDir }
$SharedFile = Join-Path (Resolve-Dir $BridgeDir) 'bridge.shm'

# ---- ULTRAKILL
$Uk = @(Get-Process ULTRAKILL -ErrorAction SilentlyContinue)
if ($Uk.Count -eq 0) { Write-Output 'ULTRAKILL: not running.' }
foreach ($P in $Uk) {
    if (-not $PSCmdlet.ShouldProcess("ULTRAKILL (PID $($P.Id))", 'close')) { continue }
    Write-Output "ULTRAKILL (PID $($P.Id)): asking it to close..."
    if (Close-ProcessGracefully $P $GraceSeconds) { Write-Output 'ULTRAKILL: closed.'; continue }
    Write-Output "ULTRAKILL: still running after $GraceSeconds s, killing it."
    try { Stop-Process -Id $P.Id -Force -ErrorAction Stop } catch { Write-Warning "Could not kill PID $($P.Id): $($_.Exception.Message)" }
    if (-not $P.WaitForExit(5000)) { throw "ULTRAKILL (PID $($P.Id)) did not exit; the control block was left as it is." }
}

# ---- control block
$State = Read-BridgeHeader $SharedFile
if (-not $State) {
    Write-Output 'Bridge: no bridge.shm (or not initialised); nothing to clear.'
} elseif ($PSCmdlet.ShouldProcess($SharedFile, 'clear the control block')) {
    if (Clear-BridgeControl $SharedFile) {
        Write-Output ('Bridge: control block cleared (flags were 0x{0:X}). The host has its camera and character back.' -f $State.ControlFlags)
    } else {
        Write-Warning 'Bridge: could not clear the control block.'
    }
}

# ---- hosts
if ($All) {
    foreach ($Name in (@($script:FakeHostName) + $HostProcessName)) {
        foreach ($P in @(Get-Process $Name -ErrorAction SilentlyContinue)) {
            if (-not $PSCmdlet.ShouldProcess("$Name (PID $($P.Id))", 'close')) { continue }
            Write-Output "${Name} (PID $($P.Id)): asking it to close..."
            if (Close-ProcessGracefully $P 20) { Write-Output "${Name}: closed." }
            else { Write-Warning "$Name did not close by itself; close it manually (it is not forced)." }
        }
    }
}
Write-Output 'Done.'
