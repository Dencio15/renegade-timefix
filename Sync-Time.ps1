#requires -Version 3.0
<#
    Sync-Time.ps1
    Sets the time zone to Eastern Time (US & Canada) and corrects the system
    clock from an internet time source.

    Built for PCs with a dead CMOS battery, where the clock resets on every
    boot. Uses plain-HTTP time sources as a fallback because when the clock is
    years off, HTTPS certificate validation fails ("not yet valid") and NTP
    refuses corrections larger than MaxPosPhaseCorrection.

    Setting the time zone works as a standard user.
    Setting the clock requires admin / SYSTEM.
#>
param(
    [string]$TimeZoneId = 'Eastern Standard Time',
    [int]$ToleranceSeconds = 2,
    [int]$RetryMinutes = 3,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $env:ProgramData 'TimeFix\timefix.log'

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    if (-not $Quiet) {
        $color = switch ($Level) { 'ERROR' { 'Red' } 'WARN' { 'Yellow' } 'OK' { 'Green' } default { 'Gray' } }
        Write-Host $line -ForegroundColor $color
    }
    try {
        $dir = Split-Path $LogFile -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
    } catch { }
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# --- 1. Time zone -----------------------------------------------------------

# 'Eastern Standard Time' is only the registry ID -- it is the DST-aware zone
# that displays as "(UTC-05:00) Eastern Time (US & Canada)". Log the friendly
# name so nobody has to take that on faith.
function Get-TzLabel {
    param([string]$Id)
    try {
        $tz = [System.TimeZoneInfo]::FindSystemTimeZoneById($Id)
        return '{0}  [{1}]' -f $tz.DisplayName, $tz.Id
    } catch { return $Id }
}

function Set-LocalTimeZone {
    param([string]$Id)

    $current = $null
    try { $current = (Get-TimeZone).Id } catch { $current = (tzutil /g) -join '' }

    if ($current -eq $Id) {
        Write-Log ('Time zone already correct: {0}' -f (Get-TzLabel $Id))
        return
    }

    Write-Log ('Changing time zone: {0} -> {1}' -f (Get-TzLabel $current), (Get-TzLabel $Id))
    try {
        Set-TimeZone -Id $Id            # Windows 10+ / PS 5.1
    } catch {
        & tzutil.exe /s $Id             # fallback for older builds
        if ($LASTEXITCODE -ne 0) { throw "tzutil failed with exit code $LASTEXITCODE" }
    }

    # .NET caches the zone for the life of the process; without this, any
    # ToLocalTime() below would still use the OLD offset.
    [System.TimeZoneInfo]::ClearCachedData()

    $applied = (Get-TimeZone).Id
    if ($applied -ne $Id) { throw "Time zone did not stick: wanted '$Id', got '$applied'." }
    Write-Log ('Time zone set to: {0}' -f (Get-TzLabel $Id)) 'OK'
}

# --- 2. Fetch true UTC from the network ------------------------------------

# HTTPS first (tamper-resistant), then plain HTTP, which is what actually
# works when the clock is so wrong that certificates fail validation.
$TimeSources = @(
    @{ Url = 'https://cloudflare.com/cdn-cgi/trace';            Kind = 'trace'  }
    @{ Url = 'https://www.google.com';                          Kind = 'header' }
    @{ Url = 'http://cloudflare.com/cdn-cgi/trace';             Kind = 'trace'  }
    @{ Url = 'http://www.msftconnecttest.com/connecttest.txt';  Kind = 'header' }
    @{ Url = 'http://www.google.com';                           Kind = 'header' }
)

function Get-NetworkUtc {
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls
    } catch { }

    foreach ($src in $TimeSources) {
        try {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $resp = Invoke-WebRequest -Uri $src.Url -UseBasicParsing -TimeoutSec 10 `
                                      -Headers @{ 'Cache-Control' = 'no-cache' }
            $sw.Stop()

            $utc = $null
            if ($src.Kind -eq 'trace') {
                $tsLine = ($resp.Content -split "`n" | Where-Object { $_ -like 'ts=*' } | Select-Object -First 1)
                if ($tsLine) {
                    $epoch = [double]($tsLine.Substring(3))
                    $utc = [DateTime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc).AddSeconds($epoch)
                }
            } else {
                # PowerShell 7 hands back a string[]; Windows PowerShell 5.1 a string.
                $dateHeader = @($resp.Headers['Date'])[0]
                if ($dateHeader) {
                    $utc = [DateTime]::Parse($dateHeader, [Globalization.CultureInfo]::InvariantCulture,
                                             [Globalization.DateTimeStyles]::AdjustToUniversal -bor
                                             [Globalization.DateTimeStyles]::AssumeUniversal)
                }
            }

            if ($utc) {
                # The response was generated roughly mid-flight; add half the
                # round trip so we land closer to true time.
                $utc = $utc.AddMilliseconds($sw.ElapsedMilliseconds / 2)
                Write-Log ("Time source OK: {0} -> {1:yyyy-MM-dd HH:mm:ss} UTC (rtt {2} ms)" -f `
                           $src.Url, $utc, $sw.ElapsedMilliseconds)
                return $utc
            }
            Write-Log "No usable timestamp from $($src.Url)." 'WARN'
        } catch {
            Write-Log "Time source failed: $($src.Url) - $($_.Exception.Message)" 'WARN'
        }
    }
    return $null
}

# --- 3. Apply --------------------------------------------------------------

Write-Log '--- TimeFix run started ---'
Write-Log ("Clock before: {0:yyyy-MM-dd HH:mm:ss} local / {1:yyyy-MM-dd HH:mm:ss} UTC" -f `
           (Get-Date), (Get-Date).ToUniversalTime())

try {
    Set-LocalTimeZone -Id $TimeZoneId
} catch {
    Write-Log "Could not set the time zone: $($_.Exception.Message)" 'ERROR'
}

# On a cold boot the NIC may not have a link yet, so keep trying for a while.
$deadline = (Get-Date).AddMinutes($RetryMinutes)
$trueUtc = $null
do {
    $trueUtc = Get-NetworkUtc
    if ($trueUtc) { break }
    if ((Get-Date) -lt $deadline) {
        Write-Log 'No time source reachable yet; waiting 15s for the network...' 'WARN'
        Start-Sleep -Seconds 15
    }
} while ((Get-Date) -lt $deadline)

if (-not $trueUtc) {
    Write-Log 'No internet time source reachable. Clock left unchanged.' 'ERROR'
    exit 2
}

$drift = ([DateTime]::UtcNow - $trueUtc).TotalSeconds
Write-Log ('Clock drift: {0:N1} seconds' -f $drift)

if ([Math]::Abs($drift) -le $ToleranceSeconds) {
    Write-Log 'Clock is already accurate; nothing to do.' 'OK'
    exit 0
}

if (-not (Test-Elevated)) {
    Write-Log ('Clock is off by {0:N0} seconds but setting it requires administrator rights.' -f $drift) 'ERROR'
    Write-Log 'Run Sync-Time-Now.bat (it will prompt for elevation), or install the scheduled task once.' 'ERROR'
    exit 5
}

try {
    Set-Date -Date $trueUtc.ToLocalTime() | Out-Null
    Write-Log ("Clock set. Now: {0:yyyy-MM-dd HH:mm:ss} local -- {1}" -f (Get-Date), (Get-TzLabel (Get-TimeZone).Id)) 'OK'
} catch {
    Write-Log "Failed to set the clock: $($_.Exception.Message)" 'ERROR'
    exit 3
}

exit 0
