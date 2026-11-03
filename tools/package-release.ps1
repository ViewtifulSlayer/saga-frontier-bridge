[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts\release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root "Directory.Build.props"
$pluginProject = Join-Path $root "src\SagaFrontierBridge.Plugin\SagaFrontierBridge.Plugin.csproj"
$mcpProject = Join-Path $root "src\SagaFrontierBridge.Mcp\SagaFrontierBridge.Mcp.csproj"

if (-not (Test-Path -LiteralPath $propsPath)) {
    throw "Version source not found: $propsPath"
}

if (-not (Test-Path -LiteralPath $pluginProject)) {
    throw "Plugin project not found: $pluginProject"
}

if (-not (Test-Path -LiteralPath $mcpProject)) {
    throw "MCP project not found: $mcpProject"
}

$buildProps = [xml](Get-Content -LiteralPath $propsPath -Raw)
$versionNode = $buildProps.SelectSingleNode("/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw "Directory.Build.props does not define a Version."
}

$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Unsupported release version: $version"
}

$numericVersion = ($version -split '[-+]')[0]
$expectedFileVersion = "$numericVersion.0"

$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $root $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($outputRoot)

$workRoot = Join-Path $outputRoot ".staging"
$pluginBuild = Join-Path $workRoot "plugin-build"
$mcpBuild = Join-Path $workRoot "mcp-build"
$pluginStage = Join-Path $workRoot "plugin"
$mcpStage = Join-Path $workRoot "mcp"
$pluginZip = Join-Path $outputRoot "saga-frontier-bridge-$version-plugin-win-x64.zip"
$mcpZip = Join-Path $outputRoot "saga-frontier-bridge-$version-mcp-win-x64.zip"
$checksumPath = Join-Path $outputRoot "SHA256SUMS.txt"

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required file not found: $Source"
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Assert-FileVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $info = (Get-Item -LiteralPath $Path -ErrorAction Stop).VersionInfo
    if ($info.FileVersion -ne $expectedFileVersion) {
        throw "Unexpected file version for ${Path}: $($info.FileVersion), expected $expectedFileVersion"
    }

    if (-not $info.ProductVersion.StartsWith($version, [System.StringComparison]::Ordinal)) {
        throw "Unexpected product version for ${Path}: $($info.ProductVersion), expected $version"
    }
}

function Get-ZipFileEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @(
            $zip.Entries |
                Where-Object { $_.Name -ne "" } |
                ForEach-Object { $_.FullName.Replace("\", "/") }
        )
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-ZipContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string[]]$Expected
    )

    $actual = @(Get-ZipFileEntries $Path | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $difference = Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actual

    if ($difference) {
        $detail = ($difference | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        }) -join "; "
        throw "Unexpected contents in $Path`: $detail"
    }
}

if (-not ("System.IO.Compression.ZipFile" -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $pluginBuild, $mcpBuild, $pluginStage, $mcpStage | Out-Null

foreach ($archive in @($pluginZip, $mcpZip, $checksumPath)) {
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
}

Write-Host "Packaging SaGa Frontier Bridge $version"

& dotnet publish $pluginProject -c Release -o $pluginBuild --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Plugin publish failed with exit code $LASTEXITCODE."
}

& dotnet publish $mcpProject -c Release -o $mcpBuild --nologo
if ($LASTEXITCODE -ne 0) {
    throw "MCP publish failed with exit code $LASTEXITCODE."
}

$pluginDll = Join-Path $pluginBuild "SagaFrontierBridge.dll"
$pluginCoreDll = Join-Path $pluginBuild "SagaFrontierBridge.Core.dll"
$mcpExe = Join-Path $mcpBuild "SagaFrontierBridge.Mcp.exe"
$mcpDll = Join-Path $mcpBuild "SagaFrontierBridge.Mcp.dll"

foreach ($assembly in @($pluginDll, $pluginCoreDll, $mcpExe, $mcpDll)) {
    Assert-FileVersion $assembly
}

$pluginTarget = Join-Path $pluginStage "BepInEx\plugins\SagaFrontierBridge"
New-Item -ItemType Directory -Force -Path $pluginTarget | Out-Null

Copy-RequiredFile $pluginDll $pluginTarget
Copy-RequiredFile $pluginCoreDll $pluginTarget
Copy-RequiredFile (Join-Path $root "README.md") $pluginStage
Copy-RequiredFile (Join-Path $root "LICENSE") $pluginStage

$mcpFiles = @(
    "SagaFrontierBridge.Mcp.exe",
    "SagaFrontierBridge.Mcp.dll",
    "SagaFrontierBridge.Mcp.deps.json",
    "SagaFrontierBridge.Mcp.runtimeconfig.json",
    "SagaFrontierBridge.Core.dll"
)

foreach ($name in $mcpFiles) {
    Copy-RequiredFile (Join-Path $mcpBuild $name) $mcpStage
}

Copy-RequiredFile (Join-Path $root "docs\mcp.md") (Join-Path $mcpStage "MCP.md")
Copy-RequiredFile (Join-Path $root "LICENSE") $mcpStage

Compress-Archive -Path (Join-Path $pluginStage "*") -DestinationPath $pluginZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $mcpStage "*") -DestinationPath $mcpZip -CompressionLevel Optimal

Assert-ZipContents $pluginZip @(
    "BepInEx/plugins/SagaFrontierBridge/SagaFrontierBridge.dll",
    "BepInEx/plugins/SagaFrontierBridge/SagaFrontierBridge.Core.dll",
    "README.md",
    "LICENSE"
)

Assert-ZipContents $mcpZip @(
    "SagaFrontierBridge.Mcp.exe",
    "SagaFrontierBridge.Mcp.dll",
    "SagaFrontierBridge.Mcp.deps.json",
    "SagaFrontierBridge.Mcp.runtimeconfig.json",
    "SagaFrontierBridge.Core.dll",
    "MCP.md",
    "LICENSE"
)

$checksumLines = foreach ($archive in @($pluginZip, $mcpZip) | Sort-Object) {
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, (Split-Path -Leaf $archive)
}

$checksumText = ($checksumLines -join "`n") + "`n"
[System.IO.File]::WriteAllText($checksumPath, $checksumText, [System.Text.Encoding]::ASCII)

Remove-Item -LiteralPath $workRoot -Recurse -Force

Write-Host ""
Write-Host "Release packages:"
Write-Host "  $pluginZip"
Write-Host "  $mcpZip"
Write-Host "  $checksumPath"
