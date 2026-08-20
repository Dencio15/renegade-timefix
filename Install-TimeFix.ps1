<#
    Install-TimeFix.ps1   -- run ONCE per PC, as administrator.

    After this runs, the agent never has to click anything: a scheduled task
    running as SYSTEM fixes the time zone and the clock at every boot and
    every logon, which is exactly when a dead CMOS battery has just wrecked
    the clock.

    It also repairs the Windows Time service itself, including the phase
    correction limits that make w32time silently refuse to fix a clock that
    is years out of date.
#>
param(
    [string]$TimeZoneId = 'Eastern Standard Time',
    [string]$TaskName   = 'TimeFix - Sync clock and time zone'
)

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:ProgramData 'TimeFix'
$Script     = Join-Path $InstallDir 'Sync-Time.ps1'

function Say { param([string]$m, [string]$c = 'Gray') Write-Host $m -ForegroundColor $c }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
          [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say 'This installer must run as administrator.' 'Red'
    exit 1
}

# --- copy the worker somewhere permanent -----------------------------------

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Sync-Time.ps1') -Destination $Script -Force
Say "Installed worker script to $Script" 'Green'

$ExeSource = Join-Path $PSScriptRoot 'IT Time Fix.exe'
$Exe       = Join-Path $InstallDir  'IT Time Fix.exe'
if (Test-Path $ExeSource) {
    Copy-Item -LiteralPath $ExeSource -Destination $Exe -Force
    Say "Installed agent tool to $Exe" 'Green'
} else {
    $Exe = $null
    Say "'IT Time Fix.exe' not found next to this installer; skipping the agent shortcut." 'Yellow'
}

# --- scheduled task, running as SYSTEM -------------------------------------

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument (
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -TimeZoneId "{1}" -Quiet' -f $Script, $TimeZoneId)

$triggers = @()

$atStartup = New-ScheduledTaskTrigger -AtStartup
try { $atStartup.Delay = 'PT30S' } catch { }   # let the NIC come up first
$triggers += $atStartup

$triggers += New-ScheduledTaskTrigger -AtLogOn

# Hourly top-up, in case the network was down at boot.
try {
    $hourly = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) `
                -RepetitionInterval (New-TimeSpan -Hours 1) `
                -RepetitionDuration (New-TimeSpan -Days 3650)
    $triggers += $hourly
} catch {
    try {
        $hourly = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) `
                    -RepetitionInterval (New-TimeSpan -Hours 1)
        $triggers += $hourly
    } catch { Say 'Could not add the hourly trigger (boot/logon triggers still installed).' 'Yellow' }
}

# After a brownout the PC often boots before the router/ISP is back, so also fire
# the moment a network actually becomes available.
try {
    $evtClass = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
    $netUp = New-CimInstance -CimClass $evtClass -ClientOnly
    $netUp.Enabled = $true
    $netUp.Subscription =
        '<QueryList><Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational">' +
        '<Select Path="Microsoft-Windows-NetworkProfile/Operational">' +
        '*[System[Provider[@Name=''Microsoft-Windows-NetworkProfile''] and EventID=10000]]' +
        '</Select></Query></QueryList>'
    $triggers += $netUp
    Say 'Added trigger: fire when the network comes up.' 'Green'
} catch {
    Say 'Could not add the network-available trigger; boot/logon/hourly still apply.' 'Yellow'
}

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
                -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers `
    -Principal $principal -Settings $settings `
    -Description 'Sets the time zone and corrects the system clock from an internet time source. Installed to work around a dead CMOS battery.' `
    -Force | Out-Null

Say "Registered scheduled task: $TaskName" 'Green'

# Let standard users START this task without a UAC prompt. The task itself runs
# as SYSTEM, so the privilege comes from the task, not from the caller -- this is
# what makes "IT Time Fix.exe" work for a non-admin agent.
#   BA = Administrators (full), SY = SYSTEM (full), AU = Authenticated Users (read + execute)
$sddl = 'D:(A;;GA;;;BA)(A;;GA;;;SY)(A;;GRGX;;;AU)'
try {
    $svc = New-Object -ComObject 'Schedule.Service'
    $svc.Connect()
    $svc.GetFolder('\').GetTask($TaskName).SetSecurityDescriptor($sddl, 0)
    Say 'Standard users may now trigger the task (no admin prompt needed).' 'Green'
} catch {
    Say "Could not relax the task permissions: $($_.Exception.Message)" 'Yellow'
    Say 'The task still runs at boot/logon/hourly; only the on-demand button needs this.' 'Yellow'
}

# A visible button for the agent, on every user's desktop.
if ($Exe) {
    try {
        $lnk = Join-Path "$env:PUBLIC\Desktop" 'IT Time Fix.lnk'
        $ws  = New-Object -ComObject WScript.Shell
        $sc  = $ws.CreateShortcut($lnk)
        $sc.TargetPath       = $Exe
        $sc.WorkingDirectory = $InstallDir
        $sc.Description      = 'Corrects the time zone and clock so Hubstaff will accept the login.'
        $sc.IconLocation     = "$Exe,0"      # the icon compiled into the exe
        $sc.Save()
        Say "Desktop shortcut created: $lnk" 'Green'
    } catch {
        Say "Could not create the desktop shortcut: $($_.Exception.Message)" 'Yellow'
    }
}

# --- repair the Windows Time service too -----------------------------------

try {
    & sc.exe config w32time start= auto | Out-Null
    & sc.exe start w32time 2>&1 | Out-Null

    # Without these, w32time refuses to correct a clock that is far off --
    # the exact failure mode of a drained CMOS battery.
    $cfg = 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config'
    Set-ItemProperty -Path $cfg -Name 'MaxPosPhaseCorrection' -Value 0xFFFFFFFF -Type DWord
    Set-ItemProperty -Path $cfg -Name 'MaxNegPhaseCorrection' -Value 0xFFFFFFFF -Type DWord

    & w32tm.exe /config /syncfromflags:manual /manualpeerlist:'time.windows.com,0x9 time.nist.gov,0x9' /reliable:no /update | Out-Null
    & w32tm.exe /resync /force 2>&1 | Out-Null
    Say 'Windows Time service configured (auto-start, unlimited phase correction).' 'Green'
} catch {
    Say "Could not fully configure w32time: $($_.Exception.Message)" 'Yellow'
    Say 'The scheduled task alone is enough; this was belt and braces.' 'Yellow'
}

# --- run it once now --------------------------------------------------------

Say ''
Say 'Running the sync now...' 'Cyan'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script -TimeZoneId $TimeZoneId
Say ''
$tz = Get-TimeZone
Say ('Done. Current time: {0:yyyy-MM-dd HH:mm:ss}' -f (Get-Date)) 'Green'
Say ('Time zone:         {0}  [{1}]' -f $tz.DisplayName, $tz.Id) 'Green'
if ($tz.Id -ne $TimeZoneId) { Say "WARNING: expected '$TimeZoneId' but the PC is on '$($tz.Id)'." 'Red' }
Say "Log file: $(Join-Path $InstallDir 'timefix.log')"
