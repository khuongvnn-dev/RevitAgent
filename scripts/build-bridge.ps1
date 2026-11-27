<#
.SYNOPSIS
    Builds the RevitAgent Bridge add-in for every supported Revit year.
.DESCRIPTION
    One build per year (2022-2024 -> net48, 2025-2026 -> net8.0-windows), sequentially, into
    src\RevitAgent.Bridge\bin\<Configuration>\R<yy>\. Verifies that each output folder contains only
    RevitAgent.Bridge.dll (+ .pdb) - the Bridge must not ship third-party DLLs into Revit.
    Install the result with the gateway tool bridge_install (or the dashboard "Install Bridge" button).
.PARAMETER Configuration
    Release (default) or Debug.
.PARAMETER Years
    Revit years to build. Default: 2022, 2023, 2024, 2025, 2026.
.EXAMPLE
    pwsh scripts/build-bridge.ps1
    powershell -File scripts\build-bridge.ps1 -Configuration Debug -Years 2024
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string[]]$Years = @("2022", "2023", "2024", "2025", "2026")
)

$ErrorActionPreference = "Stop"
# Accept "-Years 2022,2024" both from PowerShell and from `powershell -File` (which passes one string).
$Years = @($Years | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { [int]$_ })
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\RevitAgent.Bridge\RevitAgent.Bridge.csproj"
$results = @()

foreach ($year in $Years) {
    $short = $year.ToString().Substring(2)
    $outDir = Join-Path $repoRoot "src\RevitAgent.Bridge\bin\$Configuration\R$short"
    Write-Host "=== Building Bridge for Revit $year ($Configuration) ===" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $log = & dotnet build $project -c $Configuration "-p:RevitVersion=$year" -nologo 2>&1
    $code = $LASTEXITCODE
    $sw.Stop()
    $warnings = @($log | Select-String -Pattern ": warning " | ForEach-Object { $_.Line } | Sort-Object -Unique)
    $errors = @($log | Select-String -Pattern ": error " | ForEach-Object { $_.Line } | Sort-Object -Unique)
    $dll = Join-Path $outDir "RevitAgent.Bridge.dll"
    $extra = @()
    if (Test-Path $outDir) {
        $extra = @(Get-ChildItem $outDir -File | Where-Object { $_.Name -notin @("RevitAgent.Bridge.dll", "RevitAgent.Bridge.pdb") } | ForEach-Object { $_.Name })
    }
    $ok = ($code -eq 0) -and (Test-Path $dll) -and ($extra.Count -eq 0)
    $results += [pscustomobject]@{
        Year     = $year
        Success  = $ok
        Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Warnings = $warnings.Count
        Output   = $(if (Test-Path $dll) { $dll } else { "-" })
        Extra    = ($extra -join ", ")
    }
    foreach ($e in $errors) { Write-Host "  $e" -ForegroundColor Red }
    foreach ($w in $warnings) { Write-Host "  $w" -ForegroundColor Yellow }
    if ($extra.Count -gt 0) { Write-Host "  Unexpected files in output: $($extra -join ', ')" -ForegroundColor Red }
}

$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { -not $_.Success })
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed.Year -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All Bridge builds succeeded." -ForegroundColor Green
exit 0
