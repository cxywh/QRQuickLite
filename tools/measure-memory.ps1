# Memory sampling harness. ASCII-only on purpose: Windows PowerShell 5.1 reads
# BOM-less .ps1 as ANSI, which corrupts non-ASCII literals and breaks parsing.
# Usage: powershell -NoProfile -File tools/measure-memory.ps1 -Exe "<path>"
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$IdleSeconds = 15
)

Add-Type -AssemblyName System.Windows.Forms
$ErrorActionPreference = 'Stop'

function Sample {
    param($Proc, [string]$Label)
    $Proc.Refresh()
    '{0,-24} ws={1,6} MB  private={2,6} MB  peak={3,6} MB' -f $Label,
        [math]::Round($Proc.WorkingSet64 / 1MB, 1),
        [math]::Round($Proc.PrivateMemorySize64 / 1MB, 1),
        [math]::Round($Proc.PeakWorkingSet64 / 1MB, 1)
}

Get-Process QRQuickLite -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# A: main window visible, idle
$p = Start-Process $Exe -PassThru
while ($p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 50; $p.Refresh() }
Start-Sleep -Milliseconds 6000
Sample $p 'A main-window idle'

# B: capture overlay up (global hotkey)
[System.Windows.Forms.SendKeys]::SendWait('%q')
Start-Sleep -Milliseconds 1500
Sample $p 'B overlay visible'
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 1200
Sample $p 'B2 after cancel'

# C: main window closed -> tray resident
$p.CloseMainWindow()
Start-Sleep -Seconds $IdleSeconds
Sample $p 'C tray idle'

if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Milliseconds 800

# D: cold start straight into capture (context-menu path), never shows main window
$q = Start-Process $Exe -ArgumentList '--capture' -PassThru
Start-Sleep -Milliseconds 2500
Sample $q 'D cold capture'
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds $IdleSeconds
Sample $q 'D2 tray idle (no main)'

if (-not $q.HasExited) { Stop-Process -Id $q.Id -Force }

# E: full capture + decode cycle (built-in self-test), sampled from outside
$e = Start-Process $Exe -ArgumentList '--selftest', '--e2e' -PassThru
$peakWs = 0
$peakPriv = 0
while (-not $e.HasExited) {
    try {
        $e.Refresh()
        if ($e.WorkingSet64 -gt $peakWs) { $peakWs = $e.WorkingSet64 }
        if ($e.PrivateMemorySize64 -gt $peakPriv) { $peakPriv = $e.PrivateMemorySize64 }
    } catch { }
    Start-Sleep -Milliseconds 120
}
'E selftest peak          ws={0,6} MB  private={1,6} MB  exit={2}' -f `
    [math]::Round($peakWs / 1MB, 1), [math]::Round($peakPriv / 1MB, 1), $e.ExitCode

# F: three capture cycles in a row -- is private memory a one-off high-water mark or a per-cycle leak?
$f = Start-Process $Exe -PassThru
while ($f.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 50; $f.Refresh() }
Start-Sleep -Milliseconds 5000
Sample $f 'F0 before cycles'
for ($i = 1; $i -le 3; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('%q')
    Start-Sleep -Milliseconds 1200
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 1500
    Sample $f ("F{0} after cycle {1}" -f $i, $i)
}
if (-not $f.HasExited) { Stop-Process -Id $f.Id -Force }
