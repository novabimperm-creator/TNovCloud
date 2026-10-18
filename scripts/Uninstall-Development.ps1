[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$removePackage = Join-Path $repositoryRoot `
    'packaging\TNovCloud.Package\scripts\Remove-Package.ps1'

Get-Process TNovCloud.Client -ErrorAction SilentlyContinue | Stop-Process -Force
$host = Join-Path $env:LOCALAPPDATA `
    'Programs\TNovCloud\App\TNovCloud.FileSystemHost.exe'
if (Test-Path $host) {
    & $host stop | Out-Null
}

& $removePackage
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\TNovCloud') `
    -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'TNovCloud development installation removed.'
