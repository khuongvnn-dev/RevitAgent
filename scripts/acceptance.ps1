# ==================================================================
# acceptance.ps1 - RevitAgent v2 live acceptance (attach mode)
#
#   powershell -ExecutionPolicy Bypass -File scripts\acceptance.ps1 [-Year 2024] [-Model <source .rvt>] [-WaitMinutes 10]
#
# What it does
#   1. Builds the solution (Debug) unless -NoBuild.
#   2. Waits until a Revit <Year> process has the RevitAgent Bridge pipe (\\.\pipe\revitagent-bridge-<pid>).
#      YOU start Revit yourself and answer any startup prompt (e.g. "Security - Unsigned Add-In" for
#      MyAddin) - the gateway never answers security dialogs for you.
#      Then YOU enable "Let AI act on my Revit" for that Revit on the dashboard (action grant;
#      the gateway did not launch this Revit, so agents may only read it until you do). -GrantReady skips the pause.
#   3. Runs tools\RevitAgent.E2E in --attach mode: catalog checks, dialog/readiness, sandbox copy of the model,
#      doc_open (copy only), doc_info, Levels via the model (the "list all Level names" example), ribbon
#      items (incl. add-ins), UI snapshot, doc_close (discard). Your Revit is NOT closed.
#   4. Evidence: a normal gateway run (suite e2e-acceptance) in the run store, i.e.
#      %REVITAGENT_HOME%\runs, default %LOCALAPPDATA%\RevitAgent\runs (see config\run-store.json), under
#      <run folder>\evidence\calls.md (+ JSON / PNG per call). The run is finished PASS or FAIL, so runs_list
#      and the dashboard history show it. The script prints the run folder at the end.
# File is ASCII on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# ==================================================================
[CmdletBinding()]
param(
    [int]$Year = 2024,
    [string]$Model = 'D:\Autodesk\Revit 2024\Samples\Snowdon Towers Sample Architectural.rvt',
    [int]$WaitMinutes = 10,
    [switch]$NoBuild,
    [switch]$GrantReady   # skip the "enable the action grant" pause (grant already on)
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $NoBuild) {
    Write-Host '[acceptance] dotnet build RevitAgent.slnx -c Debug ...'
    dotnet build RevitAgent.slnx -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

$e2e = Join-Path $repo 'tools\RevitAgent.E2E\bin\Debug\net10.0-windows\RevitAgent.E2E.exe'
if (-not (Test-Path $e2e)) { throw "E2E harness not built: $e2e" }
if (-not (Test-Path $Model)) { throw "Model not found: $Model (pass -Model <path to a .rvt>)" }

function Find-BridgedRevit([int]$year) {
    foreach ($p in Get-Process Revit -ErrorAction SilentlyContinue) {
        try { $major = $p.MainModule.FileVersionInfo.FileMajorPart } catch { $major = 0 }
        if ($major -ne ($year - 2000)) { continue }
        if (Test-Path ("\\.\pipe\revitagent-bridge-" + $p.Id)) { return $p }
    }
    return $null
}

$deadline = (Get-Date).AddMinutes($WaitMinutes)
$revit = Find-BridgedRevit $Year
if (-not $revit) {
    Write-Host ''
    Write-Host "[acceptance] Start Revit $Year now (no model needed)." -ForegroundColor Yellow
    Write-Host '             If Revit asks about an unsigned add-in (e.g. MyAddin), answer it yourself.' -ForegroundColor Yellow
    Write-Host "             Waiting up to $WaitMinutes min for the RevitAgent Bridge to come up ..." -ForegroundColor Yellow
}
while (-not $revit -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $revit = Find-BridgedRevit $Year
}
if (-not $revit) {
    Write-Host "[acceptance] No Revit $Year with the RevitAgent Bridge found." -ForegroundColor Red
    Write-Host '             Check: Revit fully started? Bridge installed (dashboard > Revit versions & Bridge)?' -ForegroundColor Red
    Write-Host '             Revit started BEFORE the Bridge was installed must be restarted.' -ForegroundColor Red
    exit 2
}

Write-Host ''
Write-Host '[acceptance] ACTION GRANT REQUIRED (the gateway did not launch this Revit):' -ForegroundColor Yellow
Write-Host '             In the RevitAgent dashboard (card "Let AI act on my Revit") enable' -ForegroundColor Yellow
Write-Host "             the switch for pid $($revit.Id) (or 'All my Revit') with at least 15 minutes." -ForegroundColor Yellow
Write-Host '             Without it the click/type/open-document steps fail with PERMISSION_DENIED (read steps still work).' -ForegroundColor Yellow
if (-not $GrantReady) {
    Read-Host '             Press Enter once the grant is on (or re-run with -GrantReady)' | Out-Null
}

Write-Host "[acceptance] Revit $Year pid $($revit.Id) has the Bridge. Running the acceptance scenario ..."
# No --evidence: the harness creates the run folder in the run store and prints "run folder: <path>".
$lines = & $e2e --headless --attach $revit.Id --model $Model | ForEach-Object { Write-Host $_; $_ }
$code = $LASTEXITCODE
$runLine = $lines | Where-Object { "$_" -like 'run folder: *' } | Select-Object -First 1
Write-Host ''
if ($runLine) {
    Write-Host "[acceptance] $runLine (evidence\calls.md inside)"
} else {
    Write-Host '[acceptance] run folder: see the "run folder:" line above'
}
exit $code
