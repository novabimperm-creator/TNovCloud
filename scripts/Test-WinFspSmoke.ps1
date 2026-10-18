[CmdletBinding()]
param(
    [string] $DriveLetter,
    [string] $ApiUrl = 'http://127.0.0.1:5080',
    [string] $ConnectionString = 'Host=127.0.0.1;Port=55432;Database=tnovcloud;Username=tnovcloud'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FreeDriveLetter {
    $used = [IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1).ToUpperInvariant() }
    foreach ($code in @(84..90 + 68..83)) {
        $letter = [string][char]$code
        if ($used -notcontains $letter) {
            return $letter
        }
    }
    throw 'No free drive letter is available for the WinFsp smoke test.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$smokeRoot = Join-Path $env:TEMP ("TNovCloud.Smoke." + [guid]::NewGuid().ToString('N'))
$apiData = Join-Path $smokeRoot 'api-data'
$hostRoot = Join-Path $smokeRoot 'host'
New-Item $smokeRoot, $apiData, $hostRoot -ItemType Directory -Force | Out-Null

$api = $null
$hostProcess = $null
Get-Process TNovCloud.Client,TNovCloud.FileSystemHost -ErrorAction SilentlyContinue |
    Stop-Process -Force
Start-Sleep -Seconds 1
if (-not $DriveLetter) {
    $DriveLetter = Get-FreeDriveLetter
}
try {
    $env:ConnectionStrings__Postgres = $ConnectionString
    $env:Jwt__Key = 'change-this-to-a-long-random-secret-key!'
    $env:Storage__Root = $apiData
    $env:ASPNETCORE_URLS = $ApiUrl
    $apiOutput = Join-Path $repositoryRoot 'src\TNovCloud.Api\bin\Release\net10.0'
    $apiExe = Join-Path $apiOutput 'TNovCloud.Api.exe'
    if (-not (Test-Path $apiExe)) {
        throw "API was not built at $apiExe."
    }
    $apiLog = Join-Path $smokeRoot 'api.log'
    $api = Start-Process $apiExe `
        -WorkingDirectory $apiOutput `
        -RedirectStandardOutput $apiLog `
        -RedirectStandardError (Join-Path $smokeRoot 'api.err.log') `
        -PassThru `
        -NoNewWindow

    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "$ApiUrl/health" -TimeoutSec 2
            if ($health.status -eq 'ok') {
                $ready = $true
                break
            }
        } catch {
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        throw "API did not become ready at $ApiUrl. Log: $(Get-Content $apiLog -Raw -ErrorAction SilentlyContinue)"
    }

    $login = Invoke-RestMethod -Uri "$ApiUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body '{"login":"admin","password":"admin"}'
    $uploadFile = Join-Path $smokeRoot 'cached.txt'
    Set-Content -Path $uploadFile -Value 'cached-content' -Encoding ascii -NoNewline
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    $uploadOutput = & $curl -sS -w '%{http_code}' `
        -X POST "$ApiUrl/api/fs/upload?path=Projects/cached.txt" `
        -H "Authorization: Bearer $($login.token)" `
        -F "file=@$uploadFile;filename=cached.txt"
    if ($uploadOutput -notmatch '200$') {
        throw "Upload failed: $uploadOutput"
    }

    $session = @{
        ServerUrl = $ApiUrl
        DriveLetter = $DriveLetter
        Token = $login.token
        User = $login.user
    } | ConvertTo-Json -Depth 6
    $sessionPath = Join-Path $hostRoot 'session.json'
    Set-Content -Path $sessionPath -Value $session -Encoding UTF8

    $hostCandidates = @(
        (Join-Path $repositoryRoot 'src\TNovCloud.FileSystemHost\bin\Release\net10.0-windows\win-x64\TNovCloud.FileSystemHost.exe'),
        (Join-Path $repositoryRoot 'src\TNovCloud.FileSystemHost\bin\x64\Release\net10.0-windows\win-x64\TNovCloud.FileSystemHost.exe')
    )
    $hostExe = $hostCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $hostExe) {
        throw "FileSystemHost was not built. Expected one of: $($hostCandidates -join ', ')."
    }

    $hostProcess = Start-Process $hostExe -ArgumentList @('run','--session',$sessionPath,'--data-root',$hostRoot,'--drive',$DriveLetter) -PassThru -WindowStyle Hidden
    $root = "${DriveLetter}:\"
    $appeared = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (Test-Path $root) {
            $appeared = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $appeared) {
        throw "WinFsp volume $root did not appear. See $(Join-Path $hostRoot 'FileSystemHost\host.log')."
    }

    $cached = Join-Path $root 'Projects\cached.txt'
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $cached) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path $cached)) {
        throw "Cached file was not listed on $root."
    }

    $online = Get-Content $cached -Raw
    if ($online.Trim() -ne 'cached-content') {
        throw "Unexpected online cache content: $online"
    }

    Stop-Process -Id $api.Id -Force
    $api = $null
    Start-Sleep -Seconds 1
    $offline = Get-Content $cached -Raw
    if ($offline.Trim() -ne 'cached-content') {
        throw 'Offline cache read failed after the API was stopped.'
    }

    Write-Host "[OK] WinFsp volume $root listed and served a cached file without the API."
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Get-Process TNovCloud.FileSystemHost -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
