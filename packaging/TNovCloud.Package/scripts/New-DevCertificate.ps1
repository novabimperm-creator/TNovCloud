[CmdletBinding()]
param(
    [string] $Subject = 'CN=TNovCloud.Dev',
    [int] $ValidYears = 2,
    [SecureString] $PfxPassword,
    [switch] $Trust
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$certificateDirectory = Join-Path $packageRoot 'artifacts\Certificate'
New-Item -ItemType Directory -Force -Path $certificateDirectory | Out-Null

$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -FriendlyName 'TNovCloud development package signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -TextExtension @(
        '2.5.29.19={critical}{text}ca=false',
        '2.5.29.37={critical}{text}1.3.6.1.5.5.7.3.3'
    )

$cerPath = Join-Path $certificateDirectory 'TNovCloud.Dev.cer'
Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null

if ($PfxPassword) {
    $pfxPath = Join-Path $certificateDirectory 'TNovCloud.Dev.pfx'
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath `
        -Password $PfxPassword -Force | Out-Null
    Write-Host "PFX:        $pfxPath"
}

if ($Trust) {
    foreach ($store in @('TrustedPeople', 'Root')) {
        $process = Start-Process certutil.exe `
            -Verb RunAs `
            -ArgumentList '-addstore', '-f', $store, "`"$cerPath`"" `
            -Wait `
            -PassThru
        if ($process.ExitCode -ne 0) {
            throw "Could not trust the development certificate in $store (certutil exit $($process.ExitCode))."
        }
    }
}

Write-Host "Certificate: $cerPath"
Write-Host "Thumbprint:  $($certificate.Thumbprint)"
if (-not $Trust) {
    Write-Host 'The certificate was not added to Trusted People. Use -Trust only on a development machine.'
}
