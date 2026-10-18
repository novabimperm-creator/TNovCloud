[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$failed = $false

function Test-Command([string]$Name, [string]$Hint) {
    if (Get-Command $Name -ErrorAction SilentlyContinue) {
        Write-Host "[OK] $Name"
    } else {
        Write-Warning "$Name was not found. $Hint"
        $script:failed = $true
    }
}

Test-Command dotnet 'Install the .NET 10 SDK.'

$dotnetVersion = & dotnet --version
if (-not $dotnetVersion.StartsWith('10.')) {
    Write-Warning ".NET 10 SDK is required; found $dotnetVersion."
    $failed = $true
}

$winFsp = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\WinFsp' -ErrorAction SilentlyContinue
if (-not $winFsp) {
    $winFsp = Get-ItemProperty 'HKLM:\SOFTWARE\WinFsp' -ErrorAction SilentlyContinue
}
if ($winFsp) {
    $fsptool = "${env:ProgramFiles(x86)}\WinFsp\bin\fsptool-x64.exe"
    $winFspVersion = if (Test-Path $fsptool) {
        (& $fsptool ver 2>$null | Out-String).Trim()
    } else {
        ''
    }
    if ($winFspVersion.StartsWith('2.2')) {
        Write-Host "[OK] $winFspVersion"
    } else {
        Write-Warning "WinFsp 2.2.26215 Beta4 is required; found $winFspVersion."
        $failed = $true
    }
} else {
    Write-Warning 'WinFsp runtime is not installed. Run scripts/Install-WinFsp.ps1.'
    $failed = $true
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vs) {
    Write-Warning 'MSVC x64 was not found. Install Visual Studio Build Tools with the VCTools workload.'
    $failed = $true
} else {
    Write-Host "[OK] MSVC: $vs"
}

$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$sdk = Get-ChildItem $sdkRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($sdk) {
    Write-Host "[OK] Windows SDK: $($sdk.Name)"
} else {
    Write-Warning 'Windows SDK with MakeAppx and SignTool was not found.'
    $failed = $true
}

if ($failed) {
    exit 1
}

Write-Host 'All prerequisites are installed.'
