[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Version = "",

    [switch]$SkipReadyToRun,

    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\ContextFlow\ContextFlow.csproj"
$projectXml = [xml](Get-Content $project -Raw)
$resolvedVersion = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($resolvedVersion)) { $resolvedVersion = "1.0.0" }
if (-not [string]::IsNullOrWhiteSpace($Version)) { $resolvedVersion = $Version }

$artifactName = "ContextFlow-$resolvedVersion-$Runtime"
$publishDir = Join-Path $repoRoot "artifacts\$artifactName"
$artifactsRoot = Join-Path $repoRoot "artifacts"
$zipPath = Join-Path $artifactsRoot "$artifactName.zip"
$readyToRun = if ($SkipReadyToRun) { "false" } else { "true" }

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "Publishing ContextFlow $resolvedVersion for $Runtime ..." -ForegroundColor Cyan
$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:PublishReadyToRun=$readyToRun",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:Version=$resolvedVersion",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $publishDir "ContextFlow.exe"
if (-not (Test-Path $exe)) { throw "Publish did not produce ContextFlow.exe." }

Get-ChildItem $publishDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".pdb", ".lib", ".exp" } |
    Remove-Item -Force

# Ship the legal notices alongside the single-file executable. They do not
# affect the single-file nature of the application itself.
$licenseDir = Join-Path $publishDir "licenses"
New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
Copy-Item (Join-Path $repoRoot "docs\THIRD_PARTY_NOTICES.md") $licenseDir -Force
Copy-Item (Join-Path $repoRoot "docs\PRIVACY.md") $licenseDir -Force
Copy-Item (Join-Path $repoRoot "docs\USER_GUIDE.md") $licenseDir -Force
Copy-Item (Join-Path $repoRoot "docs\third-party\ONNX-RUNTIME-LICENSE.txt") $licenseDir -Force
Copy-Item (Join-Path $repoRoot "docs\third-party\ONNX-RUNTIME-THIRD-PARTY-NOTICES.txt") $licenseDir -Force

$runtimeExtras = Get-ChildItem $publishDir -File |
    Where-Object { $_.Extension -in ".dll", ".json", ".deps", ".runtimeconfig" }
if ($runtimeExtras) {
    Write-Warning "Publish output still contains runtime file(s): $($runtimeExtras.Name -join ', ')"
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
@(
    "ContextFlow $resolvedVersion ($Runtime)",
    "SHA256  $hash  ContextFlow.exe"
) | Set-Content (Join-Path $publishDir "SHA256SUMS.txt") -Encoding ascii

# Optional Authenticode signing. Set CONTEXTFLOW_SIGNTOOL and
# CONTEXTFLOW_CERT_THUMBPRINT to activate. Timestamping is optional and can be
# disabled for disconnected build environments.
if (-not [string]::IsNullOrWhiteSpace($env:CONTEXTFLOW_CERT_THUMBPRINT)) {
    $signtool = $env:CONTEXTFLOW_SIGNTOOL
    if ([string]::IsNullOrWhiteSpace($signtool)) {
        $signtool = "signtool.exe"
    }

    Write-Host "Signing ContextFlow.exe ..." -ForegroundColor Cyan
    & $signtool sign /sha1 $env:CONTEXTFLOW_CERT_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $exe
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    @(
        "ContextFlow $resolvedVersion ($Runtime)",
        "SHA256  $hash  ContextFlow.exe (signed)"
    ) | Set-Content (Join-Path $publishDir "SHA256SUMS.txt") -Encoding ascii
}

if (-not $SkipZip) {
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
    Write-Host "Package: $zipPath" -ForegroundColor Green
}

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Executable: $exe" -ForegroundColor Green
Write-Host "Size: $sizeMb MB" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
