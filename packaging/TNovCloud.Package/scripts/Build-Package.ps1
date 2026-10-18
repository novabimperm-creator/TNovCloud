[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.0.0',

    [string] $ClientPath,

    [string] $HostPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$packageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repositoryRoot `
    'src\TNovCloud.ShellExtension\TNovCloud.ShellExtension.vcxproj'
$artifacts = Join-Path $packageRoot 'artifacts'
$layout = Join-Path $artifacts 'PackageLayout'
$external = Join-Path $artifacts 'ExternalLocation'
$package = Join-Path $artifacts 'TNovCloud.Package.msix'

$msBuild = Get-VsMsBuild
& $msBuild $project /m /restore `
    "/p:Configuration=$Configuration" /p:Platform=x64
Assert-LastExitCode 'Shell extension build'

$extensionDll = Join-Path (Split-Path $project) `
    "bin\x64\$Configuration\TNovCloud.ShellExtension.dll"
if (-not (Test-Path $extensionDll)) {
    throw "The shell extension was not produced at $extensionDll."
}

if (-not $ClientPath) {
    $clientOutput = Join-Path $repositoryRoot `
        "src\TNovCloud.Client\bin\x64\$Configuration\net10.0-windows\win-x64"
    $ClientPath = Join-Path $clientOutput 'TNovCloud.Client.exe'
}
$ClientPath = [IO.Path]::GetFullPath($ClientPath)
if (-not (Test-Path $ClientPath -PathType Leaf)) {
    throw "TNovCloud.Client.exe was not found at $ClientPath. Build/publish the client or pass -ClientPath."
}
if (-not $HostPath) {
    $hostOutput = Join-Path $repositoryRoot `
        "src\TNovCloud.FileSystemHost\bin\$Configuration\net10.0-windows\win-x64"
    $HostPath = Join-Path $hostOutput 'TNovCloud.FileSystemHost.exe'
}
$HostPath = [IO.Path]::GetFullPath($HostPath)
if (-not (Test-Path $HostPath -PathType Leaf)) {
    throw "TNovCloud.FileSystemHost.exe was not found at $HostPath. Build/publish the host or pass -HostPath."
}

Remove-Item $layout, $external -Recurse -Force -ErrorAction SilentlyContinue
New-Item $layout, $external -ItemType Directory -Force | Out-Null

[xml] $manifest = Get-Content (Join-Path $packageRoot 'AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Save((Join-Path $layout 'AppxManifest.xml'))

& (Join-Path $PSScriptRoot 'New-LogoAssets.ps1') `
    -OutputDirectory (Join-Path $layout 'Assets')

$clientDirectory = Split-Path $ClientPath
Copy-Item (Join-Path $clientDirectory '*') $external -Recurse -Force
$hostDirectory = Split-Path $HostPath
Copy-Item (Join-Path $hostDirectory '*') $external -Recurse -Force
Copy-Item $extensionDll $external -Force

$makeAppx = Get-WindowsSdkTool 'makeappx.exe'
Remove-Item $package -Force -ErrorAction SilentlyContinue
& $makeAppx pack /d $layout /p $package /o /nv
Assert-LastExitCode 'MSIX package build'

Write-Host "Package:          $package"
Write-Host "External content: $external"
Write-Host 'Sign the package before registration.'
