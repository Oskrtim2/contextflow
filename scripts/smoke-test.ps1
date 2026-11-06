[CmdletBinding()]
param(
    [string]$ExePath = "",
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot "artifacts\ContextFlow-1.0.0-win-x64\ContextFlow.exe"
}

$ExePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath. Run .\scripts\publish.ps1 first."
}

$running = Get-Process -Name ContextFlow -ErrorAction SilentlyContinue
if ($running) {
    throw "ContextFlow is already running. Exit it before running the smoke test."
}

$log = Join-Path $env:LOCALAPPDATA "ContextFlow\logs\contextflow.log"
$before = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

$process = Start-Process -FilePath $ExePath -ArgumentList "--exit-after", "$WaitSeconds" -PassThru
$process.WaitForExit(([Math]::Max($WaitSeconds, 1) + 25) * 1000) | Out-Null
$process.Refresh()
if (-not $process.HasExited) {
    Stop-Process -Id $process.Id -Force
    throw "Smoke test timed out; ContextFlow did not exit."
}

if ($process.ExitCode -ne 0) {
    throw "ContextFlow exited with code $($process.ExitCode)."
}

if (-not (Test-Path $log)) {
    throw "Diagnostic log was not created at $log."
}

$stream = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
try {
    $stream.Position = [Math]::Min($before, $stream.Length)
    $reader = New-Object System.IO.StreamReader($stream)
    $tail = $reader.ReadToEnd()
}
finally {
    $stream.Dispose()
}

$expected = @("Embedded ONNX policy model loaded", "ContextFlow ready", "ContextFlow shutdown complete")
$missing = $expected | Where-Object { $tail -notmatch [Regex]::Escape($_) }
if ($missing) {
    throw "Smoke test log is missing: $($missing -join ', ')"
}

Write-Host "Smoke test passed." -ForegroundColor Green
Write-Host "  Executable : $ExePath"
Write-Host "  Exit code  : $($process.ExitCode)"
Write-Host "  Log        : $log"
