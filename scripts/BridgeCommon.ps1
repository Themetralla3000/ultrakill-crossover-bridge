# Shared helpers for the scripts in this folder (dot-sourced; PowerShell 5.1 compatible, ASCII only).
# Bridge file layout: docs/protocol.md (header 0x0, control block 0x800 size 0x64, seqlock).

$script:CtrlOffset = 0x800
$script:CtrlSize = 0x64

# Repository root and the defaults every script shares.
$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:DefaultBridgeDir = Join-Path $script:RepoRoot 'runtime'
$script:GuestStage = Join-Path $script:RepoRoot 'dist\guest'
$script:FakeHostName = 'UltrakillBridge.FakeHost'
$script:FakeHostExe = Join-Path $script:RepoRoot 'fake-host\UltrakillBridge.FakeHost\bin\Release\net8.0-windows\UltrakillBridge.FakeHost.exe'
$script:DefaultUltrakillDir = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL'

if (-not ('UkbWin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UkbWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumThreadWindows(uint tid, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

  // All top-level windows (visible or not, owned or not) of the given threads.
  public static IntPtr[] ThreadWindows(int[] tids) {
    List<IntPtr> list = new List<IntPtr>();
    foreach (int t in tids) { EnumThreadWindows((uint)t, delegate(IntPtr h, IntPtr l) { list.Add(h); return true; }, IntPtr.Zero); }
    return list.ToArray();
  }
  public static string ClassOf(IntPtr h) { StringBuilder s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string TitleOf(IntPtr h) { StringBuilder s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
}
'@
}

function Set-BridgeEnvironment([string]$Dir) {
    # UKBRIDGE_DIR is this kit's name; ERMC_DIR is the original one (Minecraft Ring's Elden Ring host DLL reads only that).
    # Both are set to the same folder so any host, old or new, finds the same files.
    $env:UKBRIDGE_DIR = $Dir
    $env:ERMC_DIR = $Dir
}

function Get-ProcessWindows([System.Diagnostics.Process]$Process) {
    # Top-level windows of every thread of the process, including invisible and owned ones (MainWindowHandle misses those).
    $Process.Refresh()
    $Tids = @($Process.Threads | ForEach-Object { [int]$_.Id })
    $Result = @()
    foreach ($H in [UkbWin]::ThreadWindows($Tids)) {
        $R = New-Object UkbWin+RECT
        [void][UkbWin]::GetWindowRect($H, [ref]$R)
        $Result += [PSCustomObject]@{
            Handle = $H
            Class = [UkbWin]::ClassOf($H)
            Title = [UkbWin]::TitleOf($H)
            Visible = [UkbWin]::IsWindowVisible($H)
            Iconic = [UkbWin]::IsIconic($H)
            Left = $R.Left; Top = $R.Top; Width = $R.Right - $R.Left; Height = $R.Bottom - $R.Top
        }
    }
    return $Result
}

function Read-BridgeHeader([string]$SharedFile) {
    if (-not (Test-Path -LiteralPath $SharedFile)) { return $null }
    try {
        $Size = $script:CtrlOffset + $script:CtrlSize
        $Buf = New-Object byte[] $Size
        $Stream = [IO.File]::Open($SharedFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $Read = $Stream.Read($Buf, 0, $Size) } finally { $Stream.Dispose() }
        if ($Read -ne $Size -or [BitConverter]::ToUInt32($Buf, 0) -ne 0x434D484D) { return $null }
        return [PSCustomObject]@{
            HostProcessId = [BitConverter]::ToUInt32($Buf, 0x20)
            GuestProcessId = [BitConverter]::ToUInt32($Buf, 0x24)
            GuestStartMs = [BitConverter]::ToUInt64($Buf, 0x30)
            CoreStatus = [BitConverter]::ToInt32($Buf, 0x44)
            ControlSeq = [BitConverter]::ToUInt32($Buf, $script:CtrlOffset)
            ControlFlags = [BitConverter]::ToUInt32($Buf, $script:CtrlOffset + 4)
        }
    } catch { return $null }
}

function Clear-BridgeControl([string]$SharedFile) {
    # Seqlock write: seq -> even, seq+1 (odd), zero 0x804..0x863, seq+2 (even, never 0). Releases camera/character to the host.
    if (-not (Test-Path -LiteralPath $SharedFile)) { return $false }
    if ($WhatIfPreference) { Write-Output 'What if: clearing the bridge control block.'; return $false }
    $Stream = [IO.File]::Open($SharedFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try {
        $Magic = New-Object byte[] 4
        [void]$Stream.Read($Magic, 0, 4)
        if ([BitConverter]::ToUInt32($Magic, 0) -ne 0x434D484D) { return $false }
        $Stream.Position = $script:CtrlOffset
        $SeqBytes = New-Object byte[] 4
        [void]$Stream.Read($SeqBytes, 0, 4)
        [uint64]$Seq = [BitConverter]::ToUInt32($SeqBytes, 0)
        if (($Seq % 2) -eq 1) {
            $Seq = ($Seq + 1) % 4294967296
            $Stream.Position = $script:CtrlOffset
            $Stream.Write([BitConverter]::GetBytes([uint32]$Seq), 0, 4); $Stream.Flush()
        }
        $Stream.Position = $script:CtrlOffset
        $Stream.Write([BitConverter]::GetBytes([uint32](($Seq + 1) % 4294967296)), 0, 4); $Stream.Flush()
        $Stream.Position = $script:CtrlOffset + 4
        $Stream.Write((New-Object byte[] ($script:CtrlSize - 4)), 0, $script:CtrlSize - 4); $Stream.Flush()
        [uint64]$Final = ($Seq + 2) % 4294967296
        if ($Final -eq 0) { $Final = 2 }
        $Stream.Position = $script:CtrlOffset
        $Stream.Write([BitConverter]::GetBytes([uint32]$Final), 0, 4); $Stream.Flush()
        return $true
    } finally { $Stream.Dispose() }
}

function Close-ProcessGracefully([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds) {
    # WM_CLOSE to every top-level window (including hidden/owned ones), then wait. Returns $true when the process exited.
    try { [void]$Process.CloseMainWindow() } catch { }
    foreach ($W in (Get-ProcessWindows $Process)) { [void][UkbWin]::PostMessage($W.Handle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    return $Process.WaitForExit($TimeoutSeconds * 1000)
}

function Resolve-Dir([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\') }

function Assert-Inside([string]$Dir, [string]$Candidate) {
    if (-not $Candidate.StartsWith($Dir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid path outside the game folder: $Candidate" }
}
