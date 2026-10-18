[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packages = Get-AppxPackage -Name 'TNovCloud.Package' -ErrorAction SilentlyContinue
foreach ($package in $packages) {
    Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
}

Remove-Item 'HKCU:\Software\TNovCloud\Mount' `
    -Recurse -Force -ErrorAction SilentlyContinue

if ($packages) {
    Write-Host 'TNovCloud sparse package removed for the current user.'
}
else {
    Write-Host 'TNovCloud sparse package was not registered for the current user.'
}
