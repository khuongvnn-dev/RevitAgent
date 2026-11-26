[CmdletBinding()]
param(
    [string]$ShimExe,
    [int]$Year = 2024,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

if (-not $ShimExe) {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
    $ShimExe = Join-Path $repoRoot "src\RevitAgent.Shim\bin\Debug\net10.0-windows\RevitAgent.Shim.exe"
}

if (-not (Test-Path $ShimExe)) {
    [Console]::Error.WriteLine("RevitAgent.Shim.exe not found at '$ShimExe'. Build the project first.")
    exit 2
}

$resolvedExe = (Resolve-Path $ShimExe).Path

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $resolvedExe
$psi.Arguments = "--check-machine --year $Year"
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

$process = [System.Diagnostics.Process]::Start($psi)
$stdout = $process.StandardOutput.ReadToEnd()
$stderr = $process.StandardError.ReadToEnd()
$process.WaitForExit()

$exitCode = $process.ExitCode

if ($Json) {
    if ($stdout) {
        [Console]::Out.Write($stdout)
    }
    if ($stderr) {
        [Console]::Error.Write($stderr)
    }
    exit $exitCode
}

if ($stderr) {
    [Console]::Error.Write($stderr)
}

if ($stdout) {
    try {
        $report = $stdout | ConvertFrom-Json
        $readyStr = if ($report.ready_for_unattended) { "READY" } else { "NOT READY" }
        Write-Host ("Machine readiness for unattended runs (Revit {0}): {1}" -f $report.year, $readyStr)
        Write-Host ("Checked at: {0}" -f $report.checked_at)
        Write-Host ""
        $report.items | Format-Table -Property status, title, fix -AutoSize
    }
    catch {
        [Console]::Out.Write($stdout)
    }
}

exit $exitCode
