[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "SagaFrontierBridge.sln"

if (-not (Test-Path -LiteralPath (Join-Path $GamePath "SaGa Frontier Remastered.exe"))) {
    throw "SaGa Frontier Remastered.exe was not found under: $GamePath"
}

if (-not (Test-Path -LiteralPath (Join-Path $GamePath "BepInEx\plugins"))) {
    throw "BepInEx is not installed under: $GamePath"
}

if (-not (Test-Path -LiteralPath (Join-Path $GamePath "winhttp.dll"))) {
    throw "winhttp.dll was not found. Install the BepInEx 6 IL2CPP loader first."
}

dotnet build $solution -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$buildDirectory = Join-Path $root "src\SagaFrontierBridge.Plugin\bin\$Configuration"
$targetDirectory = Join-Path $GamePath "BepInEx\plugins\SagaFrontierBridge"

if (-not (Test-Path -LiteralPath $buildDirectory)) {
    throw "Build output not found: $buildDirectory"
}

New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null

$assemblies = Get-ChildItem -LiteralPath $buildDirectory -Filter "SagaFrontierBridge*.dll"
if ($assemblies.Count -eq 0) {
    throw "No SagaFrontierBridge assemblies were found in: $buildDirectory"
}

foreach ($assembly in $assemblies) {
    Copy-Item -LiteralPath $assembly.FullName -Destination $targetDirectory -Force
    Write-Host "Copied $($assembly.Name) -> $targetDirectory"
}

Write-Host ""
Write-Host "Deployment complete. Start the game, then query the bridge."
Write-Host "Config: $GamePath\BepInEx\config\com.viewtifulslayer.sagafrontier.bridge.cfg"
Write-Host "Log:    $GamePath\BepInEx\LogOutput.log"
