[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f ]{40,}$')]
    [string] $Thumbprint,

    [string] $PackagePath,

    [string] $TimestampUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$packageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $PackagePath) {
    $PackagePath = Join-Path $packageRoot 'artifacts\TNovCloud.Package.msix'
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path $PackagePath -PathType Leaf)) {
    throw "Package not found: $PackagePath"
}

$normalizedThumbprint = $Thumbprint.Replace(' ', '')
$certificate = Get-Item "Cert:\CurrentUser\My\$normalizedThumbprint" `
    -ErrorAction SilentlyContinue
if (-not $certificate) {
    throw "Certificate $normalizedThumbprint was not found in Cert:\CurrentUser\My."
}

$signTool = Get-WindowsSdkTool 'signtool.exe'
$arguments = @(
    'sign',
    '/sha1', $normalizedThumbprint,
    '/s', 'My',
    '/fd', 'SHA256'
)
if ($TimestampUrl) {
    $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
}
$arguments += $PackagePath

& $signTool @arguments
Assert-LastExitCode 'MSIX package signing'
& $signTool verify /pa /v $PackagePath
Assert-LastExitCode 'MSIX signature verification'
