[CmdletBinding()]
param(
    [string] $PackagePath,
    [string] $ExternalLocation,
    [string] $DriveLetter,
    [string] $RootPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $PackagePath) {
    $PackagePath = Join-Path $packageRoot 'artifacts\TNovCloud.Package.msix'
}
if (-not $ExternalLocation) {
    $ExternalLocation = Join-Path $packageRoot 'artifacts\ExternalLocation'
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$ExternalLocation = [IO.Path]::GetFullPath($ExternalLocation)

if (-not (Test-Path $PackagePath -PathType Leaf)) {
    throw "Package not found: $PackagePath"
}
if (-not (Test-Path (Join-Path $ExternalLocation 'TNovCloud.ShellExtension.dll'))) {
    throw "Shell extension not found in external location: $ExternalLocation"
}
$clientPath = Join-Path $ExternalLocation 'TNovCloud.Client.exe'
if (-not (Test-Path $clientPath)) {
    throw "Client executable not found in external location: $clientPath"
}

$sessionPath = Join-Path $env:LOCALAPPDATA 'TNovCloud\session.json'
if ((-not $DriveLetter) -and (Test-Path $sessionPath)) {
    $session = Get-Content $sessionPath -Raw | ConvertFrom-Json
    $DriveLetter = $session.DriveLetter
}
if (-not $DriveLetter) {
    $DriveLetter = 'T'
}
$DriveLetter = $DriveLetter.Substring(0, 1).ToUpperInvariant()
if ($DriveLetter -notmatch '^[A-Z]$') {
    throw "Invalid drive letter: $DriveLetter"
}
if (-not $RootPath) {
    $RootPath = Join-Path $env:LOCALAPPDATA 'TNovCloud\Drive'
}
$RootPath = [IO.Path]::GetFullPath($RootPath)

if (-not ('TNovCloud.Package.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace TNovCloud.Package
{
    public static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeInformation(
            string rootPathName,
            IntPtr volumeNameBuffer,
            uint volumeNameSize,
            out uint volumeSerialNumber,
            IntPtr maximumComponentLength,
            IntPtr fileSystemFlags,
            IntPtr fileSystemNameBuffer,
            uint fileSystemNameSize);
    }
}
'@
}

$volumeSerial = [uint32] 0
$driveRoot = "${DriveLetter}:\"
$volumeAvailable = [TNovCloud.Package.NativeMethods]::GetVolumeInformation(
    $driveRoot,
    [IntPtr]::Zero,
    0,
    [ref] $volumeSerial,
    [IntPtr]::Zero,
    [IntPtr]::Zero,
    [IntPtr]::Zero,
    0)
if (-not $volumeAvailable) {
    Write-Warning "TNovCloud drive $driveRoot is not mounted yet; volume serial will be recorded by the client."
}

$mountKey = 'HKCU:\Software\TNovCloud\Mount'
New-Item -Path $mountKey -Force | Out-Null
New-ItemProperty -Path $mountKey -Name DriveLetter `
    -Value $DriveLetter -PropertyType String -Force | Out-Null
New-ItemProperty -Path $mountKey -Name RootPath `
    -Value $RootPath -PropertyType String -Force | Out-Null
if ($volumeAvailable) {
    New-ItemProperty -Path $mountKey -Name VolumeSerial `
        -Value $volumeSerial -PropertyType DWord -Force | Out-Null
} else {
    Remove-ItemProperty -Path $mountKey -Name VolumeSerial -ErrorAction SilentlyContinue
}
New-ItemProperty -Path $mountKey -Name ClientPath `
    -Value $clientPath -PropertyType String -Force | Out-Null

try {
    Add-AppxPackage -Path $PackagePath `
        -ExternalLocation $ExternalLocation `
        -ForceApplicationShutdown `
        -ErrorAction Stop
}
catch {
    Remove-Item $mountKey -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host 'TNovCloud sparse package registered for the current user.'
Write-Host 'Restart File Explorer or sign out/in before testing the context menu.'
