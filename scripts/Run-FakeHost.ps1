<#
.SYNOPSIS
  Starts the fake host (a small WinForms program that speaks the bridge protocol) so you can test the guest, or
  develop your own host against it, without a real host game.
.DESCRIPTION
  Builds fake-host\UltrakillBridge.FakeHost (Release) if it is not built yet, starts it with --dir <BridgeDir>, and waits
  until it has published the bridge. With -WithGuest it then runs Launch-Guest.ps1 (needs Build.ps1 and
  Install-Guest.ps1 first).
.PARAMETER BridgeDir
  The shared bridge folder (default <repo>\runtime).
.PARAMETER WithGuest
  Also start ULTRAKILL as the guest.
.PARAMETER Spawn
  Spawn point "x,y,z" in host metres (default 0,0,0).
.PARAMETER Rebuild
  Rebuild the fake host even if it exists.
#>
param(
    [string]$BridgeDir,
    [switch]$WithGuest,
    [string]$Spawn,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeCommon.ps1')

if (-not $BridgeDir) { $BridgeDir = $script:DefaultBridgeDir }
$BridgeDir = Resolve-Dir $BridgeDir
New-Item -ItemType Directory -Path $BridgeDir -Force | Out-Null
Set-BridgeEnvironment $BridgeDir
$SharedFile = Join-Path $BridgeDir 'bridge.shm'

if ($Rebuild -or -not (Test-Path -LiteralPath $script:FakeHostExe)) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) was not found in PATH.' }
    Write-Output 'Building the fake host...'
    & dotnet build (Join-Path $script:RepoRoot 'fake-host\UltrakillBridge.FakeHost\UltrakillBridge.FakeHost.csproj') -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build of the fake host failed.' }
}

$HostProcess = Get-Process $script:FakeHostName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $HostProcess) {
    $HostArgs = @('--dir', ('"' + $BridgeDir + '"'))
    if ($Spawn) { $HostArgs += @('--spawn', $Spawn) }
    Write-Output 'Starting the fake host...'
    $HostProcess = Start-Process -FilePath $script:FakeHostExe -ArgumentList $HostArgs -WorkingDirectory (Split-Path -Parent $script:FakeHostExe) -PassThru
} else {
    Write-Output "The fake host is already running (PID $($HostProcess.Id))."
}

$Watch = [Diagnostics.Stopwatch]::StartNew()
$Ready = $false
while ($Watch.Elapsed.TotalSeconds -lt 30) {
    $State = Read-BridgeHeader $SharedFile
    if ($State -and $State.HostProcessId -eq $HostProcess.Id -and $State.CoreStatus -eq 1) { $Ready = $true; break }
    if (-not (Get-Process -Id $HostProcess.Id -ErrorAction SilentlyContinue)) { throw 'The fake host closed during startup. See fakehost.log in the bridge folder.' }
    Start-Sleep -Milliseconds 300
}
if (-not $Ready) { throw 'The fake host did not publish the bridge within 30 s.' }
Write-Output "Fake host ready (PID $($HostProcess.Id)), bridge folder $BridgeDir."

if ($WithGuest) {
    & (Join-Path $PSScriptRoot 'Launch-Guest.ps1') -BridgeDir $BridgeDir
}
