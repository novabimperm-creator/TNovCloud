[CmdletBinding()]
param(
    [string] $DriveLetter = 'T',
    [string] $Version = '1.0.0.0',
    [switch] $ReuseCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageScripts = Join-Path $repositoryRoot 'packaging\TNovCloud.Package\scripts'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\TNovCloud'
$appRoot = Join-Path $installRoot 'App'
$packageRoot = Join-Path $installRoot 'Package'

& (Join-Path $PSScriptRoot 'Install-WinFsp.ps1')
& (Join-Path $PSScriptRoot 'Test-Prerequisites.ps1')
if ($LASTEXITCODE -ne 0) {
    throw 'Install the missing prerequisites before building the native shell extension.'
}

Get-Process TNovCloud.Client,TNovCloud.FileSystemHost -ErrorAction SilentlyContinue |
    Stop-Process -Force
Remove-Item $appRoot,$packageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item $appRoot,$packageRoot -ItemType Directory -Force | Out-Null

$clientProject = Join-Path $repositoryRoot 'src\TNovCloud.Client\TNovCloud.Client.csproj'
$hostProject = Join-Path $repositoryRoot 'src\TNovCloud.FileSystemHost\TNovCloud.FileSystemHost.csproj'
dotnet publish $clientProject -c Release -r win-x64 --self-contained false -o $appRoot
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
dotnet publish $hostProject -c Release -r win-x64 --self-contained false -o $appRoot
if ($LASTEXITCODE -ne 0) { throw 'File system host publish failed.' }

& (Join-Path $packageScripts 'Build-Package.ps1') `
    -Configuration Release `
    -Version $Version `
    -ClientPath (Join-Path $appRoot 'TNovCloud.Client.exe') `
    -HostPath (Join-Path $appRoot 'TNovCloud.FileSystemHost.exe')

$artifactRoot = Join-Path $repositoryRoot 'packaging\TNovCloud.Package\artifacts'
$certificate = Get-ChildItem 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq 'CN=TNovCloud.Dev' -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate -or -not $ReuseCertificate) {
    & (Join-Path $packageScripts 'New-DevCertificate.ps1') -Trust
    $certificate = Get-ChildItem 'Cert:\CurrentUser\My' |
        Where-Object { $_.Subject -eq 'CN=TNovCloud.Dev' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

$builtPackage = Join-Path $artifactRoot 'TNovCloud.Package.msix'
& (Join-Path $packageScripts 'Sign-Package.ps1') `
    -Thumbprint $certificate.Thumbprint `
    -PackagePath $builtPackage

Copy-Item $builtPackage (Join-Path $packageRoot 'TNovCloud.Package.msix') -Force
Copy-Item (Join-Path $artifactRoot 'ExternalLocation\*') $appRoot -Recurse -Force

& (Join-Path $packageScripts 'Register-Package.ps1') `
    -PackagePath (Join-Path $packageRoot 'TNovCloud.Package.msix') `
    -ExternalLocation $appRoot `
    -DriveLetter $DriveLetter `
    -RootPath "${DriveLetter}:\"

Start-Process (Join-Path $appRoot 'TNovCloud.Client.exe')
Write-Host "TNovCloud installed at $installRoot"
Write-Host 'Sign in to mount the WinFsp volume; Explorer commands become visible after the mount succeeds.'
