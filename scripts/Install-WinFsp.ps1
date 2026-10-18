[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = '2.2.26215'
$expectedHash = '2ECB5C89405488A95BBD8A01875E02C48534FD37BBDFD84488F7590464D65944'
$uri = "https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-$version.msi"
$fsptool = "${env:ProgramFiles(x86)}\WinFsp\bin\fsptool-x64.exe"

if (Test-Path $fsptool) {
    $installed = (& $fsptool ver 2>$null | Out-String).Trim()
    if ($installed.StartsWith('2.2')) {
        Write-Host "[OK] $installed"
        return
    }
}

$installer = Join-Path $env:TEMP "winfsp-$version.msi"
try {
    Invoke-WebRequest -Uri $uri -OutFile $installer
    $actualHash = (Get-FileHash $installer -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "WinFsp installer hash mismatch: $actualHash"
    }

    $process = Start-Process msiexec.exe `
        -Verb RunAs `
        -ArgumentList '/i', "`"$installer`"", '/qn', '/norestart' `
        -Wait `
        -PassThru
    if ($process.ExitCode -notin 0, 3010) {
        throw "WinFsp installer failed with exit code $($process.ExitCode)."
    }
}
finally {
    Remove-Item $installer -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $fsptool) -or
    -not ((& $fsptool ver 2>$null | Out-String).Trim().StartsWith('2.2'))) {
    throw 'WinFsp 2.2 was not detected after installation.'
}

Write-Host "[OK] WinFsp $version installed."
