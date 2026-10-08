<#
.SYNOPSIS
  Summarises the hit log written by the guest ([Combat] HitLog = true -> <bridge dir>\hitlog.csv) per weapon.
.DESCRIPTION
  Per weapon id (as classified by the guest): hits, shots, hits per shot, shots/s and hits/s while firing, mean UK
  damage per hit and per shot (limb/head bonus included), UK DPS while firing, head share. These are the numbers the
  combat design table (UltraRain docs/COMBAT-DESIGN.md sections A and C.2) estimates.

  A shot starts when the shot sequence changes or more than -ShotGapMs passed since the previous hit of the weapon.
  A burst ends after -BurstGapMs without a hit of that weapon. Rates are measured inside bursts of at least two shots
  ((shots - bursts) / time between the first and last shot of each burst), so idle time is not counted. Weapons that
  never fired twice within a burst (charged shots, coins...) only get the per-hit / per-shot damage.
.PARAMETER Path
  The CSV. Default: <UKBRIDGE_DIR or ERMC_DIR or %TEMP%\ermc>\hitlog.csv.
.PARAMETER BurstGapMs
  Silence that ends a burst (default 1500).
.PARAMETER ShotGapMs
  Gap between two hits of one weapon that always starts a new shot (default 250).
.PARAMETER Weapon
  Only these weapon names (e.g. REV_SHOT, NAIL).
.PARAMETER Csv
  Also write the summary table to this CSV file.
.EXAMPLE
  .\scripts\hitlog-summary.ps1
  .\scripts\hitlog-summary.ps1 -Path D:\logs\hitlog.csv -Weapon REV_SHOT,SHO_PELLET -Csv summary.csv
#>
param(
    [string]$Path,
    [int]$BurstGapMs = 1500,
    [int]$ShotGapMs = 250,
    [string[]]$Weapon,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'
if (-not $Path) {
    $dir = $env:UKBRIDGE_DIR
    if (-not $dir) { $dir = $env:ERMC_DIR }
    if (-not $dir) { $dir = Join-Path $env:TEMP 'ermc' }
    $Path = Join-Path $dir 'hitlog.csv'
}
if (-not (Test-Path -LiteralPath $Path)) { throw "No hit log at '$Path'. Set [Combat] HitLog = true in the guest config, fire at host enemies, then run again." }

$ci = [Globalization.CultureInfo]::InvariantCulture
$rows = @(Import-Csv -LiteralPath $Path)
if ($rows.Count -eq 0) { throw "'$Path' has no rows." }
Write-Host ("{0} hits in {1}, {2:N1} s of play" -f $rows.Count, $Path, (([double]$rows[-1].t_ms - [double]$rows[0].t_ms) / 1000.0))

$out = @()
foreach ($grp in ($rows | Group-Object weapon | Sort-Object Name)) {
    if ($Weapon -and ($Weapon -notcontains $grp.Name)) { continue }
    $hits = @($grp.Group | Sort-Object { [double]$_.t_ms })
    $shots = 0; $bursts = 0; $firstShotHits = 0
    $firingMs = 0.0; $rateShots = 0
    $prevT = $null; $prevSeq = $null
    $burstStart = $null; $burstLast = $null; $burstShots = 0; $burstHitsFirst = 0
    $inFirstShot = $false
    $closeBurst = {
        if ($script:burstShots -ge 2) {
            $script:firingMs += ($script:burstLast - $script:burstStart)
            $script:rateShots += ($script:burstShots - 1)
            $script:bursts++
        }
    }
    $script:firingMs = 0.0; $script:rateShots = 0; $script:bursts = 0
    $script:burstShots = 0; $script:burstStart = $null; $script:burstLast = $null
    $rateHits = 0.0; $sinceShotStartHits = 0; $curShotHits = 0
    $shotHitCounts = New-Object System.Collections.Generic.List[int]
    $shotStarts = New-Object System.Collections.Generic.List[double]
    foreach ($h in $hits) {
        $t = [double]$h.t_ms
        $newBurst = ($prevT -eq $null) -or (($t - $prevT) -gt $BurstGapMs)
        $newShot = $newBurst -or ($h.shotSeq -ne $prevSeq) -or (($t - $prevT) -gt $ShotGapMs)
        if ($newBurst) {
            & $closeBurst
            $script:burstShots = 0; $script:burstStart = $t
        }
        if ($newShot) {
            $shots++; $script:burstShots++
            if ($curShotHits -gt 0) { $shotHitCounts.Add($curShotHits) }
            $curShotHits = 0
            $shotStarts.Add($t)
        }
        $curShotHits++
        $script:burstLast = $t
        $prevT = $t; $prevSeq = $h.shotSeq
    }
    & $closeBurst
    if ($curShotHits -gt 0) { $shotHitCounts.Add($curShotHits) }

    $n = $hits.Count
    $ukSum = 0.0; $rawSum = 0.0; $headHits = 0; $maxUk = 0.0
    foreach ($h in $hits) {
        $uk = [double]::Parse($h.ukDamage, $ci); $ukSum += $uk; if ($uk -gt $maxUk) { $maxUk = $uk }
        $rawSum += [double]::Parse($h.rawMultiplier, $ci)
        if ($h.weakpoint -eq '1') { $headHits++ }
    }
    $secs = $script:firingMs / 1000.0
    $shotsPerSec = $null; $hitsPerSec = $null; $dps = $null
    if ($secs -gt 0) {
        $shotsPerSec = $script:rateShots / $secs
        $hitsPerSec = $shotsPerSec * ($n / [double]$shots)
        $dps = $shotsPerSec * ($ukSum / [double]$shots)
    }
    $out += [pscustomobject][ordered]@{
        Weapon        = $grp.Name
        Hits          = $n
        Shots         = $shots
        HitsPerShot   = [math]::Round($n / [double]$shots, 2)
        ShotsPerSec   = if ($shotsPerSec -ne $null) { [math]::Round($shotsPerSec, 2) } else { 'n/a' }
        HitsPerSec    = if ($hitsPerSec -ne $null) { [math]::Round($hitsPerSec, 2) } else { 'n/a' }
        RawPerHit     = [math]::Round($rawSum / $n, 3)
        UkPerHit      = [math]::Round($ukSum / $n, 3)
        UkPerShot     = [math]::Round($ukSum / $shots, 3)
        UkDps         = if ($dps -ne $null) { [math]::Round($dps, 2) } else { 'n/a' }
        MaxUkHit      = [math]::Round($maxUk, 3)
        HeadPercent   = [math]::Round(100.0 * $headHits / $n, 0)
        FiringSeconds = [math]::Round($secs, 1)
    }
}

if ($out.Count -eq 0) { Write-Host 'Nothing to show for the selected weapons.'; return }
$out | Format-Table -AutoSize
Write-Host 'UkPerHit includes the limb / head bonus; RawPerHit is the multiplier before it. Compare UkPerHit / HitsPerSec with the estimates in COMBAT-DESIGN.md A and C.2.'
if ($Csv) { $out | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8; Write-Host "Wrote $Csv" }
