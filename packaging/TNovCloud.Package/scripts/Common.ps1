Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WindowsSdkTool {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    $kitsRoot = (Get-ItemProperty `
        'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' `
        -ErrorAction Stop).KitsRoot10
    $candidates = Get-ChildItem -Path (Join-Path $kitsRoot 'bin') -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
        Where-Object { Test-Path $_ }
    $tool = $candidates | Select-Object -First 1
    if (-not $tool) {
        throw "$Name was not found. Install the Windows 10/11 SDK."
    }
    return $tool
}

function Get-VsMsBuild {
    $vsWhere = Join-Path ${env:ProgramFiles(x86)} `
        'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vsWhere)) {
        throw 'vswhere.exe was not found. Install Visual Studio Build Tools with Desktop development with C++.'
    }

    $installation = & $vsWhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if (-not $installation) {
        throw 'Visual Studio C++ x64 build tools were not found.'
    }

    $msBuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path $msBuild)) {
        throw "MSBuild was not found at $msBuild."
    }
    return $msBuild
}

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string] $Operation)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}
