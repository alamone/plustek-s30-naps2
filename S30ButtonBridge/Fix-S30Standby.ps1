#Requires -RunAsAdministrator
<#
  Makes the Plustek S30 standby setting persistent.

  The GL310 driver (Scanapi2_gl310_x64.dll) re-sends "ScannerSleepTime" from a per-account ini every time
  it initializes the scanner. The WIA service (used by NAPS2) runs as LocalService, so it reads
  C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\Plustek\311U\*.ini, which Scanner Utility never
  touches. This script sets ScannerSleepTime in every copy (plus the install-dir templates that new
  profiles are seeded from). 0 = never enter standby. Originals are backed up as *.bak once.

  It also removes DocAction II from startup (unless -KeepDocAction), since it competes for button events.
#>
param([int]$Minutes = 0, [switch]$KeepDocAction)

$targets = @(
    'C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\Plustek\311U',
    'C:\Windows\ServiceProfiles\NetworkService\AppData\Roaming\Plustek\311U',
    'C:\Windows\System32\config\systemprofile\AppData\Roaming\Plustek\311U',
    'C:\Program Files (x86)\Plustek II\Plustek SmartOffice S30'
) + (Get-ChildItem 'C:\Users' -Directory | ForEach-Object { Join-Path $_.FullName 'AppData\Roaming\Plustek\311U' })

foreach ($dir in $targets) {
    foreach ($name in 'Scanapi2_gl310_x64.ini', 'Scanapi2_gl310.ini') {
        $path = Join-Path $dir $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $text = [IO.File]::ReadAllText($path)
        if ($text -notmatch '"ScannerSleepTime"="(\d+)"') { Write-Host "skip (no key)  $path"; continue }
        $old = $Matches[1]
        if ($old -eq "$Minutes") { Write-Host "ok   ($old)      $path"; continue }
        if (-not (Test-Path -LiteralPath "$path.bak")) { Copy-Item -LiteralPath $path "$path.bak" }
        $text = $text -replace '"ScannerSleepTime"="\d+"', ('"ScannerSleepTime"="{0}"' -f $Minutes)
        [IO.File]::WriteAllText($path, $text, [Text.Encoding]::Default)
        Write-Host "set  ($old -> $Minutes)  $path"
    }
}
# DocAction II polls the same USB interrupt pipe as S30ButtonBridge and would steal button presses.
if (-not $KeepDocAction) {
    $run = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    $cmd = (Get-ItemProperty $run -ErrorAction SilentlyContinue).DocAction_II
    if ($cmd) {
        $backup = Join-Path $PSScriptRoot 'DocAction_II-Run-backup.reg'
        reg export 'HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' $backup /y | Out-Null
        Remove-ItemProperty $run -Name DocAction_II
        Write-Host "`nRemoved DocAction II from startup (Run key backed up to $backup)."
    }
    Get-Process 'DocAction II' -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host "`nRestarting WIA service so it re-reads the setting..."
Restart-Service stisvc -Force
